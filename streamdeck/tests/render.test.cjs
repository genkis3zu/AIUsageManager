const { test } = require('node:test');
const assert = require('node:assert/strict');
const { initialConnection, nextConnection } = require('../src/state');
const { ACTIONS, PALETTES, STYLES, defaultSettings, normalizeKeySettings, nextStyle, nextPalette } = require('../src/settings');
const { keyView, renderKey } = require('../src/render');

const RECORDED = '2026-11-19T00:00:00.000Z', NOW = Date.parse(RECORDED) + 60000, LATER_RESET = '2026-11-21T00:00:00.000Z';
const win = (label, percent, resetsAt = LATER_RESET) => ({ label, percent, resetsAt, pace: null, alert: null });
function snap({ claude = [10, 20], codex = 30, grok = 40, recordedAt = RECORDED, source = 'オンライン', status = '', needsLogin = {}, refreshing = false, resetsAt = LATER_RESET, claudeResets = [resetsAt, resetsAt] } = {}) {
  const service = (name, windows) => ({ name, plan: 'Pro', source, status, needsLogin: needsLogin[name] === true, recordedAt, windows });
  return JSON.stringify({ schemaVersion: 1, connected: true, generatedAt: recordedAt, refreshing, services: [
    service('Claude', [win('5時間', claude[0], claudeResets[0]), win('週', claude[1], claudeResets[1])]),
    service('Codex', [win('週', codex, resetsAt)]),
    service('Grok', [win('週', grok, resetsAt)]),
  ] });
}
const connected = options => nextConnection(initialConnection(), { kind: 'ok', json: snap(options) });
const view = (name, options, settings = {}, now = NOW) => keyView(name, connected(options), { ...defaultSettings(name), ...settings }, now);

test('missing and zero stay distinct', () => {
  const v = view('Claude', { claude: [null, 0] });
  assert.equal(v.kind, 'usage');
  assert.deepEqual(v.bars.map(b => [b.value, b.text]), [[null, '未取得'], [0, '0%']]);
});
test('remaining inverts used and never goes negative', () => {
  assert.deepEqual(view('Codex', { codex: 30 }, { metric: 'remaining' }).bars.map(b => [b.value, b.text]), [[70, '70%']]);
  assert.deepEqual(view('Codex', { codex: 120 }, { metric: 'remaining' }).bars.map(b => [b.value, b.text]), [[0, '0%']]);
});
test('a Claude window the service did not report is not drawn', () => {
  // Claude no longer reports a 5-hour window (both percent and reset are null): only 週 is shown.
  const v = view('Claude', { claude: [null, 74], claudeResets: [null, LATER_RESET] });
  assert.equal(v.kind, 'usage');
  assert.deepEqual(v.bars.map(b => [b.label, b.text]), [['週', '74%']]);
});
test('a window with a reset time but no percent still shows 未取得', () => {
  assert.deepEqual(view('Claude', { claude: [null, 20] }).bars.map(b => [b.label, b.text]), [['5時間', '未取得'], ['週', '20%']]);
});
test('Claude has 5時間 and 週 bars, Codex and Grok have 週', () => {
  assert.deepEqual(view('Claude').bars.map(b => b.label), ['5時間', '週']);
  assert.deepEqual(view('Codex').bars.map(b => b.label), ['週']);
  assert.deepEqual(view('Grok').bars.map(b => b.label), ['週']);
});
test('first refresh shows 未取得 on every key', () => {
  for (const name of ['Claude', 'Codex', 'Grok']) {
    const v = view(name, { claude: [null, null], codex: null, grok: null, refreshing: true });
    assert.equal(v.kind, 'message'); assert.equal(v.message, '未取得');
  }
});
test('value older than 15 minutes is muted with a caption', () => {
  const v = view('Codex', {}, {}, Date.parse(RECORDED) + 20 * 60000);
  assert.equal(v.tone, 'muted'); assert.equal(v.caption, '保存された値');
});
test('passed reset is muted and shows 期限経過', () => {
  const v = view('Codex', { resetsAt: new Date(NOW - 60000).toISOString() });
  assert.equal(v.tone, 'muted'); assert.equal(v.bars[0].reset, '期限経過');
});
test('an expired Claude 5時間 bar is muted while the current 週 bar stays normal', () => {
  const muted = PALETTES.find(p => p.id === 'orange').muted;
  for (const settings of [{}, { metric: 'remaining' }, { showReset: false }]) {
    const v = view('Claude', { claude: [92, 40], claudeResets: [new Date(NOW - 60000).toISOString(), LATER_RESET] }, settings);
    assert.deepEqual(v.bars.map(b => b.tone), ['muted', 'normal']);
    for (const style of STYLES) {
      const svg = renderKey(v, { ...defaultSettings('Claude'), ...settings, style });
      assert.match(svg, new RegExp(`fill="${muted}"[^>]*>${v.bars[0].text}<`), style + ' ' + JSON.stringify(settings));
    }
  }
});
test('only the alerting Claude bar takes the warning colour', () => {
  assert.deepEqual(view('Claude', { claude: [96, 20] }).bars.map(b => b.tone), ['critical', 'normal']);
  assert.deepEqual(view('Claude', { claude: [20, 92] }).bars.map(b => b.tone), ['normal', 'warning']);
});
test('fresh 92% is warning and 96% is critical', () => {
  assert.equal(view('Codex', { codex: 92 }).tone, 'warning');
  assert.equal(view('Codex', { codex: 96 }).tone, 'critical');
  assert.equal(view('Codex', { codex: 30 }).tone, 'normal');
});
test('needsLogin Grok shows 要ログイン', () => {
  const v = view('Grok', { status: '再ログインが必要です', needsLogin: { Grok: true } });
  assert.equal(v.kind, 'message'); assert.equal(v.message, '要ログイン');
});
test('needsLogin with recorded values keeps the bars and says 要ログイン', () => {
  const v = view('Codex', { needsLogin: { Codex: true }, status: 'ChatGPT ログインが必要です・CLI 記録を表示' });
  assert.equal(v.kind, 'usage'); assert.equal(v.caption, '要ログイン'); assert.equal(v.tone, 'muted');
});
test('showReset false hides reset text', () => {
  assert.equal(view('Codex', {}, { showReset: false }).bars[0].reset, '');
  assert.notEqual(view('Codex', {}, { showReset: true }).bars[0].reset, '');
});
test('loading and disconnected messages', () => {
  assert.deepEqual([keyView('Codex', initialConnection(), defaultSettings('Codex'), NOW)].map(v => [v.kind, v.message]), [['loading', '読み込み中']]);
  const gone = nextConnection(initialConnection(), { kind: 'absent' });
  const v = keyView('Codex', gone, defaultSettings('Codex'), NOW);
  assert.equal(v.kind, 'message'); assert.equal(v.message, '未接続');
});

