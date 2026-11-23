const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { ACTION, messagePath, takeMessage, sendToClaudian } = require('../src/claudian');

const token = '0123456789abcdef0123456789abcdef';
function folder() { return fs.mkdtempSync(path.join(os.tmpdir(), 'claudian-test-')); }
function leave(dir, data) { const file = path.join(dir, 'claudian-message.json'); fs.writeFileSync(file, typeof data === 'string' ? data : JSON.stringify(data)); return file; }

test('the address and the message file are fixed', () => {
  assert.equal(ACTION, 'ai-usage-manager-claudian');
  assert.equal(messagePath({ LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' }), path.join('C:\\Users\\a\\AppData\\Local', 'AIUsageManager', 'claudian-message.json'));
  assert.equal(messagePath({}), null);
});

test('the text is taken once, only with its token, and the file is removed', () => {
  const dir = folder(), now = 1_800_000_000_000;
  const file = leave(dir, { token, text: '  見積の表を直している ', created: now - 1000 });
  assert.equal(takeMessage('ffffffffffffffffffffffffffffffff', file, now), null);
  assert.equal(fs.existsSync(file), true, 'a wrong token leaves the file for the right one');
  assert.equal(takeMessage(token, file, now), '見積の表を直している');
  assert.equal(fs.existsSync(file), false);
  assert.equal(takeMessage(token, file, now), null, 'the same link does nothing the second time');
  fs.rmSync(dir, { recursive: true });
});

test('a link without a well-formed token never reads the file', () => {
  let read = false;
  const files = { statSync() { read = true; return { isFile: () => true, size: 10 }; }, readFileSync() { read = true; return '{}'; }, unlinkSync() {} };
  for (const bad of [undefined, '', 'abc', token.toUpperCase(), token + '0', '../' + token]) assert.equal(takeMessage(bad, 'x', 0, files), null);
  assert.equal(read, false);
});

test('old, future, empty, long, broken and oversized messages are not sent', () => {
  const dir = folder(), now = 1_800_000_000_000;
  const cases = [
    { token, text: 'x', created: now - 120001 },
    { token, text: 'x', created: now + 6000 },
    { token, text: 'x', created: 'now' },
    { token, text: '   ', created: now },
    { token, text: 'あ'.repeat(1001), created: now },
    { token, text: 5, created: now },
  ];
  for (const data of cases) { const file = leave(dir, data); assert.equal(takeMessage(token, file, now), null); assert.equal(fs.existsSync(file), false); }
  let file = leave(dir, '{not json');
  assert.equal(takeMessage(token, file, now), null);
  file = leave(dir, JSON.stringify({ token, text: 'x'.repeat(20000), created: now }));
  assert.equal(takeMessage(token, file, now), null);
  assert.equal(takeMessage(token, path.join(dir, 'none.json'), now), null);
  fs.rmSync(dir, { recursive: true });
});

function app(steps, { activate } = {}) {
  let count = 0;
  const sent = [];
  const input = { sendMessage(options) { sent.push(options); return Promise.resolve(); } };
  return {
    sent,
    plugins: { plugins: { claudian: { activateView: activate || (async () => {}) } } },
    workspace: { getLeavesOfType(type) { assert.equal(type, 'claudian-view'); count++; return count < steps ? [] : [{ view: { getActiveTab: () => ({ controllers: { inputController: input } }) } }]; } },
  };
}

test('the text goes to the active Claudian chat as a new message, once the chat is ready', async () => {
  const a = app(3);
  const waits = [];
  assert.equal(await sendToClaudian(a, '見積の表', ms => { waits.push(ms); return Promise.resolve(); }), '');
  await Promise.resolve(); await Promise.resolve();
  assert.deepEqual(a.sent, [{ content: '見積の表' }]);
  assert.deepEqual(waits, [100, 100]);
});

test('without Claudian, or when its chat never gets ready, the reason is returned and nothing is sent', async () => {
  assert.match(await sendToClaudian({ plugins: { plugins: {} } }, 'x'), /Claudian が見つかりません/);
  assert.match(await sendToClaudian(app(1, { activate: async () => { throw new Error('no'); } }), 'x'), /開けませんでした/);
  const never = { plugins: { plugins: { claudian: { activateView: async () => {} } } }, workspace: { getLeavesOfType: () => [{ view: { getActiveTab: () => ({ controllers: {} }) } }] } };
  let waits = 0;
  assert.match(await sendToClaudian(never, 'x', () => { waits++; return Promise.resolve(); }), /版が変わった/);
  assert.equal(waits, 30);
});
