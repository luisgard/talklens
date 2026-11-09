using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class WallTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public WallTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private Reports Reports() => new(_store, new AiModels(), () => false, NullLogger.Instance);

    // ----- Reactions -----
    [Fact]
    public void Reaction_peaks_say_what_was_being_said()
    {
        var segments = new[]
        {
            new Segment(1, 1, 0, 8_000, "Welcome everyone", "cloud"),
            new Segment(2, 1, 61_000, 68_000, "And here is the live demo", "cloud"),
            new Segment(3, 1, 125_000, 130_000, "Now the tricky part about generics", "cloud"),
        };
        var reactions = new List<Reaction> { new(1_000, "clap") };
        reactions.AddRange(Enumerable.Range(0, 6).Select(i => new Reaction(62_000 + i * 1000, "fire")));
        reactions.AddRange(Enumerable.Range(0, 4).Select(i => new Reaction(126_000 + i * 1000, "lost")));

        var summary = TalkLens.Core.Reports.SummarizeReactions(reactions, segments);

        Assert.Equal(11, summary.Total);
        Assert.Equal([1, 6, 0, 4], summary.Totals);
        Assert.Equal([0L, 60_000L, 120_000L], summary.Timeline.Select(b => b.T));
        // A single clap is not a peak.
        Assert.Equal(2, summary.Peaks.Count);
        Assert.Equal(("fire", 6, "And here is the live demo"), (summary.Peaks[0].Kind, summary.Peaks[0].Count, summary.Peaks[0].Said));
        Assert.Equal(("lost", 120_000L), (summary.Peaks[1].Kind, summary.Peaks[1].T));
    }

    [Fact]
    public void Reactions_stay_hidden_until_enough_people_reacted()
    {
        var talk = _store.CreateTalk("Testing", "Ada", "en", true);
        // One very keen phone is still one person.
        for (var i = 0; i < 20; i++) _store.AddReaction(talk.Id, i * 1000, "clap", "phone-a");
        Assert.Null(Reports().Reactions(talk, Settings.DefaultMinParticipants));

        for (var i = 1; i < Settings.DefaultMinParticipants; i++) _store.AddReaction(talk.Id, 5000, "wow", $"phone-{i}");
        var summary = Reports().Reactions(talk, Settings.DefaultMinParticipants);
        Assert.NotNull(summary);
        Assert.Equal(20 + Settings.DefaultMinParticipants - 1, summary!.Total);
    }

    // ----- Photos -----
    [Fact]
    public void A_like_from_the_same_phone_is_taken_back()
    {
        var photo = _store.AddPhoto(1, null, "phone-a");
        Assert.True(_store.ToggleLike(photo.Id, "phone-b"));
        Assert.True(_store.ToggleLike(photo.Id, "phone-c"));
        Assert.False(_store.ToggleLike(photo.Id, "phone-b"));

        Assert.Equal(1, _store.FindPhoto(photo.Id)!.Likes);
        Assert.Equal([photo.Id], _store.LikedBy("phone-c"));
    }

    [Fact]
    public void Photos_are_listed_by_status_newest_first_and_outlive_their_talk()
    {
        var talk = _store.CreateTalk("Testing", "Ada", "en", true);
        var first = _store.AddPhoto(1, talk.Id, "phone-a");
        var second = _store.AddPhoto(1, talk.Id, "phone-b");
        _store.SetPhotoReview(second.Id, "approved", "People at a talk.", "");

        Assert.Equal([second.Id, first.Id], _store.ListPhotos().Select(p => p.Id));
        Assert.Equal([second.Id], _store.ListPhotos("approved").Select(p => p.Id));
        Assert.Equal("People at a talk.", _store.FindPhoto(second.Id)!.Alt);
        Assert.Equal(2, _store.CountPhotosSince("phone-a", 0) + _store.CountPhotosSince("phone-b", 0));

        _store.DeleteTalk(talk.Id);
        Assert.Null(_store.FindPhoto(second.Id)!.TalkId);

        _store.DeletePhoto(first.Id);
        Assert.Null(_store.FindPhoto(first.Id));
    }

    [Theory]
    [InlineData("{\"ok\": true, \"reason\": \"\", \"alt\": \"A crowd clapping.\"}", true, "A crowd clapping.")]
    [InlineData("Sure:\n{\"ok\": false, \"reason\": \"A chat with phone numbers\", \"alt\": \"A phone screen.\"}", false, "A phone screen.")]
    public void The_ai_review_is_read(string reply, bool ok, string alt)
    {
        var review = PhotoWall.ParseReview(reply);
        Assert.Equal((ok, alt), (review!.Ok, review.Alt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("I cannot help with that.")]
    [InlineData("{\"reason\": \"no verdict\"}")]
    [InlineData("{\"ok\": \"yes\"}")]
    public void A_reply_without_a_clear_verdict_leaves_it_to_the_operator(string? reply) =>
        Assert.Null(PhotoWall.ParseReview(reply));

    [Fact]
    public void Location_and_camera_details_are_dropped_and_the_picture_kept()
    {
        byte[] Block(byte marker, params byte[] body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        var exif = Block(0xE1, "Exif\0\0GPS 19.43N 99.13W"u8.ToArray());
        var jfif = Block(0xE0, "JFIF\0"u8.ToArray());
        var table = Block(0xDB, 1, 2, 3);
        byte[] scan = [0xFF, 0xDA, 0x00, 0x02, 9, 9, 9, 0xFF, 0xD9];
        byte[] jpeg = [0xFF, 0xD8, .. jfif, .. exif, .. table, .. scan];

        var clean = PhotoWall.StripMetadata(jpeg);

        Assert.Equal([0xFF, 0xD8, .. jfif, .. table, .. scan], clean);
        Assert.True(PhotoWall.IsJpeg(clean));
        Assert.False(PhotoWall.IsJpeg("GIF89a"u8.ToArray()));
    }
}
