const { test } = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const { userSid, pipeName, readSnapshot } = require('../src/pipe');

const uniqueName = () => `\\\\.\\pipe\\AIUsageManager.StreamDeckTest.${process.pid}.${Math.random().toString(16).slice(2)}`;
function serve(name, onConnection) {
  const sockets = new Set();
  const server = net.createServer(socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); onConnection(socket); });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(name, () => resolve({ close: () => new Promise(done => { for (const s of sockets) s.destroy(); server.close(() => done()); }) }));
  });
}

test('reads one snapshot', async () => {
  const name = uniqueName(), server = await serve(name, s => s.end('{"a":1}'));
  try { assert.deepEqual(await readSnapshot(name), { kind: 'ok', json: '{"a":1}' }); } finally { await server.close(); }
});
test('a pipe that appears within the retry window is read', async () => {
  const name = uniqueName(); let server;
  const started = new Promise(resolve => setTimeout(async () => { server = await serve(name, s => s.end('{}')); resolve(); }, 600));
  try { assert.equal((await readSnapshot(name, { retryMs: 2000 })).kind, 'ok'); } finally { await started; await server.close(); }
});
test('a pipe that never appears is absent after the window', async () => {
  const start = Date.now();
  assert.deepEqual(await readSnapshot(uniqueName(), { retryMs: 500 }), { kind: 'absent' });
  assert.ok(Date.now() - start >= 480);
});
test('a server that never ends is failed', async () => {
  const name = uniqueName(), server = await serve(name, () => {});
  try { assert.deepEqual(await readSnapshot(name, { timeoutMs: 300 }), { kind: 'failed' }); } finally { await server.close(); }
});
test('more than 64 KiB is failed', async () => {
  const name = uniqueName(), server = await serve(name, s => s.end(Buffer.alloc(65537, 0x20)));
  try { assert.deepEqual(await readSnapshot(name), { kind: 'failed' }); } finally { await server.close(); }
});
test('invalid UTF-8 is failed', async () => {
  const name = uniqueName(), server = await serve(name, s => s.end(Buffer.from([0xff])));
  try { assert.deepEqual(await readSnapshot(name), { kind: 'failed' }); } finally { await server.close(); }
});
test('empty output is failed', async () => {
  const name = uniqueName(), server = await serve(name, s => s.end());
  try { assert.deepEqual(await readSnapshot(name), { kind: 'failed' }); } finally { await server.close(); }
});
test('null name is absent without connecting', async () => {
  assert.deepEqual(await readSnapshot(null, { connect: () => { throw new Error('unexpected connect'); } }), { kind: 'absent' });
});
test('userSid parses whoami CSV', () => {
  let call;
  assert.equal(userSid((...args) => { call = args; return '"pc\\user","S-1-5-21-1-2-3-1001"\r\n'; }), 'S-1-5-21-1-2-3-1001');
  // An absolute System32 path: a bare name can resolve to another whoami on PATH (e.g. Git's coreutils).
  assert.equal(call[0], require('node:path').win32.join(process.env.SystemRoot, 'System32', 'whoami.exe')); assert.deepEqual(call[1], ['/user', '/fo', 'csv', '/nh']);
  assert.equal(userSid(() => '"pc\\user","bad"'), null);
  assert.equal(userSid(() => { throw new Error('missing'); }), null);
});
test('pipeName uses the tray app snapshot prefix', () => {
  assert.equal(pipeName('S-1-5-21-1'), '\\\\.\\pipe\\AIUsageManager.Snapshot.S-1-5-21-1');
  assert.equal(pipeName(null), null);
});
