const { test } = require('node:test');
const assert = require('node:assert/strict');
const { initialConnection, nextConnection } = require('../src/state');
const { ACTIONS, STATUS_ACTION, PALETTES, STYLES, defaultSettings, normalizeKeySettings, nextStyle, nextPalette, paletteFor } = require('../src/settings');
const { keyView, statusView, waitingView, renderKey } = require('../src/render');

const RECORDED = '2026-11-19T00:00:00.000Z', NOW = Date.parse(RECORDED) + 60000, LATER_RESET = '2026-11-21T00:00:00.000Z';
const SHAPE = { '5時間': ['5h', '5h'], '週': ['週', 'week'] };
const win = (label, percent, resetsAt = LATER_RESET) => ({ label, shortLabel: (SHAPE[label] || [label.slice(2, -1), 'week'])[0], span: (SHAPE[label] || [0, 'week'])[1], percent, resetsAt, pace: null, alert: null });
function snap({ claude = [10, 20], codex = 30, grok = 40, recordedAt = RECORDED, source = 'オンライン', status = '', needsLogin = {}, refreshing = false, resetsAt = LATER_RESET, claudeResets = [resetsAt, resetsAt], claudeWindows = null, aiStatus = null, waiting = null } = {}) {
  const service = (name, windows) => ({ name, plan: 'Pro', source, status, needsLogin: needsLogin[name] === true, recordedAt, windows });
  return JSON.stringify({ schemaVersion: 2, connected: true, generatedAt: recordedAt, refreshing, ...(aiStatus ? { status: aiStatus } : {}), ...(waiting ? { waiting } : {}), services: [
    service('Claude', claudeWindows ? claudeWindows.map(([label, percent, at]) => win(label, percent, at)) : [win('5時間', claude[0], claudeResets[0]), win('週', claude[1], claudeResets[1])]),
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
const SOON = new Date(NOW + 3 * 3600000).toISOString();
const sizesOf = svg => [...svg.matchAll(/font-size="(\d+)"/g)].map(m => Number(m[1]));
test('Claude shows 週 and Fable as two bars', () => {
  assert.deepEqual(view('Claude', { claudeWindows: [['週', 87], ['週（Fable）', 95]] }).bars.map(b => [b.label, b.short, b.text]), [['週', '週', '87%'], ['週（Fable）', 'Fable', '95%']]);
});
test('Claude Max shows 5h, 週 and Fable bars in every style', () => {
  const v = view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（Fable）', 86]] });
  assert.deepEqual(v.bars.map(b => [b.label, b.short, b.text]), [['5時間', '5h', '10%'], ['週', '週', '20%'], ['週（Fable）', 'Fable', '86%']]);
  for (const style of STYLES) {
    const svg = renderKey(v, { ...defaultSettings('Claude'), style });
    for (const text of ['>10%<', '>20%<', '>86%<', '>5h<', '>週<', '>Fable<']) assert.ok(svg.includes(text), style + ' ' + text);
    assert.match(svg, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="144" height="144" viewBox="0 0 144 144">/);
  }
});
test('long model names are shortened to fit each layout', () => {
  const three = view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（VeryLongModelName）', 30]] });
  const two = view('Claude', { claudeWindows: [['週', 20], ['週（VeryLongModelName）', 30]] });
  for (const style of STYLES) {
    const svg3 = renderKey(three, { ...defaultSettings('Claude'), style }), svg2 = renderKey(two, { ...defaultSettings('Claude'), style });
    assert.ok(svg3.includes('>Very…<') && !svg3.includes('VeryLongModelName'), style + ' three');
    assert.ok(svg2.includes('>VeryLon…<') && !svg2.includes('VeryLongModelName'), style + ' two');
  }
});
test('when every bar resets at the same time, the time is shown once at the bottom in every style', () => {
  const shared = /<text x="132" y="132" [^>]*text-anchor="end"><tspan font-size="12" fill="[^"]+">リセット <\/tspan><tspan font-size="18" font-weight="700" fill="[^"]+">1日23h<\/tspan><\/text>/g;
  for (const v of [view('Codex'), view('Claude', { claudeWindows: [['週', 87], ['週（Fable）', 95]] }), view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（Fable）', 86]] })])
    for (const style of STYLES) {
      const svg = renderKey(v, { ...defaultSettings(v.title), style });
      assert.equal([...svg.matchAll(shared)].length, 1, style + ' ' + v.bars.length);
      assert.equal(svg.split('1日23h').length - 1, 1, style + ' ' + v.bars.length);
    }
});
test('when bars reset at different times, each bar shows its own time', () => {
  const v = view('Claude', { claudeResets: [SOON, LATER_RESET] });
  for (const style of STYLES) {
    const svg = renderKey(v, { ...defaultSettings('Claude'), style });
    assert.ok(!svg.includes('y="132"'), style);
    if (style === 'horizontal') {
      const ys = [...svg.matchAll(/<text x="132" y="(\d+)" [^>]*text-anchor="end"><tspan font-size="12" fill="[^"]+">リセット <\/tspan><tspan font-size="15" font-weight="700" fill="[^"]+">(3h0m|1日23h)<\/tspan>/g)].map(m => [Number(m[1]), m[2]]);
      assert.deepEqual(ys, [[70, '3h0m'], [122, '1日23h']]);
    } else {
      for (const time of ['3h0m', '1日23h']) assert.match(svg, new RegExp(`font-size="14" font-weight="700" fill="[^"]+" text-anchor="middle">${time}<`), style + ' ' + time);
    }
  }
  assert.ok(!renderKey(view('Codex', {}, { showReset: false }), { ...defaultSettings('Codex'), style: 'horizontal', showReset: false }).includes('リセット'));
});
test('horizontal bars get thinner and their numbers smaller as there are more of them', () => {
  const measure = v => {
    const svg = renderKey(v, { ...defaultSettings(v.title), style: 'horizontal' });
    return [[...svg.matchAll(/<rect x="12" y="\d+" width="120" height="(\d+)"/g)].map(m => Number(m[1])),
      [...svg.matchAll(/font-size="(\d+)" font-weight="700" fill="[^"]+" text-anchor="end">\d+%</g)].map(m => Number(m[1]))];
  };
  assert.deepEqual(measure(view('Codex')), [[20], [28]]);
  assert.deepEqual(measure(view('Claude', { claudeWindows: [['週', 87], ['週（Fable）', 95]] })), [[12, 12], [20, 20]]);
  assert.deepEqual(measure(view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（Fable）', 86]] })), [[8, 8, 8], [15, 15, 15]]);
});
test('three horizontal bars with different reset times show each time on the name line', () => {
  const svg = renderKey(view('Claude', { claudeWindows: [['5時間', 10, SOON], ['週', 20], ['週（Fable）', 86]] }), { ...defaultSettings('Claude'), style: 'horizontal' });
  const lines = [...svg.matchAll(/<text x="12" y="(\d+)" [^>]*><tspan [^>]*>([^<]+)<\/tspan><tspan [^>]*>([^<]+)<\/tspan><\/text>/g)].map(m => [m[2], m[3]]);
  assert.deepEqual(lines, [['5h', '3h0m'], ['週', '1日23h'], ['Fable', '1日23h']]);
});
test('keys with one or two bars use text of at least 12 px in every style', () => {
  for (const v of [view('Codex'), view('Codex', { codex: null }), view('Claude', { claudeWindows: [['週', 87], ['週（Fable）', 95]] }), view('Claude', { claudeResets: [SOON, LATER_RESET] })])
    for (const style of STYLES) {
      const sizes = sizesOf(renderKey(v, { ...defaultSettings(v.title), style }));
      assert.ok(Math.min(...sizes) >= 12, style + ' ' + v.bars.length + ' ' + sizes);
    }
});
test('three-bar keys use text of at least 10 px in every style', () => {
  for (const v of [view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（Fable）', 86]] }), view('Claude', { claudeWindows: [['5時間', 10, SOON], ['週', 20], ['週（Fable）', 86]] })])
    for (const style of STYLES) {
      const sizes = sizesOf(renderKey(v, { ...defaultSettings('Claude'), style }));
      assert.ok(sizes.length > 0 && Math.min(...sizes) >= 10, style + ' ' + sizes);
    }
});
test('a fourth window is not drawn', () => {
  const v = view('Claude', { claudeWindows: [['5時間', 10], ['週', 20], ['週（Fable）', 30], ['週（Other）', 40]] });
  assert.deepEqual(v.bars.map(b => b.short), ['5h', '週', 'Fable']);
  for (const style of STYLES) assert.ok(!renderKey(v, { ...defaultSettings('Claude'), style }).includes('Other'));
});
test('short labels come from the snapshot', () => {
  const v = view('Claude', { claudeWindows: [['週', 74], ['週（Fable）', 91]] });
  assert.deepEqual(v.bars.map(b => b.short), ['週', 'Fable']);
  for (const style of STYLES) assert.ok(renderKey(v, { ...defaultSettings('Claude'), style }).includes('>Fable<'), style);
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
  assert.equal(v.kind, 'message'); assert.equal(v.message, '要ログイン'); assert.equal(v.needsLogin, true);
  assert.equal(view('Grok').needsLogin, false);
  assert.equal(keyView('Grok', initialConnection(), defaultSettings('Grok'), NOW).needsLogin, false);
});
test('needsLogin with recorded values keeps the bars and says 要ログイン', () => {
  const v = view('Codex', { needsLogin: { Codex: true }, status: 'ChatGPT ログインが必要です・CLI 記録を表示' });
  assert.equal(v.kind, 'usage'); assert.equal(v.caption, '要ログイン'); assert.equal(v.tone, 'muted'); assert.equal(v.needsLogin, true);
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

const STATUS_PALETTE = 'green';
const AI = {
  critical: { tone: 'critical', title: 'Claude 障害', detail: 'Claude Code', page: 'https://status.claude.com/', line: 'Claude 障害（Claude Code）' },
  warning: { tone: 'warning', title: 'OpenAI 一部不調', detail: 'Codex in ChatGPT Desktop', page: 'https://status.openai.com/', line: 'OpenAI 一部不調（Codex in ChatGPT Desktop）' },
  reset: { tone: 'reset', title: 'Claude Fable リセット', detail: '週（Fable）', page: null, line: 'Claude 週（Fable） がリセットされました' },
  normal: { tone: 'normal', title: '正常', detail: '', page: null, line: 'AIの状況：正常' },
  unknown: { tone: 'unknown', title: '確認できません', detail: '', page: null, line: 'AIの状況：障害情報を確認できません' },
};
const statusOf = aiStatus => statusView(connected({ aiStatus }));
test('status key: company on the first line, what happened on the second', () => {
  assert.deepEqual([statusOf(AI.critical)].map(v => [v.kind, v.title, v.tone, v.lines, v.detail]), [['status', 'AIの状況', 'critical', ['Claude', '障害'], 'Claude Code']]);
  assert.deepEqual(statusOf(AI.warning).lines, ['OpenAI', '一部不調']);
  assert.deepEqual([statusOf(AI.reset)].map(v => [v.tone, v.lines, v.detail]), [['reset', ['Claude', 'Fable リセット'], '週（Fable）']]);
  assert.deepEqual([statusOf(AI.normal)].map(v => [v.tone, v.lines]), [['normal', ['正常']]]);
  assert.deepEqual([statusOf(AI.unknown)].map(v => [v.tone, v.lines]), [['unknown', ['確認できません']]]);
});
test('status key: loading, 未接続, and a tray app without status', () => {
  assert.deepEqual([statusView(initialConnection())].map(v => [v.kind, v.title, v.message]), [['loading', 'AIの状況', '読み込み中']]);
  assert.deepEqual([statusView(nextConnection(initialConnection(), { kind: 'absent' }))].map(v => [v.kind, v.message]), [['message', '未接続']]);
  assert.deepEqual([statusOf(null)].map(v => [v.kind, v.message]), [['message', '未取得']]);
});
test('status key: the headline takes the tone colour and stays inside the key', () => {
  const p = paletteFor(STATUS_PALETTE);
  const colours = { critical: p.critical, warning: p.warning, reset: p.accent, normal: p.text, unknown: p.muted };
  for (const [tone, value] of Object.entries(AI)) {
    const svg = renderKey(statusOf(value), { palette: STATUS_PALETTE });
    assert.match(svg, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="144" height="144" viewBox="0 0 144 144">/);
    assert.ok(svg.includes('>AIの状況<'), tone);
    const last = value.title.split(' ').slice(1).join(' ') || value.title;
    assert.match(svg, new RegExp(`fill="${colours[tone]}"[^>]*>${last}<`), tone);
    const sizes = [...svg.matchAll(/font-size="(\d+)"/g)].map(m => Number(m[1]));
    assert.ok(Math.min(...sizes) >= 10 && Math.max(...sizes) <= 22, tone + ' ' + sizes);
  }
  const long = renderKey(statusOf({ ...AI.critical, title: 'Claude ' + '障害'.repeat(20), detail: 'claude.ai、Claude Code、Claude API (api.anthropic.com)' }), { palette: STATUS_PALETTE });
  assert.ok(long.includes('…') && !long.includes('障害'.repeat(20)) && !long.includes('api.anthropic.com)'));
  const both = renderKey(statusOf({ ...AI.critical, title: 'OpenAI 障害', detail: 'Claude も一部不調（Claude Code）' }), { palette: STATUS_PALETTE });
  assert.ok(both.includes('>Claude も一部不調<') && both.includes('>（Claude Code）<'), 'the second line starts at the bracket');
  const escaped = renderKey(statusOf({ ...AI.critical, title: '<b>& 障害', detail: '<i>' }), { palette: STATUS_PALETTE });
  assert.ok(escaped.includes('&lt;b&gt;&amp;') && escaped.includes('&lt;i&gt;') && !escaped.includes('<b>') && !escaped.includes('<i>'));
});
const rows = (...list) => list.map(([company, tone, detail = '']) => ({ company, tone, detail }));
test('status key: one line per company says how each one is', () => {
  const v = statusOf({ ...AI.normal, providers: rows(['Claude', 'normal'], ['OpenAI', 'normal']) });
  assert.deepEqual([v.kind, v.rows.map(r => [r.name, r.state, r.tone]), v.note], ['status', [['Claude', '正常', 'normal'], ['OpenAI', '正常', 'normal']], '']);
  const w = statusOf({ ...AI.warning, providers: rows(['Claude', 'normal'], ['OpenAI', 'warning', 'Codex']) });
  assert.deepEqual([w.rows.map(r => [r.state, r.tone]), w.note, w.noteTone], [[['正常', 'normal'], ['一部不調', 'warning']], 'Codex', 'muted']);
  const both = statusOf({ ...AI.critical, providers: rows(['Claude', 'critical', 'Claude Code'], ['OpenAI', 'unknown']) });
  assert.deepEqual([both.rows.map(r => r.state), both.note], [['障害', '確認できず'], 'Claude Code']);
  const reset = statusOf({ ...AI.reset, providers: rows(['Claude', 'normal'], ['OpenAI', 'normal']) });
  assert.deepEqual([reset.note, reset.noteTone], ['Claude Fable リセット', 'reset']);
  // Without company lines (an older tray app) the key keeps the single headline.
  assert.deepEqual(statusOf(AI.normal).lines, ['正常']);
});
test('status key: company lines are coloured by state and fit the key', () => {
  const p = paletteFor(STATUS_PALETTE);
  const svg = renderKey(statusOf({ ...AI.critical, providers: rows(['Claude', 'critical', 'Claude Code'], ['OpenAI', 'unknown']) }), { palette: STATUS_PALETTE });
  for (const [text, fill] of [['Claude', p.text], ['障害', p.critical], ['OpenAI', p.text], ['確認できず', p.muted], ['Claude Code', p.muted]])
    assert.match(svg, new RegExp(`fill="${fill}"[^>]*>${text}<`), text);
  const ok = renderKey(statusOf({ ...AI.reset, providers: rows(['Claude', 'normal'], ['OpenAI', 'warning']) }), { palette: STATUS_PALETTE });
  assert.match(ok, new RegExp(`fill="${p.accent}"[^>]*>正常<`)); assert.match(ok, new RegExp(`fill="${p.warning}"[^>]*>一部不調<`));
  const sizes = [...svg.matchAll(/font-size="(\d+)"/g)].map(m => Number(m[1]));
  assert.ok(Math.min(...sizes) >= 10 && Math.max(...sizes) <= 16, String(sizes));
});
const waitItem = (service, minutes, reason = 'question', place = 'Fix login', app = 'claude') => ({ service, place, reason, since: new Date(NOW - minutes * 60000).toISOString(), app });
const waitingOf = waiting => waitingView(connected({ waiting }), NOW);
test('waiting key shows the longest wait first', () => {
  const v = waitingOf({ items: [waitItem('Claude', 12), waitItem('Codex', 4, 'question', 'web-app', 'codex')], unreadable: [] });
  assert.deepEqual([v.kind, v.title, v.count, v.top.service, v.top.place, v.top.reason, v.top.minutes, v.tone], ['waiting', '対応待ち', 2, 'Claude', 'Fix login', '質問', 12, 'critical']);
  assert.equal(waitingOf({ items: [waitItem('Codex', 4, 'confirm')], unreadable: [] }).tone, 'warning');
  const svg = renderKey(v, { palette: 'green' });
  for (const t of ['>対応待ち<', '>2<', '>Claude<', '>Fix login<', '>12分<']) assert.ok(svg.includes(t), t);
});
test('waiting key without waits says 対応待ちなし and counts finished', () => {
  const v = waitingOf({ items: [waitItem('Claude', 3, 'finished')], unreadable: [] });
  assert.deepEqual([v.count, v.top, v.finished, v.tone, v.unreadable], [0, null, 1, 'muted', []]);
  const svg = renderKey(v, { palette: 'green' });
  assert.ok(svg.includes('>対応待ちなし<') && svg.includes('>完了 1件<'));
  const unread = renderKey(waitingOf({ items: [], unreadable: [{ service: 'Codex', reason: '記録を読めません' }] }), { palette: 'green' });
  assert.ok(unread.includes('>確認できず<') && unread.includes('>Codex 読めません<') && !unread.includes('対応待ちなし'), 'never 対応待ちなし when a record could not be read');
  assert.deepEqual([waitingView(connected({}), NOW)].map(x => [x.kind, x.message]), [['message', '未取得']], 'a tray app that sends no list');
  assert.ok(renderKey(waitingOf({ items: [], unreadable: [] }), { palette: 'green' }).includes('>対応待ちなし<'));
  assert.deepEqual([waitingView(initialConnection(), NOW)].map(x => [x.kind, x.message]), [['loading', '読み込み中']]);
  assert.deepEqual([waitingView(nextConnection(initialConnection(), { kind: 'absent' }), NOW)].map(x => [x.kind, x.message]), [['message', '未接続']]);
});
test('waiting key text is escaped and fits', () => {
  const v = waitingOf({ items: [waitItem('Claude', 75, 'question', '<b>' + 'とても長い作業の名前'.repeat(4))], unreadable: [] });
  const svg = renderKey(v, { palette: 'green' });
  assert.ok(!svg.includes('<b>') && svg.includes('&lt;b&gt;') && svg.includes('…') && svg.includes('>1時間15分<'));
  const sizes = [...svg.matchAll(/font-size="(\d+)"/g)].map(m => Number(m[1]));
  assert.ok(Math.min(...sizes) >= 10, String(sizes));
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
  const off = nextConnection(connected(), { kind: 'ok', json: '{"schemaVersion":2,"connected":false,"services":[]}' });
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
  assert.equal(STATUS_ACTION, 'io.github.genkis3zu.aiusagemanager.status');
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
