using System.Text.Json;
using TalkLens.Ai;

namespace TalkLens.Core;

public sealed record PhotoReview(bool Ok, string Reason, string Alt);

/// <summary>
/// Photos people share for the big screen. Nothing reaches the screen until the operator approves it, unless the organizer
/// chose to publish on their own; even then the AI holds back anything it would not show on a projector.
/// </summary>
public sealed class PhotoWall(Store store, AiModels ai, Func<bool> online, ILogger log)
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int PerHour = 10;
    private const int MaxAltChars = 200;
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(45);

    private const string ReviewPrompt = """
        You check photos that people at a tech conference want to show on the big screen in the room.
        Reply with JSON only: {"ok": true or false, "reason": "...", "alt": "..."}.
        ok is false for nudity, violence, hateful symbols or words, offensive gestures, readable private data such as
        documents, chats, badges with full names or screens with personal details, or anything else you would not put
        on a projector at a professional event. reason says why in a few words, or is empty.
        alt describes the photo in one short sentence for people who cannot see it. Never guess who anyone is.
        """;

    /// <summary>Called whenever a photo changes, so the screen, the album and the operator can update.</summary>
    public Func<Photo, Task>? Changed { get; set; }

    public static string Folder => Path.Combine(Paths.Data, "photos");
    public static string FilePath(long id) => Path.Combine(Folder, $"{id}.jpg");

    /// <summary>JPEG only: the phone already resized it, which also dropped the location and camera details.</summary>
    public static bool IsJpeg(byte[] bytes) => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    public async Task<Photo> AddAsync(long room, long? talk, string voter, byte[] jpeg)
    {
        var photo = store.AddPhoto(room, talk, voter);
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(FilePath(photo.Id), StripMetadata(jpeg));
        _ = Task.Run(() => ReviewAsync(photo.Id, jpeg));
        return photo;
    }

    private async Task ReviewAsync(long id, byte[] jpeg)
    {
        try
        {
            var review = await CheckAsync(jpeg);
            var auto = store.IsOn(Settings.PhotosAuto);
            // Held back by the AI: the operator decides, with the reason in front of them.
            var status = review is { Ok: false } ? "pending" : auto ? "approved" : "pending";
            store.SetPhotoReview(id, status, review?.Alt ?? "", review is { Ok: false } ? review.Reason : "");
            if (store.FindPhoto(id) is { } photo && Changed != null) await Changed(photo);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Photo {Id}: the review failed, so it waits for the operator", id);
        }
    }

    /// <returns>Null without a vision model or internet.</returns>
    public async Task<PhotoReview?> CheckAsync(byte[] jpeg)
    {
        if (ai.Vision is not { } vision || !online()) return null;
        using var cts = new CancellationTokenSource(ReviewTimeout);
        var reply = await vision.CompleteAsync(ReviewPrompt, "Check this photo.", jpeg, 300, cts.Token);
        return ParseReview(reply);
    }

    public static PhotoReview? ParseReview(string? reply)
    {
        if (reply == null) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var json = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = json.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var text = (string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            var alt = text("alt");
            return new PhotoReview(ok.GetBoolean(), text("reason"), alt.Length > MaxAltChars ? alt[..MaxAltChars].TrimEnd() + "…" : alt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the APP1 to APP15 and comment blocks (EXIF, GPS, XMP) in case a phone sent the original. A phone that
    /// resized the photo already sends none.
    /// </summary>
    public static byte[] StripMetadata(byte[] jpeg)
    {
        if (!IsJpeg(jpeg)) return jpeg;
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            // Start of scan: the image data follows, to the end.
            if (marker == 0xDA) break;
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) return jpeg;
            var drop = marker is >= 0xE1 and <= 0xEF or 0xFE;
            if (!drop) output.Write(jpeg, i, 2 + length);
            i += 2 + length;
        }
        output.Write(jpeg, i, jpeg.Length - i);
        return output.ToArray();
    }

    public void Delete(long id)
    {
        store.DeletePhoto(id);
        // If the file is still open somewhere it stays on disk, but nothing links to it any more.
        try { File.Delete(FilePath(id)); } catch (IOException) { }
    }
}
