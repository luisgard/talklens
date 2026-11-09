using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Media;
using TalkLens.Web;
using Xunit;

namespace TalkLens.Tests;

public sealed class EventTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public EventTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class FakeChat(string reply) : IChatModel
    {
        public string Name => "fake";
        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct) => Task.FromResult<string?>(reply);
    }

    // ----- CSV -----
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@me", "'@me")]
    [InlineData("Ada", "Ada")]
    [InlineData("Lovelace, Ada", "\"Lovelace, Ada\"")]
    public void Csv_cells_cannot_run_as_formulas(string value, string expected) => Assert.Equal(expected, Csv.Cell(value));

    [Fact]
    public void Csv_reads_a_luma_export()
    {
        var people = Csv.ParsePeople("api_id,name,first_name,last_name,email,qr_code_url\ngst-1,\"Lovelace, Ada\",,,ada@example.com,https://luma.com/check-in/x?pk=gst-1\ngst-2,,Alan,Turing,alan@example.com,\ngst-3,No Mail,,,not-an-email,\n");
        Assert.Equal(2, people.Count);
        Assert.Equal(new ImportedPerson("Lovelace, Ada", "ada@example.com", "gst-1", "https://luma.com/check-in/x?pk=gst-1"), people[0]);
        Assert.Equal("Alan Turing", people[1].Name);
    }

    [Fact]
    public void Csv_leaves_out_who_is_not_coming_and_counts_them()
    {
        var file = Csv.ReadPeople("name,email,approval_status\nAda,ADA@example.com,approved\nBob,bob@example.com,declined\nEve,eve@example.com,waitlist\nNo Mail,,approved\n");
        Assert.Equal("ada@example.com", Assert.Single(file.People).Email);
        Assert.Equal((2, 1), (file.NotComing, file.NoEmail));
    }

    [Fact]
    public void Csv_reads_what_a_spanish_excel_saves()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("Nombre;Apellido;Correo Electrónico\nJosé;Pérez;jose@example.com\n");
        var person = Assert.Single(Csv.ReadPeople(Csv.Decode(bytes)).People);
        Assert.Equal(("José Pérez", "jose@example.com"), (person.Name, person.Email));
    }

    [Fact]
    public void Csv_keeps_an_arrival_already_marked_in_luma()
    {
        var person = Assert.Single(Csv.ParsePeople("name,email,checked_in_at\nAda,ada@example.com,2026-11-09T16:00:00Z\n"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T16:00:00Z").ToUnixTimeMilliseconds(), person.CheckedInAt);
    }

    [Fact]
    public void The_same_email_in_other_capitals_is_one_person()
    {
        _store.UpsertAttendee("Ada", "ada@example.com", "csv");
        Assert.False(_store.UpsertAttendee("Ada L", "ADA@Example.com", "csv").Created);
    }

    [Fact]
    public void Csv_without_an_email_column_imports_nobody() => Assert.Empty(Csv.ParsePeople("name\nAda\n"));

    [Fact]
    public void Csv_uses_the_mail_as_name_when_there_is_none() => Assert.Equal("ada", Csv.ParsePeople("email\nada@example.com\n")[0].Name);

    // ----- Luma -----
    [Fact]
    public void Luma_guest_is_read_with_its_arrival()
    {
        using var doc = JsonDocument.Parse("""{"id":"gst-1","user_name":"Ada","user_email":"ada@example.com","check_in_qr_code":"qr-1","approval_status":"approved","event_tickets":[{"checked_in_at":null},{"checked_in_at":"2026-11-04T10:00:00Z"}]}""");
        var guest = LumaClient.ParseGuest(doc.RootElement);
        Assert.Equal(("gst-1", "Ada", "ada@example.com", "qr-1", true), (guest.Id, guest.Name, guest.Email, guest.Qr, guest.Approved));
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T10:00:00Z").ToUnixTimeMilliseconds(), guest.CheckedInAt);
    }

    [Fact]
    public void Luma_guest_who_is_not_approved_is_marked()
    {
        using var doc = JsonDocument.Parse("""{"guest":{"id":"g","user_email":"a@b.c","approval_status":"declined"}}""");
        var guest = LumaClient.ParseGuest(doc.RootElement);
        Assert.False(guest.Approved);
        Assert.Null(guest.CheckedInAt);
    }

    private sealed class LumaStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("the-key", request.Headers.GetValues("x-luma-api-key").Single());
            var second = request.RequestUri!.Query.Contains("pagination_cursor=next");
            var body = second
                ? """{"entries":[{"id":"g2","user_name":"Alan","user_email":"alan@example.com","approval_status":"approved"}],"has_more":false}"""
                : """{"entries":[{"id":"g1","user_name":"Ada","user_email":"ada@example.com","approval_status":"approved"}],"has_more":true,"next_cursor":"next"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Luma_guests_are_read_across_pages()
    {
        using var http = new HttpClient(new LumaStub());
        var guests = await new LumaClient(http, "http://stub").GetGuestsAsync("evt-1", "the-key", default);
        Assert.Equal(["Ada", "Alan"], guests.Select(g => g.Name));
    }

    // ----- Door scanning -----
    [Fact]
    public void A_scan_finds_our_ticket_link_a_bare_token_and_a_luma_qr()
    {
        var (ada, _) = _store.UpsertAttendee("Ada", "ada@example.com", "self");
        var (alan, _) = _store.UpsertAttendee("Alan", "alan@example.com", "luma", "gst-9", "https://luma.com/check-in/evt?pk=gst-9");
        Assert.Equal(ada.Id, Endpoints.FindByScan(_store, $"http://192.168.1.2:8080/ticket/{ada.Token}")?.Id);
        Assert.Equal(ada.Id, Endpoints.FindByScan(_store, ada.Token)?.Id);
        Assert.Equal(alan.Id, Endpoints.FindByScan(_store, "https://luma.com/check-in/evt?pk=gst-9")?.Id);
        Assert.Equal(alan.Id, Endpoints.FindByScan(_store, "https://luma.com/other?pk=gst-9&x=1")?.Id);
        Assert.Null(Endpoints.FindByScan(_store, "https://example.com/nothing"));
        Assert.Null(Endpoints.FindByScan(_store, " "));
    }

    [Fact]
    public void The_same_email_never_makes_a_second_ticket()
    {
        var (first, created) = _store.UpsertAttendee("Ada", "Ada@Example.com", "self");
        var (second, createdAgain) = _store.UpsertAttendee("Someone else", "ada@example.com", "csv");
        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Token, second.Token);
    }

    // ----- Privacy of attendee data -----
    [Fact]
    public void Deleting_a_person_removes_everything_about_them()
    {
        var (ada, _) = _store.UpsertAttendee("Ada", "ada@example.com", "self");
        var (alan, _) = _store.UpsertAttendee("Alan", "alan@example.com", "self");
        var talk = _store.CreateTalk("Demo", "Grace", "es", true).Id;
        var sponsor = _store.AddSponsor("Acme", "acme");
        _store.AddWatchTime(ada.Id, talk, 1000);
        _store.AddDoorScan(ada.Id, 1, talk);
        _store.AddContact(ada.Id, alan.Id, "Demo");

        _store.DeleteAttendee(ada.Id);

        Assert.Null(_store.FindAttendee(ada.Id));
        Assert.Empty(_store.ListAttendance());
        Assert.Empty(_store.ListContacts(alan.Id));
        Assert.Equal(0, _store.CountDoorOnly(talk, []));
        Assert.NotNull(sponsor);
    }

    [Fact]
    public void A_contact_is_stored_once_and_seen_by_both()
    {
        var (ada, _) = _store.UpsertAttendee("Ada", "ada@example.com", "self");
        var (alan, _) = _store.UpsertAttendee("Alan", "alan@example.com", "self");
        _store.AddContact(ada.Id, alan.Id, "Demo");
        _store.AddContact(alan.Id, ada.Id, "Demo");
        Assert.Single(_store.ListContacts(ada.Id));
        Assert.Single(_store.ListContacts(alan.Id));
    }

    [Fact]
    public void Someone_at_the_door_who_is_also_connected_counts_once()
    {
        var (ada, _) = _store.UpsertAttendee("Ada", "ada@example.com", "self");
        var (alan, _) = _store.UpsertAttendee("Alan", "alan@example.com", "self");
        var talk = _store.CreateTalk("Demo", "Grace", "es", true).Id;
        _store.AddDoorScan(ada.Id, 1, talk);
        _store.AddDoorScan(alan.Id, 1, talk);
        Assert.Equal(2, _store.CountDoorOnly(talk, []));
        Assert.Equal(1, _store.CountDoorOnly(talk, [ada.Id]));
    }

    [Fact]
    public void Chat_of_a_talk_stays_out_of_the_event_chat()
    {
        var talk = _store.CreateTalk("Demo", "Grace", "es", true).Id;
        _store.AddChat(talk, "Ada", "in the talk");
        _store.AddChat(0, "Alan", "everywhere");
        Assert.Equal("in the talk", Assert.Single(_store.ListChat(talk)).Text);
        Assert.Equal("everywhere", Assert.Single(_store.ListChat(0)).Text);
    }

    // ----- Reports -----
    [Fact]
    public void A_small_count_is_hidden()
    {
        Assert.Null(Reports.Gate(4, 5));
        Assert.Equal(5, Reports.Gate(5, 5));
    }

    [Fact]
    public void A_moment_with_too_few_reactions_is_hidden()
    {
        var moments = Reports.Moments([1000, 2000], [3000], [], 3);
        Assert.Equal(new Moment(0, 2, 1, 0), Assert.Single(moments));
        Assert.Empty(Reports.Moments([1000, 2000], [], [], 3));
    }

    [Theory]
    [InlineData("Hablamos de Ácme hoy", true)]
    [InlineData("ACME sponsors this", true)]
    [InlineData("This is the acmeology talk", false)]
    [InlineData("nothing here", false)]
    public void Mentions_match_whole_words_ignoring_case_and_accents(string text, bool expected) =>
        Assert.Equal(expected, Reports.Matches(text, Reports.Terms("acme, other brand")));

    [Fact]
    public void Model_topics_cannot_invent_counts_or_show_small_groups()
    {
        var topics = Reports.ParseTopics("Pricing | 7\nToo small | 2\nToo many | 99\nnot a topic\n | 6", 10, 5);
        Assert.Equal(new Topic("Pricing", 7), Assert.Single(topics));
    }

    [Fact]
    public void Topics_need_enough_people_behind_them()
    {
        var topics = Reports.WordTopics(["pricing today", "pricing tomorrow", "kubernetes"], 2);
        Assert.Equal("pricing", Assert.Single(topics).Name);
    }

    // ----- Clips -----
    [Fact]
    public void Clips_go_where_questions_cluster_and_do_not_overlap()
    {
        var picks = Reports.PickClipMoments([120_000, 121_000, 122_000], [10_000, 125_000, 400_000], 600_000);
        Assert.Contains(picks, p => p.Reason == "many questions");
        Assert.All(picks, p => Assert.InRange(p.T, 0, 600_000));
        foreach (var (a, b) in picks.Zip(picks.Skip(1))) Assert.True(b.T - a.T >= Reports.ClipLengthMs);
    }

    [Fact]
    public void Clip_captions_are_cut_to_the_clip()
    {
        var talk = _store.CreateTalk("Demo", "Grace", "es", true).Id;
        _store.AddSegment(talk, 1000, 4000, "before", "local");
        _store.AddSegment(talk, 9000, 12000, "starts before the clip", "local");
        _store.AddSegment(talk, 18000, 25000, "runs past the clip", "local");
        var srt = ClipMaker.Subtitles(_store.GetSegments(talk), 10000, 10000);
        Assert.DoesNotContain("before\n", srt.Replace("starts before the clip", ""));
        Assert.Contains("00:00:00,000 --> 00:00:02,000", srt);
        Assert.Contains("00:00:08,000 --> 00:00:10,000", srt);
    }

    // ----- Remote access: the venue network is local, everything else is remote -----
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Addresses_are_classified(string address, bool local) => Assert.Equal(local, Remote.IsLocal(IPAddress.Parse(address)));

    // ----- Questions across the whole event -----
    [Fact]
    public async Task An_answer_across_talks_names_the_talk_each_source_came_from()
    {
        var a = _store.CreateTalk("Networks", "Ada", "es", true).Id;
        var b = _store.CreateTalk("Kernels", "Alan", "es", true).Id;
        _store.AddSegment(a, 1000, 4000, "los routers usan eBPF para filtrar", "local");
        _store.AddSegment(b, 1000, 4000, "el kernel carga programas eBPF", "local");
        var answerer = new Answerer(_store, new AiModels { Chat = new FakeChat("Las dos hablan de eBPF [1] [2].") }, () => true, 0.2, NullLogger.Instance);

        var answer = await answerer.AskAcrossAsync("¿quién habló de eBPF?", default);

        Assert.Equal(2, answer.Talks!.Count);
        Assert.Equal(new[] { a, b }.Order(), answer.Talks.Select(t => t.TalkId).Order());
    }

    [Fact]
    public async Task A_talk_that_was_not_kept_is_not_searched()
    {
        var a = _store.CreateTalk("Off the record", "Ada", "es", false).Id;
        _store.AddSegment(a, 1000, 4000, "secreto sobre eBPF", "local");
        _store.DeleteTalk(a);
        var answerer = new Answerer(_store, new AiModels(), () => true, 0.2, NullLogger.Instance);
        Assert.Equal("not_found", (await answerer.AskAcrossAsync("¿eBPF?", default)).Mode);
    }

    [Fact]
    public void A_port_another_program_is_using_is_skipped()
    {
        var other = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        other.Start();
        try
        {
            var taken = ((System.Net.IPEndPoint)other.LocalEndpoint).Port;

            Assert.NotEqual(taken, Ports.FirstFree(taken));
            Assert.InRange(Ports.FirstFree(taken), taken + 1, taken + 10);
        }
        finally { other.Stop(); }
    }

    [Fact]
    public void Two_services_never_get_the_same_port()
    {
        var first = Ports.FirstFree(48123);

        Assert.NotEqual(first, Ports.FirstFree(first, first));
    }
}
