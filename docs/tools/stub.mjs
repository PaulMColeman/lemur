// A stand-in Scriberr + OpenAI-style server for the manual's screenshots, on 127.0.0.1:18080 only.
// It answers the connection test, holds every summary request open so a meeting stays "Summarizing…", and answers
// the summary model's title requests ("Suggest a title" when renaming) with a made-up title.
// Request headers carry the demo's API key: never logged.
import http from 'node:http';

const json = (res, body) => {
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
};
const held = [];
const server = http.createServer((req, res) => {
  const url = req.url.split('?')[0];
  if (req.method === 'GET' && url === '/api/v1/profiles/') {
    return json(res, [{ id: 'demo', name: 'Meeting (Parakeet + pyannote)', is_default: true, parameters: {} }]);
  }
  if (req.method === 'GET' && (url === '/v1/models' || url === '/models')) {
    return json(res, { object: 'list', data: [{ id: 'meeting-summary', object: 'model' }] });
  }
  if (req.method === 'POST' && (url === '/v1/chat/completions' || url === '/chat/completions')) {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      const onboarding = body.includes('onboarding');
      const answer = onboarding
        ? { title: 'Onboarding review: optional calendar step', about: 'The calendar step becomes optional; the invite step ships with a share link first.' }
        : { title: 'Project check-in and next steps', about: 'The project is on track; legal still has to sign the vendor contract.' };
      setTimeout(() => json(res, { choices: [{ message: { content: JSON.stringify(answer) }, finish_reason: 'stop' }] }), 1500);
    });
    return;
  }
  if (req.method === 'POST' && url.startsWith('/api/v1/summarize')) {
    req.resume();
    held.push(res);
    console.log(`summarize request held (${held.length})`);
    return;
  }
  console.log(`404 ${req.method} ${url}`);
  res.writeHead(404);
  res.end();
});
server.requestTimeout = 0;
server.headersTimeout = 60_000;
server.listen(18080, '127.0.0.1', () => console.log('stub listening on 127.0.0.1:18080'));
