// Stand-in for the cloud AI providers, so the cloud paths and outages can be exercised without API keys.
// It answers like Groq and OpenAI Chat Completions. It does not judge real model quality.
// POST /control/down makes it refuse connections for the given seconds, to simulate the hotspot dying.
import { createServer } from 'node:http';

const port = Number(process.env.FAKE_AI_PORT ?? 9100);
let requests = { transcribe: 0, chat: 0, vision: 0 };
let server;

function json(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

function handle(req, res) {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const body = Buffer.concat(chunks);
    if (req.url === '/models') return json(res, 200, { data: [] });
    if (req.url === '/stats') return json(res, 200, requests);
    if (req.url.startsWith('/control/down')) {
      const seconds = Number(new URL(req.url, 'http://x').searchParams.get('seconds') ?? 10);
      json(res, 200, { down: seconds });
      goDown(seconds);
      return;
    }
    if (req.url === '/audio/transcriptions') {
      requests.transcribe++;
      return json(res, 200, { text: 'fragmento en la nube numero ' + requests.transcribe });
    }
    if (req.url === '/chat/completions') {
      const payload = JSON.parse(body.toString('utf8'));
      const user = payload.messages[1].content;
      let content;
      if (Array.isArray(user)) {
        requests.vision++;
        content = 'Diagrama de red: router conectado a celulares.';
      } else if (/Caption to translate|^(?!Excerpts)/.test(user) && !user.startsWith('Excerpts')) {
        requests.chat++;
        content = 'translated: ' + user.split('\n').pop().replace('Caption to translate: ', '');
      } else {
        requests.chat++;
        content = /zanahoria/.test(user) ? 'NOT_FOUND' : 'Respuesta sobre la charla [1].';
      }
      return json(res, 200, { choices: [{ message: { role: 'assistant', content } }] });
    }
    json(res, 404, {});
  });
}

function goUp() {
  server = createServer(handle);
  server.listen(port, '127.0.0.1');
}

function goDown(seconds) {
  server.close();
  server.closeAllConnections();
  setTimeout(goUp, seconds * 1000);
}

goUp();
console.log(`Fake AI on http://127.0.0.1:${port}`);
