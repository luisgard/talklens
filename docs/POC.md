# Proof of concept status

The proof of concept answers one question: is the 5 second limit met on a modest laptop? Gate 1 needs the checks below on
real phones and a real router. **Gate 1 has not been passed yet.**

## Measured so far

Measured on one development machine, with the server, publisher and viewer all on loopback, and a synthetic Spanish talk
(text-to-speech voice and generated slides). There was no Wi-Fi, no phone and no room noise, so treat these as a best case.

| Target | Result | How |
| --- | --- | --- |
| RNF-02: video under 1 s | about 45 ms p50, 71 ms max | WHIP to MediaMTX to WHEP in one browser tab, timestamps encoded in the frames |
| RNF-03: captions 2 to 3.5 s (local model) | first words about 0.8 s after a sentence starts, final caption about 0.6 s p50 and 0.8 s max after it ends | `tests/e2e/e2e.mjs`, against known speech timing |
| RF-06: slide change | every change detected within 3 s, none spurious | `e2e.mjs`, 6 changes |
| RNF-05: answers under 10 s (excerpts) | under 50 ms | `e2e.mjs`. Says nothing about cloud model speed |
| RNF-06: internet loss (simulated provider, 25 s outage) | local model about 4 s after the cut, back to cloud about 3 s after the link returned, longest gap between captions 5.2 s | `tests/e2e/outage.mjs` |
| RNF-07: laptop load | about 330 MB of memory; server process CPU under 1% (ffmpeg and MediaMTX not included) | Operator panel |

The local Spanish model understood the demo talk but made word errors, for example "el autor del evento" for "el router del
evento". Real accuracy in a room is unknown.

## First real phone test (2026-11-01)

One Android phone publishing its camera and microphone over a home Wi-Fi network to the laptop, in a 63 second talk with the
local Spanish model. No event router, no hotspot and no cloud provider.

- The camera page worked from the phone: the laptop received 720x1280 video and the speech came through as captions.
- Captions were usable but rough, in the tester's words "not the best, but it works". Some lines were clear ("es una prueba
  funciona") and some were not ("prepare germina", "más de bien").
- Scanning the camera QR asked for the operator key by hand, although the QR contains it. Typing the key worked. The same
  link with the key signs in from the laptop, so the cause is still unknown.
- The camera stopped sending once with its page still open, and sending had to be started again by hand. The cause is
  unknown. The page has since gained automatic reconnect (see below).
- A panel tab left open did not update its past talks list: it still said "No talks yet" after the talk ended, while the
  server had the talk. The list only reloads after an action in that same tab or a page load.

Delay on the same setup, measured with `/measure.html` while the phone camera filmed the clock on the laptop screen. Viewing
was on the laptop, so the Wi-Fi hop to an attendee phone is not included.

| Target | Result | How |
| --- | --- | --- |
| RNF-02: video under 1 s | about 0.24 s (7 captures, 0.2 to 0.25 s) | Clock on screen against its image in the video, read from screenshots. Includes capture, encoding, Wi-Fi and decoding |
| RNF-03: captions 2 to 3.5 s (local model) | first words about 0.75 s (max 0.84 s), final caption about 0.74 s after the sentence ends (max 0.98 s) | 8 sentences read by a synthetic voice on the laptop speakers, heard by the phone mic |

This was a clear synthetic voice in a quiet room, so it is still a good case. 5 of the 8 sentences had no word errors, better
than the tester's own voice in the talk above.

## Translation with a real model (2026-11-02)

Spanish to English with `claude-haiku-5-5`, captions from the local model, on home internet. Three runs of the synthetic demo
talk with the attendee page open on the laptop, not on a phone.

| Run | Translated | Delay after the final caption |
| --- | --- | --- |
| Fresh server | 7 of 8. One stayed in Spanish and three arrived together, late | Not measured per caption |
| Same server, second pass | 8 of 8 | 626 to 744 ms |
| Fresh server again | 7 of 7 | 571 to 839 ms |

- The late burst in the first run did not repeat, including on the second fresh start. Its cause is unknown. That run only
  logged what the page showed, so it cannot say where the time went.
- Word errors from the local model carry over. In the two runs that heard "el video nunca se y de la red local", it came back
  half in Spanish as "the video never se y de la red local". "router" heard as "daughter" became "the event's daughter".
- With an earlier, looser prompt, the model commented on fragments or returned them in Spanish. The current prompt fixed that
  on the tester's real transcript (3 replays of 20 captions), apart from a lone "su".
