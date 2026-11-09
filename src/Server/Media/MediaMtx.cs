using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TalkLens.Core;

namespace TalkLens.Media;

public sealed record StreamInfo(bool Ready, int Readers, long BytesReceived, string Tracks);

/// <summary>Owns the MediaMTX child process. It relays WHIP to WHEP without re-encoding (RF-01, RNF-07).</summary>
public sealed class MediaMtx(ILogger<MediaMtx> log) : BackgroundService
{
    public const string HttpBase = "http://127.0.0.1:8889";
    public const string RtspBase = "rtsp://127.0.0.1:8554";
    public const string HlsBase = "http://127.0.0.1:8888";
    private const string ApiBase = "http://127.0.0.1:9997";

    private const string Config = """
        logLevel: warn
        api: true
        apiAddress: 127.0.0.1:9997
        rtsp: true
        rtspTransports: [tcp]
        rtspAddress: 127.0.0.1:8554
        rtmp: false
        hls: true
        hlsAddress: 127.0.0.1:8888
        hlsAlwaysRemux: true
        hlsVariant: fmp4
        hlsSegmentCount: 60
        hlsSegmentDuration: 3s
        srt: false
        webrtc: true
        webrtcAddress: 127.0.0.1:8889
        webrtcLocalUDPAddress: :8189
        webrtcLocalTCPAddress: :8189
        paths:
          "~^live/r[0-9]+/(cam|slides)$":
        """;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1) };

    public static string CameraPath(long room) => $"live/r{room}/cam";
    public static string SlidesPath(long room) => $"live/r{room}/slides";
    private Process? _process;

    public string BinaryPath => Path.Combine(Paths.Tools, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mediamtx.exe" : "mediamtx");
    public bool Running => _process is { HasExited: false };
    public bool Installed => File.Exists(BinaryPath);

    public async Task<IReadOnlyDictionary<string, StreamInfo>> GetStreamsAsync(CancellationToken ct)
    {
        var streams = new Dictionary<string, StreamInfo>();
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{ApiBase}/v3/paths/list", ct));
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var ready = (item.TryGetProperty("online", out var online) && online.GetBoolean())
                    || (item.TryGetProperty("ready", out var legacy) && legacy.GetBoolean());
                var readers = item.TryGetProperty("readers", out var r) ? r.GetArrayLength() : 0;
                var bytes = item.TryGetProperty("bytesReceived", out var b) ? b.GetInt64() : 0;
                var tracks = item.TryGetProperty("tracks", out var t) && t.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", t.EnumerateArray().Select(x => x.GetString()))
                    : "";
                streams[item.GetProperty("name").GetString()!] = new StreamInfo(ready, readers, bytes, tracks);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogDebug(e, "MediaMTX API unavailable");
        }
        return streams;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Installed)
        {
            log.LogError("MediaMTX not found at {Path}. Run the start script, which downloads it.", BinaryPath);
            return;
        }
        Directory.CreateDirectory(Paths.Data);
        var configPath = Path.Combine(Paths.Data, "mediamtx.yml");
        await File.WriteAllTextAsync(configPath, Config, stop);
        KillStale();

        while (!stop.IsCancellationRequested)
        {
            var info = new ProcessStartInfo(BinaryPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(configPath);
            _process = Process.Start(info);
            await File.WriteAllTextAsync(PidFile, _process!.Id.ToString(), CancellationToken.None);
            try
            {
                await _process.WaitForExitAsync(stop);
                log.LogWarning("MediaMTX exited with code {Code}; restarting", _process.ExitCode);
                await Task.Delay(1000, stop);
            }
            catch (OperationCanceledException)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return;
            }
        }
    }

    private static string PidFile => Path.Combine(Paths.Data, "mediamtx.pid");

    /// <summary>A hard crash of the server can leave MediaMTX holding its ports.</summary>
    private static void KillStale()
    {
        if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile), out var pid)) return;
        try
        {
            using var stale = Process.GetProcessById(pid);
            if (stale.ProcessName.Equals("mediamtx", StringComparison.OrdinalIgnoreCase)) stale.Kill();
        }
        catch (ArgumentException) { }
    }
}
