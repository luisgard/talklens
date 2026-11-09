using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using TalkLens.Core;

namespace TalkLens.Ai;

public interface IChatModel
{
    string Name { get; }
    /// <returns>The reply text, or null if the model declined.</returns>
    Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct);

    /// <summary>For a short reply someone is waiting on, repeated many times with the same long system prompt.</summary>
    Task<string?> CompleteQuickAsync(string system, string user, int maxTokens, CancellationToken ct) => CompleteAsync(system, user, null, maxTokens, ct);
}

/// <summary>The configured cloud models. Null means that capability runs offline only.</summary>
public sealed class AiModels
{
    public IChatModel? Chat { get; init; }
    public IChatModel? Vision { get; init; }
    public IEmbeddingModel? Embeddings { get; init; }

    public static AiModels Create(AiOptions options, HttpClient http)
    {
        var chatProvider = Resolve(options.ChatProvider, ["openai", "anthropic", "groq"]);
        var visionProvider = Resolve(options.VisionProvider, ["openai", "anthropic"]);
        return new AiModels
        {
            Chat = Build(chatProvider, options.ChatModel, options, http),
            Vision = Build(visionProvider, options.VisionModel, options, http),
            Embeddings = Resolve(options.EmbeddingProvider, ["openai"]) == "openai" && AiOptions.Key("openai") is { } key
                ? new OpenAiEmbeddings(http, options.OpenAiBaseUrl, key, options.OpenAiEmbeddingModel, options.EmbeddingDimensions)
                : null,
        };
    }

    private static string? Resolve(string configured, string[] autoOrder) => configured switch
    {
        "auto" => autoOrder.FirstOrDefault(p => AiOptions.Key(p) != null),
        "none" => null,
        _ => configured,
    };

    private static IChatModel? Build(string? provider, string? model, AiOptions options, HttpClient http)
    {
        var key = provider == null ? null : AiOptions.Key(provider);
        if (key == null) return null;
        return provider switch
        {
            "anthropic" => new AnthropicChat(key, model ?? "claude-opus-5-5"),
            "openai" => new OpenAiCompatibleChat(http, options.OpenAiBaseUrl, key, model ?? "gpt-6-luna", "openai"),
            "groq" => new OpenAiCompatibleChat(http, options.GroqBaseUrl, key, model ?? "llama-3.3-70b-versatile", "groq"),
            _ => throw new InvalidOperationException($"Unknown AI provider '{provider}'"),
        };
    }
}

/// <summary>Chat Completions wire format, shared by OpenAI and Groq.</summary>
public sealed class OpenAiCompatibleChat(HttpClient http, string baseUrl, string apiKey, string model, string provider) : IChatModel
{
    public string Name => $"{provider}/{model}";

    public async Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
    {
        object content = jpeg == null ? user : new object[]
        {
            new { type = "text", text = user },
            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                max_completion_tokens = maxTokens,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{provider} returned {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var choice = doc.RootElement.GetProperty("choices")[0];
        return choice.GetProperty("message").TryGetProperty("content", out var text) ? text.GetString() : null;
    }
}

/// <summary>Claude through the official SDK. Low effort keeps answers inside the 10 second target (RNF-05).</summary>
public sealed class AnthropicChat(string apiKey, string model) : IChatModel
{
    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public string Name => $"anthropic/{model}";

    public async Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
    {
        var content = new List<BetaContentBlockParam>();
        if (jpeg != null)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg },
            });
        }
        content.Add(new BetaTextBlockParam { Text = user });

        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            // Thinking counts against this limit, so leave room beyond the visible reply.
            MaxTokens = maxTokens + 2000,
            System = system,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = content }],
        }, ct);

        return TextOf(response);
    }

    public async Task<string?> CompleteQuickAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            // Thinking was most of the wait: about 2 s against 1 s for the same short reply.
            Thinking = new BetaThinkingConfigDisabled(),
            Messages = [new BetaUserMessageParam { Content = user }],
        }, ct);
        return TextOf(response);
    }

    private static string? TextOf(BetaMessage response)
    {
        if (response.StopReason == BetaStopReason.Refusal) return null;
        var parts = response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(b => b.Text);
        return string.Concat(parts);
    }
}