- 20 captions cost about 4314 input and 258 output tokens, about $0.0006.

## OpenAI captions (2026-11-02)

One pass of the synthetic demo talk through `gpt-live-transcribe` on home internet, published from the same laptop. No real
voice yet and no hotspot.

| Target | Result | How |
| --- | --- | --- |
| RNF-03: captions | first words about 0.5 to 0.85 s after a sentence starts, final caption within about 0.5 s of its end | Hub arrival against the known sentence times, counted from just before ffmpeg started, so about 0.5 s of error |
| RF-10: translation | 8 of 8, about 0.6 s, with two at 1.1 and 1.5 s | Final caption to translation at the hub |

- The real client connected and stayed in cloud mode on its first real run, with no code changes.
- Word errors on this voice were not clearly fewer than with the local model: "router" became "avatar" and "sale de" became
  "se hilde". The opening words of the first sentence were lost, as they were with the local model. Lines came back as whole
  sentences with punctuation.
- With the OpenAI key added, `auto` would move answers and slides to OpenAI, so keeping Claude needs `Ai__ChatProvider` and
  `Ai__VisionProvider` set to `anthropic`.

Then the tester's own voice from the phone, about 3.5 minutes, 36 lines. Not good enough yet, for two reasons:

- **False fallbacks.** The server left the cloud 4 times with "cloud stopped responding" while the internet was up, so 9 of the
  36 lines came from the local model, including the worst ones. A replayed line can also overlap a cloud line. The exact trigger
  is not logged. One suspect: any sound over the voice threshold starts the 6 s stall clock, and only a non-empty transcript
  stops it.
- **Fragments.** A 0.5 s pause ends a turn, and each turn is translated alone. "Como que lo paso primero en español y luego lo
  pasa." / "A inglés y" came out as "he goes through it first in Spanish and then he passes it on." / "to English and". Replaying
  6 groups of lines through Claude as whole sentences gave better English in most, for example "it translates it into Spanish
  first and then into English", though it can still go wrong: "Y por eso no" / "Le doy un siete." became "And that's why I
  don't give it a seven."

Fixes made after that talk, not yet tried with a real voice:

- **False fallbacks.** Reproduced with a clip of short noise bursts: the old build left the cloud twice in 42 s. The stall clock
  now starts when a turn is committed and stops on any answer, including an empty one. The same clip then ran with no fallback,
  and so did the two runs of 35 s below.
- **Fragments.** A turn now ends after a 0.8 s pause, not 0.5 s. Pieces wait up to 3 s for one that ends a sentence, then are
  translated together and shown in order.

Measured with synthetic sentences cut in half by a 1.3 s silence, two runs of 35 s, on OpenAI and Claude:

| Check | Result |
| --- | --- |
| Translated | 19 of 19 pieces, in order |
| Joined | 7 of the 12 translations held more than one piece |
| Delay from the first piece's final caption | up to about 3.8 s for a piece that waited; 0.6 to 1.2 s for one that did not |

- The final caption arrives about 1 s after the words end, because of the 0.8 s pause. So the worst case is about 4.9 s from
  the words to the translation, close to the 5 s limit, with no margin for a slower network.
- Joining does not rescue every line, and cutting a word in half made the clip harsher than speech: "El video nunca se ildel /
  Red local." came out as "The video never se ildel Red local."

## Camera reconnect (2026-11-04)

The camera page now reconnects by itself and keeps retrying until someone presses Stop. It keeps the same camera if it is still
live, and asks for it again if the phone took it away. A rejected operator key stops the retries and asks for the QR again.

Measured with `tests/e2e/reconnect.mjs` and a one-off outage run: Chrome on the laptop with a fake camera, over loopback. No
phone and no real Wi-Fi cut.

| Break | Camera back after | Attendee |
| --- | --- | --- |
| Server drops the camera session | 13 s, because the browser takes about 8 s to notice | Moving again 3 s later |
| Camera track ends | 2 s | Moving again about 9 s later |
| Server down, about 25 s | 29.6 s, under 3 s after it was back | Moving again 3 s later |
| Stop pressed | Stays stopped after 7 s | Not checked |

- The audio pipe was receiving again after the first two breaks. Captions across a reconnect were not checked with real speech.
- The cause of the stop seen on the phone is still unknown, so this recovers from it but does not prevent it.
- A real Wi-Fi cut and a locked phone screen still need a try. Headless Edge sent no video here, only audio, and it was not
  looked into further. Chrome sent H.264.

