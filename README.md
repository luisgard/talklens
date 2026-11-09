# TalkLens

Open source live captions, translation and "ask the talk" for conferences. Attendees scan a QR code and get the
speaker's video, audio and captions on their phone, with no app and no account. It runs on a modest laptop and its own
router: phones encode the video, the laptop only relays it, and only the AI work goes out to the internet. If the
internet drops, captions continue on a small local model.

"TalkLens" is a working name, and the MIT license is a placeholder choice. Both are open questions in the spec.

All requirements of the spec (RF-01 to RF-38) have an implementation, and an event's home page lists every room, so people can
choose between overlapping talks and move between them. Most of it has only run on one laptop, against synthetic talks. See
[docs/POC.md](docs/POC.md) for what has and has not been verified.

## Run it

You need the .NET 8 SDK and FFmpeg. The script downloads MediaMTX and the offline caption models, then starts the server.

```powershell
.\start.ps1
```

```bash
./start.sh
```

The console prints the attendee address and the operator link. On Windows, keep the folder on a short path such as
`C:\talklens`: Windows cannot load OpenCV from very long paths.

With no API keys everything runs offline: captions on the local model, and questions answered with the closest excerpts.
To use cloud AI, copy `.env.example` to `.env` and add a key.

## Run a talk

1. Set up the network as in [docs/NETWORK.md](docs/NETWORK.md).
2. Open the operator link on the laptop. Use **Add room** for each talk that runs at the same time as another.
3. Pick a room and scan its camera QR with the phone that will film. Accept the certificate warning (the laptop signs its
   own certificate, and browsers only allow the camera over HTTPS), and tap **Start sending**. Keep that phone plugged in
   with its screen on.
4. Name the talk. Slides are off by default: under **Language, audio and slides**, choose a screen share or second phone
   (it shows a QR), an HDMI capture card, or the camera phone pointed at the projection.
5. Confirm that the organizer and the speaker both agreed, and press **Start talk**.
6. Print or project **All rooms** for the event, or the room's own QR at its door.
7. During the talk, **Questions for the speaker** lists what attendees sent, most repeated first. **Show to speaker** puts one
   question on the speaker screen, a page for a phone or display the speaker can see. Its QR is under **Speaker screen** and
   contains the operator key. The speaker screen also says **Slow down a little** when the speaker passes about 170 words a
   minute or captions fall more than 6 seconds behind, because captions, translations and the signing avatar all lag then.
8. **Polls** sends a question with 2 to 6 answers to every phone in the room. You see the votes as they arrive; phones see the
   results once enough people voted that a count does not reveal anyone's vote. Opening a new poll closes the last one.
9. **Room screen for the projector** (`/screen/{room}`, under Polls): open it full screen on the computer connected to the
   projector. It shows the QR to join, the captions, the poll while it runs, approved photos with the AI's description, and the
   room's reactions floating up. It holds no key and does not count as an attendee.
10. **Photo wall**: photos people share wait here for you to approve. The AI checks each one first, says why it held one back,
   and writes a description for people who cannot see it. **Publish shared photos without waiting for me** in Set up skips your
   approval for photos the AI lets through. Anyone in a published photo can take it down at once; it then waits for you.

## Run an event

Everything beyond the talks is optional, and all of it lives behind the links at the top of the operator panel.

- **Home** (`/start`): the steps of an event in order, what is done and what comes next. Start here.
- **Set up** (`/manage`): event details, talks and rooms, guest list, and optionally sponsors and privacy, one step at a time.
- **Check-in** (`/checkin`): find a person by name or email and tap once, with no QR needed, and add someone not on the list. It
  only talks to the laptop, so it works with no internet. A QR ticket can be scanned too, which needs the HTTPS address for the
  camera. The QR for a team phone contains the operator key.
- **Go live** (`/operator`): connect the camera, name the talk, start.
- **Live view** (`/organizer`): attendance per room, where the interest is, questions waiting, and each room's technical state.
- **Results** (`/reports`): every talk side by side (most people at once, person-minutes, rating, questions, chat, poll votes,
  reactions, catch-ups), sortable, with the event's totals and its best attended and best rated talks. Then feedback per talk
  with the rating, its comments and poll results, how the room reacted over the talk with what was being said at each peak,
  sponsor mentions, what the audience cared about, and clips.

