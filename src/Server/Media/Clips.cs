using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using TalkLens.Core;

namespace TalkLens.Media;

/// <summary>Copies a room's camera to disk without re-encoding, for watching later and for clips (RF-17, RF-19). Only runs when the operator asks, and stays on the laptop.</summary>
public sealed class Recorder(string ffmpeg, long talkId, string rtspUrl, Func<long> clock, ILogger log)
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task? _loop;

    public static string Directory(long talkId) => Path.Combine(Paths.Data, "recordings", talkId.ToString());

    public void Start()
    {
        System.IO.Directory.CreateDirectory(Directory(talkId));
        _loop = Task.Run(RunAsync);
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_process is { HasExited: false } process)
        {
            // A clean quit finishes the file. A killed one is still mostly readable, so this is only a courtesy.
            try { await process.StandardInput.WriteAsync('q'); await process.StandardInput.FlushAsync(); } catch (IOException) { }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { process.Kill(); }
        }
        if (_loop != null) await _loop;
    }

    /// <summary>The camera can drop and return, so each connection is a new part named after when it began.</summary>
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-i", rtspUrl, "-c", "copy", "-f", "matroska", Path.Combine(Directory(talkId), $"part-{clock()}.mkv") })
                    info.ArgumentList.Add(arg);
                _process = Process.Start(info)!;
                _ = _process.StandardError.ReadToEndAsync();
                await _process.WaitForExitAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning("Recording stopped: {Type}", e.GetType().Name);
            }
            try { await Task.Delay(Retry, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The part covering a moment, and where in that part it falls.</summary>
    public static (string File, double OffsetSeconds)? FindPart(long talkId, long t, Func<string, bool>? hasContent = null)
    {
        var dir = Directory(talkId);
        if (!System.IO.Directory.Exists(dir)) return null;
        var parts = System.IO.Directory.GetFiles(dir, "part-*.mkv").Concat(System.IO.Directory.GetFiles(dir, "part-*.mp4"))
            .Select(f => (File: f, Start: StartOf(f)))
            .Where(p => (hasContent ?? (f => new FileInfo(f).Length > 0))(p.File))
            .OrderBy(p => p.Start).ToList();
        var chosen = parts.LastOrDefault(p => p.Start <= t);
        if (chosen.File == null) chosen = parts.FirstOrDefault();
        return chosen.File == null ? null : (chosen.File, Math.Max(0, t - chosen.Start) / 1000.0);
    }

    private static long StartOf(string file) => long.Parse(Path.GetFileNameWithoutExtension(file)["part-".Length..]);

    /// <summary>What a browser can play. Usually one, more if the camera dropped and came back during the talk.</summary>
    public static IReadOnlyList<(long Start, string File)> ReplayParts(long talkId) =>
        !System.IO.Directory.Exists(Directory(talkId)) ? [] : System.IO.Directory.GetFiles(Directory(talkId), "part-*.mp4")
            .Select(f => (Start: StartOf(f), File: f)).OrderBy(p => p.Start).ToList();

    public static bool HasUnfinishedParts(long talkId) =>
        System.IO.Directory.Exists(Directory(talkId)) && System.IO.Directory.GetFiles(Directory(talkId), "part-*.mkv").Length > 0;
}

/// <summary>Prepares recordings to watch and cuts vertical clips with captions, one at a time and below normal priority, so a live talk always comes first.</summary>
public sealed class ClipMaker(Store store, AppOptions options, ILogger<ClipMaker> log) : BackgroundService
{
    private readonly Channel<Func<CancellationToken, Task>> _queue = Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    public static string Directory => Path.Combine(Paths.Data, "clips");
    public static string FilePath(string file) => Path.Combine(Directory, file);

    public ClipRow Request(long talkId, long t, long lengthMs, string reason)
    {
        var clip = store.AddClip(talkId, t, lengthMs, reason);
        QueueClip(clip.Id);
        return clip;
    }

    private void QueueClip(long id) => _queue.Writer.TryWrite(async stop =>
    {
        if (store.FindClip(id) is { } clip) await RenderAsync(clip, stop);
    });

    public void PrepareReplay(long talkId) => _queue.Writer.TryWrite(stop => RemuxAsync(talkId, stop));

    public void Delete(ClipRow clip)
    {
        if (clip.File.Length > 0 && File.Exists(FilePath(clip.File))) File.Delete(FilePath(clip.File));
        store.DeleteClip(clip.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Whatever a closed laptop left behind. Recordings go first, because clips read them.
        var recordings = Path.Combine(Paths.Data, "recordings");
        if (System.IO.Directory.Exists(recordings))
            foreach (var dir in System.IO.Directory.GetDirectories(recordings))
                if (long.TryParse(Path.GetFileName(dir), out var talk) && Recorder.HasUnfinishedParts(talk)) PrepareReplay(talk);
        foreach (var unfinished in store.ListClips().Where(c => c.Status is "queued" or "rendering")) QueueClip(unfinished.Id);
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stop))
            {
                try { await job(stop); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("A video job failed: {Type}", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Browsers do not play Matroska or, in MP4, Opus. The picture is copied when it can be, since that takes seconds and loses nothing.</summary>
    private async Task RemuxAsync(long talkId, CancellationToken stop)
    {
        var dir = Recorder.Directory(talkId);
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var part in System.IO.Directory.GetFiles(dir, "part-*.mkv"))
        {
            var name = Path.GetFileNameWithoutExtension(part);
            if (new FileInfo(part).Length == 0)
            {
                File.Delete(part);
                continue;
            }
            var temp = Path.Combine(dir, $"tmp-{name}.mp4");
            string[] common = ["-hide_banner", "-loglevel", "error", "-y", "-i", part];
            string[] finish = ["-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", temp];
            var done = await RunFfmpegAsync([.. common, "-c:v", "copy", .. finish], stop)
                || await RunFfmpegAsync([.. common, "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p", .. finish], stop);
            if (done)
            {
                File.Move(temp, Path.Combine(dir, name + ".mp4"), overwrite: true);
                File.Delete(part);
            }
            else
            {
                // Often a few broken seconds from a camera that dropped. Set aside, so it is not tried again on every start.
                log.LogWarning("Could not prepare part of talk {Talk} to watch", talkId);
                if (File.Exists(temp)) File.Delete(temp);
                File.Move(part, Path.ChangeExtension(part, ".failed"), overwrite: true);
            }
        }
        // Deleted while this ran: its folder could not go then, because a file in it was open.
        if (store.FindTalk(talkId) == null) System.IO.Directory.Delete(dir, recursive: true);
    }

    private async Task<bool> RunFfmpegAsync(IEnumerable<string> args, CancellationToken stop)
    {
        var info = new ProcessStartInfo(options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _ = process.StandardError.ReadToEndAsync(stop);
        await process.WaitForExitAsync(stop);
        return process.ExitCode == 0;
    }

    private async Task RenderAsync(ClipRow clip, CancellationToken stop)
    {
        store.SetClip(clip.Id, "rendering", "");
        var part = Recorder.FindPart(clip.TalkId, clip.T);
        if (part == null)
        {
            store.SetClip(clip.Id, "failed", "");
            return;
        }
        var file = $"clip-{clip.Id}.mp4";
        var srt = $"clip-{clip.Id}.srt";
        try
        {
            await File.WriteAllTextAsync(FilePath(srt), Subtitles(store.GetSegments(clip.TalkId, Math.Max(0, clip.T - 5000), clip.T + clip.LengthMs), clip.T, clip.LengthMs), new UTF8Encoding(false), stop);
            // A relative subtitle path sidesteps the filter's escaping rules for Windows paths.
            var filter = "crop='min(iw,ih*9/16)':ih,scale=720:1280,setsar=1"
                + (new FileInfo(FilePath(srt)).Length > 0 ? $",subtitles={srt}:force_style='FontSize=15,Alignment=2,MarginV=70,Outline=2'" : "");
            var info = new ProcessStartInfo(options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = Directory };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-ss", part.Value.OffsetSeconds.ToString(CultureInfo.InvariantCulture), "-i", part.Value.File,
                "-t", (clip.LengthMs / 1000.0).ToString(CultureInfo.InvariantCulture), "-vf", filter,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", file,
            }) info.ArgumentList.Add(arg);

            using var process = Process.Start(info)!;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var errors = process.StandardError.ReadToEndAsync(stop);
            await process.WaitForExitAsync(stop);
            if (process.ExitCode == 0 && File.Exists(FilePath(file)))
            {
                store.SetClip(clip.Id, "ready", file);
                return;
            }
            log.LogWarning("Clip {Id} failed: {Errors}", clip.Id, await errors);
            store.SetClip(clip.Id, "failed", "");
        }
        finally
        {
            if (File.Exists(FilePath(srt))) File.Delete(FilePath(srt));
        }
    }

    public static string Subtitles(IEnumerable<Segment> segments, long clipStart, long length)
    {
        static string Stamp(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        var n = 1;
        foreach (var segment in segments.Where(s => s.End > clipStart && s.T < clipStart + length))
            text.Append(n++).Append('\n').Append(Stamp(segment.T - clipStart)).Append(" --> ").Append(Stamp(Math.Min(segment.End, clipStart + length) - clipStart)).Append('\n')
                .Append(segment.Text).Append("\n\n");
        return text.ToString();
    }
}