## Several rooms and the new interface (2026-11-04)

Rooms were built ahead of their phase (RF-14), because talks that overlap are central to the idea. Each room has its own camera,
captions, transcript, translation and questions, and an event home page lists them. The home page shows the number of people
following each room, as a total only, and offers a summary of any talk (a first part of RF-33 and RF-34). Full rooms, moving
back 2 minutes in the video and the organizer panel are not built.

`tests/e2e/rooms.mjs`, two clips published over RTSP from the laptop at the same time, on OpenAI and Claude. All checks passed:

- Both talks ran together, and each attendee only got captions from their own room. Starts and ends reached only that room.
- Transcripts stayed apart, and stopping one talk left the other running.
- A second talk in a busy room and a talk in a room that does not exist are refused. A room with a live talk cannot be removed.

Server process with two rooms: about 340 MB and under 1% CPU. That leaves out ffmpeg and MediaMTX, had no slide detection, and is
not a modest laptop, so it says little about how many rooms fit. The publishers came from the laptop, so no phone or Wi-Fi was
involved, and video to attendees in two rooms at once has not been measured.

The interface is new: a home page, a simpler room page, a panel with one tile per room and three setup steps, and a glass look. It
was checked by screenshots in Chrome at phone and desktop sizes, with no horizontal scroll at 320 px and no page errors. It has
not been tried on a real phone, so smoothness of the blur on a modest phone is unknown, and iPhone is still untried. Captions stay
on solid dark over the video so they remain readable. High contrast, reduced transparency and browsers without blur get plain
surfaces. Screen reader use is still untested.

## Questions for the speaker (2026-11-04)

RF-18 and RF-29 are built. An attendee chooses Ask the AI (the default, private) or Ask the speaker, or forwards an AI question
with a button. The question reaches the moderator at once and is grouped right after: same wording locally, same meaning with the
chat model, within 5 s. If the model fails or is offline, the question simply stays on its own. The moderator picks one to show
on the speaker screen, and only one is shown at a time.

`tests/e2e/questions.mjs` passed against the real server with Claude Haiku: the access rules, isolation between rooms, grouping of
a repeat and a Spanish paraphrase of an English question, a different question left apart, the moderator actions and deletion with
a talk not kept. It also confirmed English by default on a browser set to Spanish, and that choosing Spanish carries across pages.
That is one paraphrase, on the laptop, so how well grouping does with real audience questions is unknown.

## The event around the talks (2026-11-04)

All of it ran on one laptop in headless Chrome, with a looping demo video and no real speech. `tests/e2e/event.mjs` passes
(113 checks, including the privacy cases), along with 106 unit tests and `questions.mjs` (31 checks).

| What ran | Result |
| --- | --- |
| Tickets, door, CSV, contacts, sponsors, chat, hallway, library, reports, deleting data | Pass, through the API and pages |
| Outside access, simulated with a forwarding header | Closed by default, operator always closed, video only with its own setting. No real tunnel |
| Back 2 min | Plays the HLS copy from behind the live point and returns to WebRTC |
| Clip from a recorded talk | Comes out vertical. Its captions were not looked at, since the demo had no speech |
| Cloud voice | The model name answered 200 on one call to OpenAI. Not heard through a phone |
| Luma | Against a stub written from Luma's docs, not a real account |

The test found a bug only a browser could show: the chat rewrote the labels on the page.

## Watching later and search by meaning (2026-11-04)

Passes on one laptop: 121 unit tests and `event.mjs` (126 checks), with headless Chrome playing the recording from a linked minute.

| What ran | Result |
| --- | --- |
| Whole talk recorded, prepared and played | H264 and AAC, seeks, link expires and cannot be changed, closed from outside without its setting |
| `text-embedding-3-small` at 512 dimensions, 8 questions against 4 texts | Right text scored 0.36 to 0.51, anything else at most 0.24. `MinScore` is 0.3. A small sample: watch it with real talks |
| Real server and models, two seeded talks in English, questions in Spanish | Found by meaning, an unrelated question found nothing and the AI answered `not_found`, deleting a talk removed its vectors |

Not tried: a long real talk, a camera that drops during a recording, or Safari on iPhone playing the video.

## Elasticsearch (2026-11-04)

