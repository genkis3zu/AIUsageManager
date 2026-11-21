const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { EventEmitter } = require('node:events');
const { locationFile, readAppLocation, resolveAppPath, launchApp } = require('../src/location');

function sandbox() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'aum-sd-'));
  const exe = name => { const file = path.join(dir, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, ''); return file; };
  const record = value => { const file = path.join(dir, 'app-location.json'); fs.writeFileSync(file, typeof value === 'string' ? value : JSON.stringify(value)); return file; };
  return { dir, exe, record, done: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

test('recorded path is used when no manual path is set', () => {
  const s = sandbox();
  try { const app = s.exe('a\\AIUsageManager.exe'); assert.equal(resolveAppPath('', s.record({ schemaVersion: 1, path: app })), app); } finally { s.done(); }
});
test('manual path wins over the recorded path', () => {
  const s = sandbox();
  try {
    const recorded = s.exe('a\\AIUsageManager.exe'), manual = s.exe('b\\AIUsageManager.exe');
    assert.equal(resolveAppPath(manual, s.record({ schemaVersion: 1, path: recorded })), manual);
  } finally { s.done(); }
});
test('invalid or missing manual path falls back to the recorded path', () => {
  const s = sandbox();
  try {
    const recorded = s.exe('a\\AIUsageManager.exe'), file = s.record({ schemaVersion: 1, path: recorded });
    for (const manual of [path.join(s.dir, 'missing', 'AIUsageManager.exe'), 'cmd /c x', '', undefined]) assert.equal(resolveAppPath(manual, file), recorded);
  } finally { s.done(); }
});
test('missing, oversized, non-JSON, wrong version, wrong name, directory and nonexistent records give null', () => {
  const s = sandbox();
  try {
    const app = s.exe('a\\AIUsageManager.exe'), other = s.exe('a\\cmd.exe');
    fs.mkdirSync(path.join(s.dir, 'dir\\AIUsageManager.exe'), { recursive: true });
    assert.equal(readAppLocation(path.join(s.dir, 'none.json')), null);
    const padded = JSON.stringify({ schemaVersion: 1, path: app });
    assert.equal(readAppLocation(s.record(padded + ' '.repeat(4097 - Buffer.byteLength(padded)))), null);
    assert.equal(readAppLocation(s.record('{bad')), null);
    assert.equal(readAppLocation(s.record({ schemaVersion: 2, path: app })), null);
    assert.equal(readAppLocation(s.record({ schemaVersion: 1, path: other })), null);
    assert.equal(readAppLocation(s.record({ schemaVersion: 1, path: path.join(s.dir, 'dir\\AIUsageManager.exe') })), null);
    assert.equal(readAppLocation(s.record({ schemaVersion: 1, path: path.join(s.dir, 'gone\\AIUsageManager.exe') })), null);
    assert.equal(readAppLocation(s.record(padded)), app);
  } finally { s.done(); }
});
test('locationFile is under LOCALAPPDATA only when it is a drive path', () => {
  assert.equal(locationFile({ LOCALAPPDATA: 'C:\\Users\\u\\AppData\\Local' }), 'C:\\Users\\u\\AppData\\Local\\AIUsageManager\\app-location.json');
  assert.equal(locationFile({ LOCALAPPDATA: 'relative\\dir' }), null);
  assert.equal(locationFile({}), null);
});
test('launch passes no arguments, detached, no shell, in the app folder', async () => {
  let call, unref = 0;
  const spawn = (...args) => { call = args; const child = new EventEmitter(); child.unref = () => { unref++; }; queueMicrotask(() => child.emit('spawn')); return child; };
  await launchApp('C:\\Apps\\AIUsageManager.exe', spawn);
  // The app's own folder as working directory: inheriting the plugin folder would lock it against plugin updates.
  assert.deepEqual(call, ['C:\\Apps\\AIUsageManager.exe', [], { shell: false, detached: true, stdio: 'ignore', windowsHide: false, cwd: 'C:\\Apps' }]);
  assert.equal(unref, 1);
});
test('launch passes only the three login arguments and the background start', async () => {
  const calls = [];
  const spawn = (...args) => { calls.push(args[1]); const child = new EventEmitter(); child.unref = () => {}; queueMicrotask(() => child.emit('spawn')); return child; };
  for (const arg of ['--login-claude', '--login-codex', '--login-grok', '--background']) await launchApp('C:\\Apps\\AIUsageManager.exe', spawn, [arg]);
  assert.deepEqual(calls, [['--login-claude'], ['--login-codex'], ['--login-grok'], ['--background']]);
  const never = () => { throw new Error('unexpected spawn'); };
  for (const args of [['--refresh'], ['--show'], ['--login-claude', '--login-codex'], ['--background', '--login-claude'], ['--login-claude x'], 'x'])
    await assert.rejects(launchApp('C:\\Apps\\AIUsageManager.exe', never, args), /Invalid app arguments/);
});
test('launch failure rejects', async () => {
  const spawn = () => { const child = new EventEmitter(); child.unref = () => {}; queueMicrotask(() => child.emit('error', new Error('ENOENT'))); return child; };
  await assert.rejects(launchApp('C:\\Apps\\AIUsageManager.exe', spawn));
});
test('invalid paths never spawn', async () => {
  const spawn = () => { throw new Error('unexpected spawn'); };
  for (const file of ['cmd /c x', '\\\\server\\share\\AIUsageManager.exe', 'C:\\Apps\\other.exe', null]) await assert.rejects(launchApp(file, spawn), /Invalid app path/);
});
