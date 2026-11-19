const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { execFileSync } = require('node:child_process');
const { ACTIONS, PALETTES } = require('../src/settings');

const root = path.resolve(__dirname, '..', '..');
const out = path.join(root, 'build', 'streamdeck', 'io.github.genkis3zu.aiusagemanager.sdPlugin');
const FILES = ['manifest.json', 'bin/plugin.cjs', 'ui/settings.html', 'ui/settings.js', 'imgs/plugin.svg', 'imgs/category.svg', 'imgs/action.svg', 'imgs/key.svg', 'LICENSE'];
const listFiles = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? listFiles(path.join(dir, e.name)).map(f => e.name + '/' + f) : [e.name]);

test('build writes exactly the plugin files', () => {
  fs.mkdirSync(out, { recursive: true }); fs.writeFileSync(path.join(out, 'stale.txt'), 'old');
  execFileSync(process.execPath, [path.join(root, 'tools', 'build-streamdeck.cjs')], { cwd: root, stdio: 'pipe' });
  assert.deepEqual(listFiles(out).sort(), [...FILES].sort());
});
test('manifest has the plugin identity, Node 24 and three key actions', () => {
  const m = JSON.parse(fs.readFileSync(path.join(out, 'manifest.json'), 'utf8'));
  assert.equal(m.UUID, 'io.github.genkis3zu.aiusagemanager'); assert.equal(m.Name, 'AI Usage Manager'); assert.equal(m.Version, '1.1.1.0');
  assert.equal(m.CodePath, 'bin/plugin.cjs'); assert.deepEqual(m.Nodejs, { Version: '24' }); assert.equal(m.SDKVersion, 2);
  assert.equal(m.Software.MinimumVersion, '7.6'); assert.deepEqual(m.OS, [{ Platform: 'windows', MinimumVersion: '10' }]);
  assert.equal(m.Icon, 'imgs/plugin'); assert.equal(m.CategoryIcon, 'imgs/category'); assert.equal(m.Category, 'AI Usage Manager');
  assert.deepEqual(m.Actions.map(a => [a.UUID, a.Name]), Object.entries(ACTIONS).map(([uuid, name]) => [uuid, name + ' 使用量']));
  for (const a of m.Actions) {
    assert.deepEqual(a.Controllers, ['Keypad']); assert.equal(a.PropertyInspectorPath, 'ui/settings.html'); assert.equal(a.Icon, 'imgs/action');
    assert.equal(a.SupportedInMultiActions, false); assert.equal(a.UserTitleEnabled, false); assert.deepEqual(a.States, [{ Image: 'imgs/key', ShowTitle: false }]);
  }
});
test('the bundle loads without connecting and exports the plugin', () => {
  const bundle = require(path.join(out, 'bin', 'plugin.cjs'));
  assert.equal(typeof bundle.createPlugin, 'function'); assert.equal(typeof bundle.main, 'function');
});
test('the bundle has no network or credential access', () => {
  const text = fs.readFileSync(path.join(out, 'bin', 'plugin.cjs'), 'utf8').replaceAll('http://www.w3.org/2000/svg', '');
  for (const banned of ['http://', 'https://', 'node:http', 'node:https', 'node:tls', 'node:dns', 'fetch(', '.credentials.json', 'auth.json'])
    assert.ok(!text.includes(banned), banned);
});
test('the settings panel offers every palette and loads nothing external', () => {
  const html = fs.readFileSync(path.join(out, 'ui', 'settings.html'), 'utf8'), js = fs.readFileSync(path.join(out, 'ui', 'settings.js'), 'utf8');
  for (const p of PALETTES) { assert.ok(js.includes(`'${p.id}'`), p.id); assert.ok(js.includes(`'${p.label}'`), p.label); }
  assert.doesNotMatch(html + js, /https?:\/\//);
});