What attendees get, with no account:

- **Agenda** (`/event`), which says when a room is full and offers its stream instead.
- **My ticket** (`/me`): an optional free ticket, a contact code to swap with someone, and the talks they went to.
- **Library** (`/library`) and each talk's page (`/talk/{id}`): a one-minute summary, search across the event, questions that span
  talks, and the hallway: chat and unanswered questions for 24 hours after the talk. The recording has subtitles in any of the
  event's languages, made from the transcript and downloadable as `.vtt`, chapters that jump to each part, the technical terms
  explained, where the room reacted most, and a 1 to 5 star rating with an optional anonymous comment. The AI makes subtitles, chapters and terms once per
  language and keeps them with the talk.
- **Photos** (`/album`): every approved photo, by talk, with likes, and a download of all of them as a zip.
- **Community** (`/community`), if the organizer turns it on: an event-wide chat and people with shared interests.
- In a room: public chat, polls, reactions (👏 🔥 🤯 ❓) while the talk is live, a **Photos** tab to share a photo with the room
  screen, **Back 2 min** with captions, **Listen in my language**, **Terms explained** under Ask, and a rating once the talk ends.
  A photo is resized on the phone before it is sent, which also drops its location and camera details, and the server strips any
  that remain. Each phone can send 10 photos an hour.

Ratings, poll votes and likes count once per phone, by a random id the phone keeps, with no account. A phone can react once
every half second, and reaction counts appear only once enough different phones reacted. Nobody sees who sent a photo or
reacted. There is no face recognition. Reports follow the same minimum as
everything else: an average or a comment appears only once at least that many people rated the talk.

Choices the spec left open, all changeable in **Event setup**: reports hide anything counted from fewer than 5 people, and the
hallway stays open for 24 hours. A contact code lasts 5 minutes and works once. How full a room is counts connected people plus
people scanned at its door who are not connected, and it can also be marked full by hand. Payment is out of scope, so the library
opens with a code from the organizer or an event ticket.

### Luma

Upload Luma's CSV export, or import with an API key (Luma Plus). Only people who are coming are added: declined, waitlisted and
unapproved guests are left out and counted. Search by name or email covers the door, and a Luma QR works there if the file has one. Luma's API
has no documented way to mark an arrival and no structured agenda, so arrivals stay here (**Export attendees**) and the agenda is
entered here.

### Mail

Tickets and contact lists go out by SMTP. Set the `Mail__*` values and `MAIL_PASSWORD` in `.env`. Without them, tickets stay on the
phone that registered and the registration desk can show them under **Event setup**. Mail waits in a queue until there is internet.

### Recording, watching later and clips

**Record the whole talk** is ticked by default when starting a talk, and it needs **Keep the transcript and slides** too. When the
talk ends, the laptop prepares the video in seconds, and people can watch it on the talk's page under the same rules as the rest of
the library. Any moment found in a search or cited in an answer plays from that minute. The video stays on the laptop and goes out
through a tunnel only with **Also send the video outside**. Allow about 1 GB per hour of talk. The sound is the camera
phone's own, even if captions listen to a different microphone.

The laptop also cuts up to a few vertical clips with captions, at low priority, where questions clustered or slides changed. All of
it is deleted with the talk.

### Search by meaning, and Elasticsearch

The agenda, the library and long talks are searched two ways at once, and the results are merged: by words, for names and exact
terms, and by vector (OpenAI `text-embedding-3-small`), for the same idea in other words or another language. "Something on eBPF
and security" finds the session about tracing the kernel, even though none of those words are in it. Results found only by meaning
are labelled. Vectors are made during and after each kept talk, and whatever was missed offline is caught up later. Search queries
go to OpenAI, like questions to the AI, and are not stored.

Search runs on Elasticsearch whenever it answers on this laptop, with nothing to configure. The organizer's home page says when it
is on.

```bash
docker compose -f elasticsearch/docker-compose.yml up -d
```

