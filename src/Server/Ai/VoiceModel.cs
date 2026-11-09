using System.Net.Http.Headers;
using System.Net.Http.Json;
using TalkLens.Core;

namespace TalkLens.Ai;

/// <summary>RF-11. Optional better voices from the cloud. Without one, the attendee's phone reads the translation with its own voice, for free.</summary>
public sealed class VoiceModel(HttpClient http, AiOptions options, ILogger<VoiceModel> log)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public bool Available => options.VoiceProvider == "openai" && AiOptions.Key("openai") != null;

    public async Task<byte[]?> SpeakAsync(string text, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.OpenAiBaseUrl}/audio/speech")
            {
                Content = JsonContent.Create(new { model = options.OpenAiVoiceModel, voice = options.OpenAiVoice, input = text, response_format = "mp3" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiOptions.Key("openai"));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(timeout.Token);
            log.LogWarning("Voice model answered {Status}", (int)response.StatusCode);
            return null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Voice model failed: {Type}", e.GetType().Name);
            return null;
        }
    }
}
