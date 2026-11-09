namespace TalkLens.Core;

public sealed class AppOptions
{
    public NetworkOptions Network { get; set; } = new();
    public AiOptions Ai { get; set; } = new();
    public MailOptions Mail { get; set; } = new();
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string TesseractPath { get; set; } = "tesseract";
    public string TesseractLangs { get; set; } = "eng+spa";
    /// <summary>Languages offered for caption translation (RF-10).</summary>
    public string[] Languages { get; set; } = ["es", "en"];
}

public sealed class NetworkOptions
{
    /// <summary>Attendee app. Plain HTTP so phones never see a certificate warning. Use 80 for the captive portal.</summary>
    public int HttpPort { get; set; } = 8080;
    /// <summary>Camera and operator pages. Browsers only allow camera access over HTTPS.</summary>
    public int HttpsPort { get; set; } = 8443;
    /// <summary>LAN address printed in QR codes. Empty means auto-detect.</summary>
    public string PublicHost { get; set; } = "";
    public string WifiSsid { get; set; } = "";
    public string WifiPassword { get; set; } = "";
}

public sealed class AiOptions
{
    /// <summary>openai | groq | local</summary>
    public string TranscriptionProvider { get; set; } = "openai";
    public string OpenAiTranscriptionModel { get; set; } = "gpt-live-transcribe";
    public string GroqTranscriptionModel { get; set; } = "whisper-large-v3-turbo";
    public double GroqBlockSeconds { get; set; } = 2;

    /// <summary>auto | openai | anthropic | groq | none</summary>
    public string ChatProvider { get; set; } = "auto";
    public string? ChatModel { get; set; }
    /// <summary>auto | openai | anthropic | none</summary>
    public string VisionProvider { get; set; } = "auto";
    public string? VisionModel { get; set; }

    /// <summary>none means each phone reads translations with its own voice. openai uses the model below.</summary>
    public string VoiceProvider { get; set; } = "none";
    public string OpenAiVoiceModel { get; set; } = "gpt-4o-mini-tts";
    public string OpenAiVoice { get; set; } = "alloy";

    /// <summary>Search by meaning. auto | openai | none. Without it, search uses words alone.</summary>
    public string EmbeddingProvider { get; set; } = "auto";
    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; set; } = 512;
    /// <summary>Search uses Elasticsearch whenever it answers here, and SQLite otherwise. Empty turns it off. See elasticsearch/docker-compose.yml.</summary>
    public string ElasticsearchUrl { get; set; } = "http://127.0.0.1:9200";

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string OpenAiRealtimeUrl { get; set; } = "wss://api.openai.com/v1/realtime?intent=transcription";
    /// <summary>Any HTTP response from this URL counts as "internet is up".</summary>
    public string ConnectivityProbeUrl { get; set; } = "https://api.openai.com/v1/models";

    public double AnswerTimeoutSeconds { get; set; } = 9.5;
    public double CloudStallSeconds { get; set; } = 6;

    public static string? Key(string provider) => provider switch
    {
        "openai" => Env("OPENAI_API_KEY"),
        "groq" => Env("GROQ_API_KEY"),
        "anthropic" => Env("ANTHROPIC_API_KEY"),
        _ => null,
    };

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public static class Paths
{
    public static string Root { get; } = FindRoot();
    public static string Data => Path.Combine(Root, "data");
    public static string Tools => Path.Combine(Root, "tools");
    public static string Models => Path.Combine(Root, "models");

    private static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("TALKLENS_ROOT");
        if (!string.IsNullOrEmpty(configured)) return configured;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "start.ps1"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Loads KEY=VALUE lines from .env without overriding real environment variables.</summary>
    public static void LoadDotEnv()
    {
        var file = Path.Combine(Root, ".env");
        if (!File.Exists(file)) return;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            var key = line[..eq].Trim();
            if (Environment.GetEnvironmentVariable(key) == null)
                Environment.SetEnvironmentVariable(key, line[(eq + 1)..].Trim().Trim('"'));
        }
    }
}
