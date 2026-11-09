using System.Diagnostics;

namespace TalkLens.Media;

/// <summary>Runs ffmpeg, slices stdout into fixed-size frames, and restarts when the source drops.</summary>
public sealed class FfmpegPipe(string ffmpegPath, IReadOnlyList<string> args, int frameBytes, Action<byte[]> onFrame, ILogger log)
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private Process? _process;

    public bool Running { get; private set; }
    public int Restarts { get; private set; }
    public DateTime LastFrameUtc { get; private set; }
    public string LastError { get; private set; } = "";

    public void Start() => _loop = Task.Run(LoopAsync);

    public async Task StopAsync()
    {
        _stop.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (_loop != null) await _loop;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (EndOfStreamException)
            {
                // The source went away; ffmpeg restarts below.
            }
            catch (Exception e)
            {
                log.LogWarning(e, "ffmpeg pipe failed");
            }
            if (_stop.IsCancellationRequested) return;
            Restarts++;
            try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync()
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        _process = process;
        Running = true;
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.Length > 0) LastError = line;
        });

        var stdout = process.StandardOutput.BaseStream;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = new byte[frameBytes];
                await stdout.ReadExactlyAsync(frame, _stop.Token);
                LastFrameUtc = DateTime.UtcNow;
                onFrame(frame);
            }
        }
        finally
        {
            Running = false;
            try { process.Kill(); } catch (InvalidOperationException) { }
        }
    }
}