Passes on one laptop: 130 unit tests, including the one against real Elasticsearch, `event.mjs` (148 checks), `questions.mjs` (31) and `rooms.mjs` (19).

Measured on the i5-1235U laptop with 16 GB, Docker Desktop on WSL, `elasticsearch:9.5.3` with a 1 GB heap and the Basic license.
The load is one day of an event: 960 chunks of 90 words with 512-dimension vectors.

| What | Result |
| --- | --- |
| Memory | Elasticsearch 1.55 GB, with Docker's virtual machine and Desktop about 2.9 GB in total. TalkLens itself is 150 to 200 MB |
| Disk | 2.6 GB for the image. The day's index is 2.4 MB |
| CPU | Under 1% idle, about 2.5 cores while starting |
| Index the whole day | 6 s |
| Search by words | Median 23 ms, 95% under 120 ms |
| Search by vector | Median 30 ms, 95% under 185 ms |
| Real model and server, 3 sessions | "something on eBPF and security" and "algo de seguridad en linux" found the kernel session by meaning, "mantainer" found its session by words despite the typo, and "recipe for mole poblano" found nothing |
| Stopped, wiped or deleted | The test with Elasticsearch running checks a deleted talk disappears and a wiped index fills again without new vectors. Without it, search runs on SQLite |

Each search in the table above takes 260 to 700 ms in total, nearly all of it the query going to OpenAI. Not tried: stopping
Elasticsearch in the middle of a real talk, or a whole real event's worth of talks.

## Not verified

- **The event features on real phones.** Scanning tickets and contact codes with a real camera, the voice, and going back on Safari,
  which uses its own HLS player.
- **Real mail and a real Luma account.** Neither was connected. Nor has a real tunnel been run.
- **Real phones.** iPhone has not been tried. Android has only published from one phone, and watching on a phone has no
  measured delay. Screen sharing has not been tried (RNF-10).
- **Real network.** The event router, Wi-Fi capacity, the hotspot route and the captive portal. Only a home Wi-Fi network has
  been used.
- **Real cloud providers.** Translation has run on Claude, and OpenAI captions on one synthetic talk. Their accuracy on a real
  voice is unmeasured. The Groq client was tested only against stubs written from the provider docs, so the OpenAI versus Groq
  comparison is still open.
- **Translation on a phone.** It has only been watched on the laptop, and only from Spanish to English.
- **Answer quality.** The five test questions need a real model and a real talk.
- **Slide reading.** The vision path ran only against a stub. Tesseract was not installed, so offline OCR has not run.
- **Capture devices.** HDMI cards and mixer inputs use FFmpeg device inputs that were not exercised. Device listing is written
  for Windows DirectShow; Linux lists only `/dev/video*` and the default audio source.
- **Linux and macOS.** `start.sh` has not been run. Everything above ran on Windows.
- **Accessibility.** Controls have labels and captions use a live region, but no screen reader has been tried.

## Remaining checklist

From the spec. Each item needs a laptop, a router and real phones.

- [ ] Publish the camera from a phone browser and watch on 2 or 3 phones through the router; measure the delay
- [ ] Measure caption delay with OpenAI over the hotspot
- [ ] Repeat with Groq and compare delay and accuracy
- [ ] Cut the internet and confirm the local model keeps captions going
- [ ] Detect slide changes from a real projection and read their text
- [ ] Ask 5 test questions about a real talk and review the quality
- [ ] Send questions to the speaker from real phones, and see how well they group
- [ ] Run a small event: check in at the door, swap contacts, listen in another language, go back 2 minutes, make a clip
- [ ] Record the results here, choose a provider and adjust the targets

## Known limits

- Captions can pause for about 5 s at the moment the internet drops, while the switch happens. This sits right at the RNF-01
  limit. The audio from that gap is replayed into the local model, so its words still appear, late.
- The links in answers jump to that moment in the transcript and its slides. They do not seek the video, because Phase 1
  does not record it.
- Each room runs its own transcription, and its own translation for the languages people chose there, so cloud cost grows
  with the number of rooms. How many rooms a modest laptop and one router can hold has not been measured.
- An audio or capture device on the laptop can only serve one room.
- There is no LL-HLS fallback for browsers without WebRTC.
- Slides are dated when the change is detected, up to about 3 s after it happens.
- Only the attendee pages and the home page have a language choice, in English and Spanish. The operator, camera and speaker
  pages are in English only.
- Changing networks needs a server restart, so the certificate and MediaMTX pick up the new address.