test('state: one failure keeps last values; two disconnect; success resets', () => {
  const first = connected();
  const once = nextConnection(first, { kind: 'failed' });
  assert.equal(once.status, 'connected'); assert.equal(once.snapshot, first.snapshot); assert.equal(once.failures, 1);
  const twice = nextConnection(once, { kind: 'failed' });
  assert.equal(twice.status, 'disconnected'); assert.equal(twice.snapshot, null);
  const back = nextConnection(once, { kind: 'ok', json: snap() });
  assert.equal(back.status, 'connected'); assert.equal(back.failures, 0);
});
test('state: invalid JSON counts as a failure', () => {
  const once = nextConnection(connected(), { kind: 'ok', json: '{bad' });
  assert.equal(once.status, 'connected'); assert.equal(once.failures, 1);
});
test('state: connected:false and absent disconnect at once', () => {
  const off = nextConnection(connected(), { kind: 'ok', json: '{"schemaVersion":1,"connected":false,"services":[]}' });
  assert.deepEqual(off, { status: 'disconnected', snapshot: null, failures: 0 });
  assert.deepEqual(nextConnection(connected(), { kind: 'absent' }), { status: 'disconnected', snapshot: null, failures: 0 });
});
test('state: starts loading and one failure keeps loading', () => {
  assert.deepEqual(initialConnection(), { status: 'loading', snapshot: null, failures: 0 });
  assert.equal(nextConnection(initialConnection(), { kind: 'failed' }).status, 'loading');
});

test('settings: unknown values fall back to defaults', () => {
  assert.deepEqual(normalizeKeySettings('Codex', { style: 'x', metric: 'y', showReset: 'maybe', palette: 'z' }), { style: 'vertical', metric: 'used', showReset: true, palette: 'green' });
  assert.deepEqual(normalizeKeySettings('Claude', undefined), { style: 'vertical', metric: 'used', showReset: true, palette: 'orange' });
  assert.equal(normalizeKeySettings('Grok', { showReset: 'false' }).showReset, false);
  assert.equal(normalizeKeySettings('Grok', { showReset: false }).showReset, false);
  assert.deepEqual(normalizeKeySettings('Grok', { style: 'ring', metric: 'remaining', palette: 'blue' }), { style: 'ring', metric: 'remaining', showReset: true, palette: 'blue' });
});
test('settings: palettes, styles and actions are fixed', () => {
  assert.deepEqual(PALETTES.map(p => [p.id, p.label]), [['green', '緑'], ['orange', 'オレンジ'], ['blue', '青'], ['violet', '紫'], ['rose', 'ピンク'], ['slate', 'グレー']]);
  assert.deepEqual(STYLES, ['ring', 'vertical', 'horizontal']);
  assert.deepEqual(ACTIONS, { 'io.github.genkis3zu.aiusagemanager.claude': 'Claude', 'io.github.genkis3zu.aiusagemanager.codex': 'Codex', 'io.github.genkis3zu.aiusagemanager.grok': 'Grok' });
});
test('settings: style and palette cycle in order and wrap', () => {
  assert.deepEqual([nextStyle('ring'), nextStyle('vertical'), nextStyle('horizontal')], ['vertical', 'horizontal', 'ring']);
  assert.deepEqual([nextPalette('green'), nextPalette('orange'), nextPalette('slate')], ['orange', 'blue', 'green']);
});

test('renderKey: every style returns a 144x144 svg without image or href', () => {
  const views = [view('Claude'), view('Codex'), view('Codex', { codex: 96 }), keyView('Grok', initialConnection(), defaultSettings('Grok'), NOW), view('Grok', { grok: null })];
  for (const style of STYLES) for (const v of views) {
    const svg = renderKey(v, { ...defaultSettings(v.title), style });
    assert.match(svg, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="144" height="144" viewBox="0 0 144 144">/);
    assert.ok(svg.endsWith('</svg>'));
    assert.doesNotMatch(svg, /<image|href/);
  }
});
test('renderKey: text is XML-escaped', () => {
  const v = { title: 'Codex', kind: 'usage', message: '', caption: '<b>&', tone: 'muted', bars: [{ label: '週', value: 30, text: '30%', reset: '2日5h' }] };
  for (const style of STYLES) {
    const svg = renderKey(v, { ...defaultSettings('Codex'), style });
    assert.ok(svg.includes('&lt;b&gt;&amp;')); assert.ok(!svg.includes('<b>&'));
  }
});
