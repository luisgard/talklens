# API and widget for streaming platforms

For a platform that already streams the video and wants captions, translation and questions on top. Video does not need to
leave the venue for any of this.

## Getting access

From outside the venue the server must be reachable through a tunnel you run, such as
`cloudflared tunnel --url http://localhost:8080`. The operator pages stay closed through a tunnel whatever the settings say.

- **The API** needs a key. Create it under **Event setup > Privacy and access**. It works whether or not viewers outside the
  venue are allowed. Creating a new key cancels the old one.
- **The widget** is a public page, so from outside it also needs **Let people outside the venue follow captions, transcript
  and slides** turned on.

## API

Send the key as `X-Api-Key`. Everything is JSON, and CORS is open because the key is what limits access. Only talks kept after
they ended are listed. Questions and summaries spend the event's AI budget, and share its rate limits.

| Request | Returns |
| --- | --- |
| `GET /api/v1/rooms` | Each room, its current talk and how many people are in it |
| `GET /api/v1/talks` | Kept talks |
| `GET /api/v1/talks/{id}/transcript` | Everything said and every slide, with times in ms |
| `POST /api/v1/talks/{id}/ask` with `{"question": "..."}` | An answer and its citations |
| `POST /api/v1/talks/{id}/summary` with `{"language": "en"}` | A one-minute summary and its citations |
| `GET /api/v1/search?q=...` | Matching moments by words and by meaning, grouped by talk. `byMeaning` marks those found only by meaning |

An answer looks like this. `mode` is `ai`, `offline` (matching excerpts, because there is no model or internet), `not_found` or
`empty`. Each `[n]` in the text matches a citation.

```json
{
  "text": "The router carries everything locally [1].",
  "citations": [{ "n": 1, "kind": "segment", "refId": 42, "t": 61000, "text": "..." }],
  "mode": "ai"
}
```

## Find a talk

`GET /api/event/search?q=` needs no key, because the schedule is public. It returns the matching agenda entries, best first, as
`[{ id, byMeaning }]`. `byMeaning` means none of the words matched. Limited to 30 searches by meaning per minute for each client,
then it uses words alone.

## Signing avatar (experimental)

`GET /api/signs` lists the signs it knows. `POST /api/signs/translate` with `{"text":"..."}` returns `tokens` (`word`,
`hasSign`), `sigml` and `byModel`. Limited to 30 a minute per client. See the README for what this is not.

## Widget

Captions and questions for one room, in an iframe with your color:

```html
<div data-talklens-room="1" data-accent="ff5500" data-lang="en"></div>
<script src="https://your-event-address/widget.js"></script>
```

| Attribute | Meaning |
| --- | --- |
| `data-talklens-room` | Room ID. Required |
| `data-accent` | Button color, 6 hex digits |
| `data-lang` | Caption language. Empty means the speaker's language |
| `data-ask="0"` | Hides the question box |
| `data-background="transparent"` | No background, to lay the captions over a player |
| `data-height` | Height in pixels. 260 by default |

The widget carries no TalkLens branding. It is also available directly as `/embed?room=1&accent=ff5500`.
