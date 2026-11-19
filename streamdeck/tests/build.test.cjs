const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { execFileSync } = require('node:child_process');
const { ACTIONS, STATUS_ACTION, PALETTES } = require('../src/settings');

const root = path.resolve(__dirname, '..', '..');
const out = path.join(root, 'build', 'streamdeck', 'io.github.genkis3zu.aiusagemanager.sdPlugin');
const FILES = ['manifest.json', 'bin/plugin.cjs', 'ui/settings.html', 'ui/settings.js', 'imgs/plugin.svg', 'imgs/category.svg', 'imgs/action.svg', 'imgs/key.svg', 'LICENSE'];
const listFiles = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? listFiles(path.join(dir, e.name)).map(f => e.name + '/' + f) : [e.name]);

test('build writes exactly the plugin files', () => {
  fs.mkdirSync(out, { recursive: true }); fs.writeFileSync(path.join(out, 'stale.txt'), 'old');
  execFileSync(process.execPath, [path.join(root, 'tools', 'build-streamdeck.cjs')], { cwd: root, stdio: 'pipe' });
  assert.deepEqual(listFiles(out).sort(), [...FILES].sort());
});
test('manifest has the plugin identity, Node 24, three usage keys and the AIの状況 key', () => {
  const m = JSON.parse(fs.readFileSync(path.join(out, 'manifest.json'), 'utf8'));
  assert.equal(m.UUID, 'io.github.genkis3zu.aiusagemanager'); assert.equal(m.Name, 'AI Usage Manager'); assert.equal(m.Version, '1.2.1.0');
  assert.equal(m.CodePath, 'bin/plugin.cjs'); assert.deepEqual(m.Nodejs, { Version: '24' }); assert.equal(m.SDKVersion, 2);
  assert.equal(m.Software.MinimumVersion, '7.6'); assert.deepEqual(m.OS, [{ Platform: 'windows', MinimumVersion: '10' }]);
  assert.equal(m.Icon, 'imgs/plugin'); assert.equal(m.CategoryIcon, 'imgs/category'); assert.equal(m.Category, 'AI Usage Manager');
  assert.deepEqual(m.Actions.map(a => [a.UUID, a.Name]), [...Object.entries(ACTIONS).map(([uuid, name]) => [uuid, name + ' 使用量']), [STATUS_ACTION, 'AIの状況']]);
  for (const a of m.Actions) {
    // The AIの状況 key has nothing to set, so it has no settings panel.
    assert.equal(a.PropertyInspectorPath, a.UUID === STATUS_ACTION ? undefined : 'ui/settings.html', a.UUID);
    assert.deepEqual(a.Controllers, ['Keypad']); assert.equal(a.Icon, 'imgs/action');
    assert.equal(a.SupportedInMultiActions, false); assert.equal(a.UserTitleEnabled, false); assert.deepEqual(a.States, [{ Image: 'imgs/key', ShowTitle: false }]);
  }
});
test('the bundle loads without connecting and exports the plugin', () => {
  const bundle = require(path.join(out, 'bin', 'plugin.cjs'));
  assert.equal(typeof bundle.createPlugin, 'function'); assert.equal(typeof bundle.main, 'function');
});
test('the bundle has no network or credential access', () => {
  const raw = fs.readFileSync(path.join(out, 'bin', 'plugin.cjs'), 'utf8');
  // The two status pages are only handed to Stream Deck to open in the browser; the plugin never requests them.
  assert.ok(raw.includes("'https://status.claude.com/'") && raw.includes("'https://status.openai.com/'"));
  const text = raw.replaceAll('http://www.w3.org/2000/svg', '').replaceAll("'https://status.claude.com/'", '').replaceAll("'https://status.openai.com/'", '');
  for (const banned of ['http://', 'https://', 'wss://', 'node:http', 'node:https', 'node:tls', 'node:dns', 'node:dgram', 'fetch(', 'XMLHttpRequest', '.credentials.json', 'auth.json'])
    assert.ok(!text.includes(banned), banned);
  // The only WebSocket address is Stream Deck's own on this PC.
  const sockets = [...text.matchAll(/ws:\/\/[^'"`\s]*/g)].map(m => m[0]);
  assert.ok(sockets.length > 0 && sockets.every(s => s.startsWith('ws://127.0.0.1:')), sockets.join(' '));
  // node:net is used only for the named pipe (connect with a path, never host/port).
  assert.ok(!/host\s*:/.test(text) && !/\.connect\(\s*\d/.test(text));
});
test('the settings panel offers every palette and loads nothing external', () => {
  const html = fs.readFileSync(path.join(out, 'ui', 'settings.html'), 'utf8'), js = fs.readFileSync(path.join(out, 'ui', 'settings.js'), 'utf8');
  for (const p of PALETTES) { assert.ok(js.includes(`'${p.id}'`), p.id); assert.ok(js.includes(`'${p.label}'`), p.label); }
  assert.doesNotMatch(html + js, /https?:\/\//);
});
