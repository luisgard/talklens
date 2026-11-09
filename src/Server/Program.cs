using Microsoft.AspNetCore.SignalR;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Media;
using TalkLens.Slides;
using TalkLens.Web;

Paths.LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var options = builder.Configuration.Get<AppOptions>() ?? new AppOptions();

// Windows lets two programs listen on one port, with no error, and then each visitor reaches either of them.
var wantedHttpPort = options.Network.HttpPort;
var wantedHttpsPort = options.Network.HttpsPort;
options.Network.HttpPort = Ports.FirstFree(wantedHttpPort);
options.Network.HttpsPort = Ports.FirstFree(wantedHttpsPort, options.Network.HttpPort);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(options.Network.HttpPort);
    kestrel.ListenAnyIP(options.Network.HttpsPort, listen => listen.UseHttps(LocalCertificate.LoadOrCreate()));
});

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var services = builder.Services;
services.AddSingleton(options);
services.AddSingleton(http);
services.AddSingleton(AiModels.Create(options.Ai, http));
services.AddSingleton(new Store(Path.Combine(Paths.Data, "talklens.db")));
services.AddSingleton<Presence>();
services.AddSingleton<LanInfo>();
services.AddSingleton<OperatorAuth>();
services.AddSingleton<SlideReader>();
services.AddSingleton<Connectivity>();
services.AddHostedService(p => p.GetRequiredService<Connectivity>());
services.AddSingleton<MediaMtx>();
services.AddHostedService(p => p.GetRequiredService<MediaMtx>());
services.AddSingleton(options.Mail);
services.AddSingleton(options.Ai);
services.AddSingleton<VoiceModel>();
services.AddSingleton<Mailer>();
services.AddHostedService(p => p.GetRequiredService<Mailer>());
services.AddSingleton<ClipMaker>();
services.AddHostedService(p => p.GetRequiredService<ClipMaker>());
services.AddSingleton(p => new Reports(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online,
    p.GetRequiredService<ILoggerFactory>().CreateLogger<Reports>()));
services.AddSingleton<TalkService>();
services.AddHostedService(p => p.GetRequiredService<TalkService>());
services.AddSingleton<StatusBroadcaster>();
services.AddHostedService(p => p.GetRequiredService<StatusBroadcaster>());
services.AddSingleton(p => new SignLexicon(Path.Combine(p.GetRequiredService<IWebHostEnvironment>().WebRootPath, "signs", "lexicon.sigml")));
services.AddSingleton(p => new SignTranslator(
    p.GetRequiredService<SignLexicon>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online, p.GetRequiredService<ILoggerFactory>().CreateLogger<SignTranslator>()));
services.AddHttpClient("elastic", client => client.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton(p => new ElasticIndex(
    p.GetRequiredService<IHttpClientFactory>().CreateClient("elastic"), options.Ai.ElasticsearchUrl.TrimEnd('/'),
    ElasticIndex.PrefixFor(Path.Combine(Paths.Data, "talklens.db")), p.GetRequiredService<Store>(), p.GetRequiredService<ILoggerFactory>().CreateLogger<ElasticIndex>()));
services.AddHostedService(p => p.GetRequiredService<ElasticIndex>());
services.AddSingleton(p => new VectorIndex(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>().Embeddings, () => p.GetRequiredService<Connectivity>().Online,
    p.GetRequiredService<ILoggerFactory>().CreateLogger<VectorIndex>(), p.GetRequiredService<ElasticIndex>()));
services.AddHostedService(p => p.GetRequiredService<VectorIndex>());
services.AddSingleton(p => new Matchmaker(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>().Embeddings, () => p.GetRequiredService<Connectivity>().Online,
    p.GetRequiredService<ILoggerFactory>().CreateLogger<Matchmaker>(), p.GetRequiredService<ElasticIndex>()));
services.AddHostedService(p => p.GetRequiredService<Matchmaker>());
services.AddSingleton(p => new Answerer(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online,
    options.Ai.AnswerTimeoutSeconds, p.GetRequiredService<ILoggerFactory>().CreateLogger<Answerer>(), p.GetRequiredService<VectorIndex>()));
services.AddSingleton(p => new TalkInsights(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online,
    p.GetRequiredService<ILoggerFactory>().CreateLogger<TalkInsights>()));
services.AddSingleton(p => new PhotoWall(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online,
    p.GetRequiredService<ILoggerFactory>().CreateLogger<PhotoWall>())
{
    Changed = photo => Endpoints.PhotoChanged(p.GetRequiredService<IHubContext<LiveHub>>(), photo),
});
services.AddSingleton(p => new QuestionQueue(
    p.GetRequiredService<Store>(), p.GetRequiredService<AiModels>(), () => p.GetRequiredService<Connectivity>().Online,
    room => p.GetRequiredService<IHubContext<LiveHub>>().Clients.Group(LiveHub.OperatorGroup).SendAsync("questions", room),
    p.GetRequiredService<ILoggerFactory>().CreateLogger<QuestionQueue>()));
services.AddHttpClient("media");
services.AddSingleton<Announcements>();
services.AddSignalR();

var app = builder.Build();

app.UseRemoteGuard();
app.UseCaptiveRedirect();
app.UseDefaultFiles();
// Browsers must check for a newer page each time, or phones keep an old version after an update.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
app.MapHub<LiveHub>("/hub");
app.MapAttendeeApi();
app.MapOperatorApi();
app.MapEventApi();
app.MapNetworkingApi();
app.MapOffersApi();
app.MapNowApi();
app.MapContentApi();
app.MapEngagementApi();
app.MapWallApi();
app.MapPages();
app.MapEventPages();
app.MapMediaProxy();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var lan = app.Services.GetRequiredService<LanInfo>();
    var auth = app.Services.GetRequiredService<OperatorAuth>();
    var ai = app.Services.GetRequiredService<AiModels>();
    if (options.Network.HttpPort != wantedHttpPort || options.Network.HttpsPort != wantedHttpsPort)
    {
        Console.WriteLine($"""

            Another program is using port {(options.Network.HttpPort != wantedHttpPort ? wantedHttpPort : wantedHttpsPort)}, so TalkLens moved to {options.Network.HttpPort} and {options.Network.HttpsPort}.
            QR codes printed earlier will not work. If phones cannot connect, allow these ports through the firewall: see docs/NETWORK.md.
            """);
    }
    Console.WriteLine($"""

        TalkLens is running.
          Attendees:  {lan.HttpUrl}/
          Organizer:  {lan.HttpsUrl}/start?key={auth.Key}
                      http://localhost:{options.Network.HttpPort}/start?key={auth.Key}
          Captions:   {options.Ai.TranscriptionProvider}{(options.Ai.TranscriptionProvider != "local" && AiOptions.Key(options.Ai.TranscriptionProvider) == null ? " (no API key: local model only)" : "")}
          Answers:    {ai.Chat?.Name ?? "offline excerpts only (no API key)"}
          Slides:     {ai.Vision?.Name ?? "local OCR only (no API key)"}

        """);
});

app.Run();