Talks are written to SQLite first and copied to Elasticsearch every 10 seconds. If it stops, or was never started, the same searches
run on SQLite, so a modest laptop loses typo tolerance and word endings, not search. Elasticsearch adds about 2.9 GB of memory on
Windows and 2.6 GB of disk: see [docs/POC.md](docs/POC.md). `Ai__ElasticsearchUrl` changes the address, and empty turns it off. It
listens only on this laptop, so phones never reach it. With no OpenAI key or no internet, search uses words alone.

### Signing avatar (experimental)

A research demo, not an interpreter, and off unless someone turns it on in the talk's settings. Try it at `/signs`.
Text goes through a model that picks and orders glosses, each gloss comes from `wwwroot/signs/lexicon.sigml`, and
[CWASA](https://vh.cmp.uea.ac.uk/index.php/CWASA_Conditions_of_Use) plays the SiGML on a 3D avatar. Without a model it goes
word by word. The model cannot invent a movement: only signs that are in the file reach the avatar, and words without a sign
are shown crossed out.

During a talk the server cuts captions into pieces of up to six words as they are spoken, translates each one once and sends it to
every phone with the avatar on, so more phones cost nothing extra, and nothing is translated while nobody has it on. A piece the
model has not answered in 2.5 s goes word by word. When signing cannot keep up with speech it skips pieces and says so.

The avatar sits in the corner of the video, like an interpreter on a news broadcast. What it signs is kept with the talk at the
moment each piece was said, so the recording signs along at the same moments. Parts nobody watched with the avatar on have no
signs, unless the organizer turns on **Sign every kept talk** in Event setup, which signs every kept talk from start to end at the
cost of more AI calls.

- It knows about 830 signs in HamNoSys from [Avatar-Generator-FYP](https://github.com/SaiSriSatvic2005/Avatar-Generator-FYP-) (MIT). Its project describes its data as Indian Sign Language, but some file names point to British and Australian signs, so treat the language as mixed or unconfirmed. It is not LSM, which would need its own set.
- No one who signs has reviewed the output. Do not present it as accessibility: captions remain the supported way to follow a talk.
- CWASA is loaded unmodified from the University of East Anglia, as its terms ask, so it needs internet on each phone, and only
  loads for people who turn it on. Its licence (CC BY-NC-ND) does not allow commercial use without talking to them.

### Viewers outside the venue, API and widget

Off by default, and it needs a tunnel you run yourself. See [docs/API.md](docs/API.md).

The app is in English unless the person picks another language, on the home page or in the room's settings. It does not
follow the browser language.

## How it fits together

```
Camera phone ──WHIP──▶ MediaMTX ──WebRTC (WHEP)──▶ attendee phones      video never leaves the LAN
                         │
                         ├─ ffmpeg, audio only ─▶ captions ─▶ cloud engine, or local Vosk when offline
                         │                                        │
Slide source ─(WHIP/HDMI)┴─ ffmpeg, 1 fps ─▶ OpenCV change        ▼
                                             detection ─▶ SQLite + full-text search ─▶ SignalR ─▶ attendee phones
                                                  │                    ▲
                                                  ▼                    │
                                       vision model, or Tesseract ─────┘
```

| Area | Code |
| --- | --- |
| Talk lifecycle, pipelines | [src/Server/Core/TalkService.cs](src/Server/Core/TalkService.cs) |
| Cloud to local caption failover | [src/Server/Transcription/TranscriptionManager.cs](src/Server/Transcription/TranscriptionManager.cs) |
| Slide change detection | [src/Server/Slides/SlideDetector.cs](src/Server/Slides/SlideDetector.cs) |
| Grounded answers with citations | [src/Server/Ai/Answerer.cs](src/Server/Ai/Answerer.cs), [src/Server/Ai/Retriever.cs](src/Server/Ai/Retriever.cs) |
| HTTP API, video proxy, captive portal | [src/Server/Web/Endpoints.cs](src/Server/Web/Endpoints.cs) |
| Attendee app | [src/Server/wwwroot/attendee.js](src/Server/wwwroot/attendee.js) |

## AI providers

Set in `.env`. Each one is independent.

| Job | Options | Default |
| --- | --- | --- |
| Captions | `openai` (`gpt-live-transcribe`, streaming), `groq` (`whisper-large-v3-turbo`, 1-2 s blocks), `local` | `openai` |
| Answers, translation | `openai` (`gpt-6-luna`), `anthropic` (`claude-opus-5-5`), `groq` (`llama-3.3-70b-versatile`) | first with a key |
| Reading slides | `openai`, `anthropic` | first with a key |

Claude runs at low effort to stay inside the 10 second answer target, with Anthropic's server-side refusal fallback turned
on. If answers are too slow, `Ai__ChatModel=claude-haiku-5-5` is the faster option.

## Privacy rules as built

- A talk cannot start until the operator confirms that the organizer and the speaker authorized it.
- Video stays on the LAN. Only audio, slide images and transcript text go to the configured provider. The exception needs two
  separate settings, for an organizer who wants viewers outside the venue and has the speaker's agreement.
- Viewers from outside are refused until the organizer turns that on, and operator pages and the operator key never work through
  a tunnel.
- A ticket is optional. A registered email never gets its ticket back from the form, so nobody can take someone else's or find out
  who registered. Attendees can delete their own data, and the organizer can delete everyone's.
- Nothing about a person goes to anyone until that person accepts: a contact swap shows only a first name before accepting, and a
  sponsor only gets people who accepted at its stand, who can withdraw later. Sponsor reports count mentions, not people.
- Reports hide any count below the minimum. Questions to the AI are not stored, so they are never in a report.
- Chat is labelled public, sends only when the person presses publish, and the name is optional. The operator can remove messages.
- Exported CSV cells cannot run as spreadsheet formulas.
- Questions to the AI are not stored or logged, and attendees give no personal data. They never reach the moderator or the
  speaker unless the attendee presses **Send to the speaker**.
- Questions to the speaker are stored with the talk, with no name, address or device, so nobody can tell who asked what. Other
  attendees do not see them. They are deleted with the talk, and their text goes to the configured chat provider to group them.
- Answers come only from the transcript and slides. An answer with no valid citation is discarded and the app says the
  talk has not covered that.
- With "keep" unticked, the transcript and slides are deleted when the talk stops. Past talks can also be deleted from
  the operator panel.
- There is no facial recognition, and the attendee count comes from open connections, not from images.
- Starting talks and publishing video need the operator key. It is inside the camera and slide QR codes, so treat those as
  private and show attendees only the attendee QR.

## Third party code

`wwwroot/lib` includes [hls.js](https://github.com/video-dev/hls.js) 1.5.17 and [jsQR](https://github.com/cozmo/jsQR) 1.4.0 (Apache-2.0), and
the SignalR client. MediaMTX and FFmpeg are downloaded separately.

The signing avatar loads [CWASA](https://vh.cmp.uea.ac.uk/index.php/CWASA_Conditions_of_Use) from the University of East Anglia when someone turns it on (CC BY-NC-ND, not bundled). `wwwroot/signs/lexicon.sigml` holds signs from Avatar-Generator-FYP, with its MIT licence inside the file.

If another program is already using port 8080 or 8443, TalkLens moves to the next free one and says so when it starts. A local
Kubernetes load balancer is a common cause. Windows would otherwise let both listen, and phones would reach either at random.

## Tests

Build to a short path if the checkout path is long.

```bash
dotnet test tests/Tests
```

With Elasticsearch running, `TALKLENS_LIVE_ELASTIC=1 dotnet test tests/Tests` also runs the test against the real thing, in its own
indices that it removes.

End to end, against a running server (no phone needed; Windows, for the speech generator):

```bash
cd tests/e2e && npm install
powershell -ExecutionPolicy Bypass -File make-demo-talk.ps1
node e2e.mjs
node event.mjs <path to the running copy> <video to publish>
```

To simulate cloud providers and an internet outage without keys, start `node fake-ai.mjs`, run the server with the settings
below, then run `node outage.mjs`.

```bash
OPENAI_API_KEY=fake GROQ_API_KEY=fake Ai__TranscriptionProvider=groq Ai__ChatProvider=groq Ai__VisionProvider=openai Ai__GroqBaseUrl=http://127.0.0.1:9100 Ai__OpenAiBaseUrl=http://127.0.0.1:9100 Ai__ConnectivityProbeUrl=http://127.0.0.1:9100/models dotnet run --project src/Server
```
