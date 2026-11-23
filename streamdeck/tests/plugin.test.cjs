const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPlugin } = require('../src/plugin');

const ID = 'io.github.genkis3zu.aiusagemanager';
const RECORDED = new Date(1000).toISOString();
const win = (label, percent) => ({ label, shortLabel: label === '5時間' ? '5h' : label, span: label === '5時間' ? '5h' : 'week', percent, resetsAt: '2030-01-01T00:00:00.000Z', pace: null, alert: null });
const service = (name, windows) => ({ name, plan: 'Pro', source: 'オンライン', status: '', needsLogin: false, recordedAt: RECORDED, windows });
const SNAP = JSON.stringify({ schemaVersion: 2, connected: true, generatedAt: RECORDED, refreshing: false, services: [
  service('Claude', [win('5時間', 10), win('週', 20)]), service('Codex', [win('週', 30)]), service('Grok', [win('週', 40)]),
] });
const appear = (context, action = ID + '.codex', settings = {}) => ({ event: 'willAppear', action, context, payload: { settings } });

function harness({ reads = [], resolve = () => 'C:\\Apps\\AIUsageManager.exe', launch = async () => {} } = {}) {
  const sent = [], resolveCalls = [], launches = [], launchArgs = [];
  let clock = 1000, readCount = 0;
  const deck = { setImage: (c, svg) => sent.push(['image', c, svg]), setSettings: (c, s) => sent.push(['settings', c, s]), showAlert: c => sent.push(['alert', c]), showOk: c => sent.push(['ok', c]), openUrl: url => sent.push(['open', url]) };
  const timers = {
    intervals: [], timeouts: [],
    setInterval(fn, ms) { const t = { fn, ms, cleared: false }; this.intervals.push(t); return t; },
    clearInterval(t) { t.cleared = true; },
    setTimeout(fn, ms) { const t = { fn, ms }; this.timeouts.push(t); return t; },
  };
  const readSnapshot = async name => { readCount++; assert.equal(name, 'PIPE'); const next = reads.shift(); return typeof next === 'function' ? next() : next || { kind: 'ok', json: SNAP }; };
  const plugin = createPlugin({
    deck, pipe: 'PIPE', readSnapshot, now: () => clock, timers,
    resolveAppPath: manual => { resolveCalls.push(manual); return resolve(manual); },
    launchApp: async (file, args) => { launches.push(file); launchArgs.push(args || []); return launch(file); },
  });
  return { plugin, sent, timers, resolveCalls, launches, launchArgs, reads: () => readCount, tick: ms => { clock += ms; },
    opened: () => sent.filter(s => s[0] === 'open').map(s => s[1]),
    images: context => sent.filter(s => s[0] === 'image' && s[1] === context).map(s => s[2]),
    settings: context => sent.filter(s => s[0] === 'settings' && s[1] === context).map(s => s[2]) };
}
// The tray app was running when Stream Deck started and was closed later (after the second read).
const closedLater = (...after) => [{ kind: 'ok', json: SNAP }, { kind: 'absent' }, ...after];
async function press(h, context, ms) { await h.plugin.handle({ event: 'keyDown', context }); h.tick(ms); await h.plugin.handle({ event: 'keyUp', context }); }

test('a pipe name that could not be found is looked up again on the next poll', async () => {
  const names = [null, 'PIPE'], seen = [];
  const deck = { setImage() {}, setSettings() {}, showAlert() {} };
  const timers = { setInterval: () => ({}), clearInterval() {}, setTimeout() {} };
  const plugin = createPlugin({ deck, pipe: () => names.shift(), now: () => 1000, timers, resolveAppPath: () => null, launchApp: async () => {},
    readSnapshot: async name => { seen.push(name); return name ? { kind: 'ok', json: SNAP } : { kind: 'absent' }; } });
  await plugin.handle(appear('k1'));
  await plugin.poll();
  assert.deepEqual(seen, [null, 'PIPE']);
});
test('first key polls immediately and starts one 5 s timer', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  assert.equal(h.reads(), 1);
  assert.deepEqual(h.timers.intervals.map(t => t.ms), [5000]);
  assert.ok(h.images('k1').at(-1).includes('30%'));
  await h.plugin.handle(appear('k2', ID + '.grok'));
  assert.equal(h.reads(), 1); assert.equal(h.timers.intervals.length, 1);
  assert.ok(h.images('k2').at(-1).includes('40%'));
  h.timers.intervals[0].fn(); await h.plugin.poll();
  assert.equal(h.reads(), 2);
});
test('a second willAppear for the same context keeps one entry and one timer', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('k1'));
  assert.equal(h.timers.intervals.length, 1);
  await h.plugin.handle({ event: 'willDisappear', context: 'k1' });
  assert.equal(h.timers.intervals[0].cleared, true);
});
test('last key disappearing clears the timer', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('k2'));
  await h.plugin.handle({ event: 'willDisappear', context: 'k1' });
  assert.equal(h.timers.intervals[0].cleared, false);
  await h.plugin.handle({ event: 'willDisappear', context: 'k2' });
  assert.equal(h.timers.intervals[0].cleared, true);
});
test('overlapping polls share one read', async () => {
  let release;
  const h = harness({ reads: [{ kind: 'ok', json: SNAP }, () => new Promise(r => { release = () => r({ kind: 'ok', json: SNAP }); })] });
  await h.plugin.handle(appear('k1'));
  const first = h.plugin.poll(), second = h.plugin.poll();
  assert.equal(first, second); assert.equal(h.reads(), 2);
  release(); await first;
});
test('willAppear saves normalized settings when they differ', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1', ID + '.claude', {}));
  assert.deepEqual(h.settings('k1'), [{ style: 'vertical', metric: 'used', showReset: true, palette: 'orange' }]);
  await h.plugin.handle(appear('k2', ID + '.codex', { style: 'ring', metric: 'remaining', showReset: false, palette: 'blue' }));
  assert.deepEqual(h.settings('k2'), []);
});
test('short press cycles style and saves; long press (600 ms) cycles palette', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.settings('k1').at(-1), { style: 'horizontal', metric: 'used', showReset: true, palette: 'green' });
  await press(h, 'k1', 600);
  assert.deepEqual(h.settings('k1').at(-1), { style: 'horizontal', metric: 'used', showReset: true, palette: 'orange' });
});
test('keyUp without keyDown is a short press', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  await h.plugin.handle({ event: 'keyUp', context: 'k1' });
  assert.equal(h.settings('k1').at(-1).style, 'horizontal');
});
const LOGIN_SNAP = (() => { const s = JSON.parse(SNAP); s.services[1].needsLogin = true; s.services[1].status = 'ChatGPT ログインが必要です'; s.services[2].needsLogin = true; s.services[2].windows[0].percent = null; return JSON.stringify(s); })();
test('a short press on a 要ログイン key starts that service login through the tray app', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: LOGIN_SNAP }] });
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('k2', ID + '.grok'));
  assert.ok(h.images('k1').at(-1).includes('要ログイン')); assert.ok(h.images('k2').at(-1).includes('要ログイン'));
  const before = h.settings('k1').length;
  await press(h, 'k1', 100); await press(h, 'k2', 100);
  assert.deepEqual(h.launchArgs, [['--login-codex'], ['--login-grok']]);
  assert.equal(h.settings('k1').length, before);
});
test('a long press on a 要ログイン key still changes the colour', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: LOGIN_SNAP }] });
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 600);
  assert.deepEqual(h.launchArgs, []); assert.equal(h.settings('k1').at(-1).palette, 'orange');
});
test('a 要ログイン press without a usable tray app shows アプリの場所を設定', async () => {
  for (const options of [{ resolve: () => null }, { launch: async () => { throw new Error('nope'); } }]) {
    const h = harness({ reads: [{ kind: 'ok', json: LOGIN_SNAP }], ...options });
    await h.plugin.handle(appear('k1'));
    await press(h, 'k1', 100);
    assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  }
});
test('keys that do not need login keep cycling the style on a short press', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.launchArgs, []); assert.equal(h.settings('k1').at(-1).style, 'horizontal');
});
test('disconnected press launches the resolved path and polls again after 2 s', async () => {
  const h = harness({ reads: closedLater() });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  assert.ok(h.images('k1').at(-1).includes('未接続'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe']);
  assert.deepEqual(h.settings('k1'), [{ style: 'vertical', metric: 'used', showReset: true, palette: 'green' }]);
  const retry = h.timers.timeouts.find(t => t.ms === 2000);
  assert.ok(retry);
  retry.fn(); await h.plugin.poll();
  assert.equal(h.reads(), 3);
  assert.ok(h.images('k1').at(-1).includes('30%'));
});
const flush = async () => { for (let i = 0; i < 3; i++) await new Promise(r => setImmediate(r)); };
async function fire(h, ms) { const t = h.timers.timeouts.find(x => x.ms === ms && !x.fired); t.fired = true; t.fn(); await flush(); }

test('after a launch every key shows 起動中, presses are ignored, and polls repeat at 2, 5 and 10 s', async () => {
  const h = harness({ reads: closedLater({ kind: 'absent' }, { kind: 'ok', json: SNAP }) });
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('k2', ID + '.grok')); await h.plugin.poll();
  await press(h, 'k1', 100);
  assert.ok(h.images('k1').at(-1).includes('起動中')); assert.ok(h.images('k2').at(-1).includes('起動中'));
  assert.deepEqual(h.timers.timeouts.map(t => t.ms).sort((a, b) => a - b), [2000, 5000, 10000]);
  await press(h, 'k1', 100); await press(h, 'k2', 700);
  assert.equal(h.launches.length, 1); assert.deepEqual(h.settings('k1'), [{ style: 'vertical', metric: 'used', showReset: true, palette: 'green' }]);
  await fire(h, 2000);
  assert.ok(h.images('k1').at(-1).includes('起動中'));
  await fire(h, 5000);
  assert.ok(h.images('k1').at(-1).includes('30%')); assert.ok(h.images('k2').at(-1).includes('40%'));
});
test('起動中 ends after the last re-poll when the app never appears, and presses launch again', async () => {
  const h = harness({ reads: closedLater({ kind: 'absent' }, { kind: 'absent' }, { kind: 'absent' }) });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  await press(h, 'k1', 100);
  await fire(h, 2000); await fire(h, 5000);
  assert.ok(h.images('k1').at(-1).includes('起動中'));
  await fire(h, 10000);
  assert.ok(h.images('k1').at(-1).includes('未接続'));
  await press(h, 'k1', 100);
  assert.equal(h.launches.length, 2);
});
test('a key removed while its launch fails does not throw', async () => {
  let fail;
  const h = harness({ reads: closedLater(), launch: () => new Promise((_, reject) => { fail = () => reject(new Error('nope')); }) });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  await h.plugin.handle({ event: 'keyDown', context: 'k1' });
  const pending = h.plugin.handle({ event: 'keyUp', context: 'k1' });
  await h.plugin.handle({ event: 'willDisappear', context: 'k1' });
  fail();
  await pending;
});
test('disconnected press without a path shows アプリの場所を設定 and alerts, without launching', async () => {
  const h = harness({ reads: closedLater({ kind: 'absent' }), resolve: () => null });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  await press(h, 'k1', 100);
  assert.deepEqual(h.launches, []);
  assert.ok(h.sent.some(s => s[0] === 'alert' && s[1] === 'k1'));
  assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  const clear = h.timers.timeouts.find(t => t.ms === 5000);
  clear.fn();
  assert.ok(h.images('k1').at(-1).includes('未接続'));
});
test('a failed launch shows アプリの場所を設定', async () => {
  const h = harness({ reads: closedLater(), launch: async () => { throw new Error('nope'); } });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  await press(h, 'k1', 100);
  assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  assert.ok(!h.timers.timeouts.some(t => t.ms === 2000));
});
test('manual appPath from global settings reaches resolveAppPath', async () => {
  const h = harness({ reads: closedLater() });
  await h.plugin.handle({ event: 'didReceiveGlobalSettings', payload: { settings: { appPath: 'D:\\Tools\\AIUsageManager.exe' } } });
  await h.plugin.handle(appear('k1')); await h.plugin.poll();
  await press(h, 'k1', 100);
  assert.deepEqual(h.resolveCalls, ['D:\\Tools\\AIUsageManager.exe']);
});
test('didReceiveSettings replaces the key settings', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  await h.plugin.handle({ event: 'didReceiveSettings', context: 'k1', payload: { settings: { style: 'vertical', metric: 'remaining', showReset: true, palette: 'green' } } });
  assert.ok(h.images('k1').at(-1).includes('70%'));
});
const withWaiting = waiting => { const s = JSON.parse(SNAP); s.waiting = waiting; return JSON.stringify(s); };
const waitAt = (service, minutes, app, reason = 'question') => ({ service, place: 'p', reason, since: new Date(Date.now() - minutes * 60000).toISOString(), app });
test('pressing the waiting key opens the app of the longest wait', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withWaiting({ items: [waitAt('Claude', 12, 'claude'), waitAt('Codex', 4, 'codex')], unreadable: [] }) }] });
  await h.plugin.handle(appear('w1', ID + '.waiting', { style: 'ring' }));
  assert.ok(h.images('w1').at(-1).includes('>対応待ち<')); assert.deepEqual(h.settings('w1'), []);
  await press(h, 'w1', 100); await press(h, 'w1', 700);
  assert.deepEqual(h.opened(), ['claude://', 'claude://']); assert.deepEqual(h.launches, []);
});
test('without waits the waiting key opens the app of the newest finished item, or nothing', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withWaiting({ items: [waitAt('Codex', 2, 'vscode', 'finished')], unreadable: [] }) }] });
  await h.plugin.handle(appear('w1', ID + '.waiting'));
  await press(h, 'w1', 100);
  assert.deepEqual(h.opened(), ['vscode://']);
  const empty = harness();
  await empty.plugin.handle(appear('w1', ID + '.waiting'));
  await press(empty, 'w1', 100);
  assert.deepEqual(empty.opened(), []);
});
test('a waiting key never opens a link outside the four apps', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withWaiting({ items: [waitAt('Claude', 12, 'https')], unreadable: [] }) }] });
  await h.plugin.handle(appear('w1', ID + '.waiting'));
  await press(h, 'w1', 100);
  assert.deepEqual(h.opened(), []);
});
test('a waiting key opens nothing when the longest wait has no app to open', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withWaiting({ items: [waitAt('Claude', 12, null), waitAt('Codex', 4, 'codex')], unreadable: [] }) }] });
  await h.plugin.handle(appear('w1', ID + '.waiting'));
  assert.ok(h.images('w1').at(-1).includes('>対応待ち<'));
  await press(h, 'w1', 100);
  assert.deepEqual(h.opened(), []); assert.deepEqual(h.launches, []);
});
test('a disconnected waiting key starts the tray app', async () => {
  const h = harness({ reads: closedLater() });
  await h.plugin.handle(appear('w1', ID + '.waiting')); await h.plugin.poll();
  await press(h, 'w1', 100);
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe']); assert.deepEqual(h.opened(), []);
});
const withStatus = status => { const s = JSON.parse(SNAP); s.status = status; return JSON.stringify(s); };
const OUTAGE = { tone: 'warning', title: 'OpenAI 一部不調', detail: 'Codex', page: 'https://status.openai.com/', line: 'OpenAI 一部不調（Codex）' };
test('the AIの状況 key shows the shared status and saves no settings', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withStatus(OUTAGE) }] });
  await h.plugin.handle(appear('s1', ID + '.status', { style: 'ring' }));
  const svg = h.images('s1').at(-1);
  assert.ok(svg.includes('>AIの状況<') && svg.includes('>OpenAI<') && svg.includes('>一部不調<') && svg.includes('>Codex<'));
  assert.deepEqual(h.settings('s1'), []);
  await h.plugin.handle({ event: 'didReceiveSettings', context: 's1', payload: { settings: { palette: 'blue' } } });
  assert.equal(h.images('s1').at(-1), svg);
});
test('pressing the AIの状況 key opens that company status page, short or long', async () => {
  const h = harness({ reads: [{ kind: 'ok', json: withStatus(OUTAGE) }] });
  await h.plugin.handle(appear('s1', ID + '.status'));
  await press(h, 's1', 100); await press(h, 's1', 700);
  assert.deepEqual(h.opened(), ['https://status.openai.com/', 'https://status.openai.com/']);
  assert.deepEqual(h.settings('s1'), []); assert.deepEqual(h.launches, []);
});
test('without an outage page the AIの状況 key opens the Claude status page', async () => {
  for (const status of [{ tone: 'normal', title: '正常', detail: '', page: null, line: 'AIの状況：正常' }, { ...OUTAGE, page: 'https://evil.example/' }, null]) {
    const h = harness({ reads: [{ kind: 'ok', json: status ? withStatus(status) : SNAP }] });
    await h.plugin.handle(appear('s1', ID + '.status'));
    await press(h, 's1', 100);
    assert.deepEqual(h.opened(), ['https://status.claude.com/'], JSON.stringify(status));
  }
});
test('a disconnected AIの状況 key starts the tray app instead of opening a page', async () => {
  const h = harness({ reads: closedLater() });
  await h.plugin.handle(appear('s1', ID + '.status')); await h.plugin.poll();
  assert.ok(h.images('s1').at(-1).includes('未接続'));
  await press(h, 's1', 100);
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe']); assert.deepEqual(h.opened(), []);
  assert.ok(h.images('s1').at(-1).includes('起動中'));
});
test('identical images are not resent', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  const count = h.images('k1').length;
  await h.plugin.poll();
  assert.equal(h.images('k1').length, count);
});
test('when Stream Deck starts without the tray app, the plugin starts it once in the background', async () => {
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }, { kind: 'ok', json: SNAP }] });
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('s1', ID + '.status'));
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe']); assert.deepEqual(h.launchArgs, [['--background']]);
  assert.ok(h.images('k1').at(-1).includes('起動中')); assert.ok(h.images('s1').at(-1).includes('起動中'));
  assert.deepEqual(h.timers.timeouts.map(t => t.ms).sort((a, b) => a - b), [2000, 5000, 10000]);
  assert.ok(!h.sent.some(s => s[0] === 'alert'));
  await fire(h, 2000); await fire(h, 5000);
  assert.ok(h.images('k1').at(-1).includes('30%'));
  assert.equal(h.launches.length, 1);
});
test('a tray app closed after Stream Deck started is not started again by itself', async () => {
  const h = harness({ reads: closedLater({ kind: 'absent' }) });
  await h.plugin.handle(appear('k1')); await h.plugin.poll(); await h.plugin.poll();
  assert.deepEqual(h.launches, []); assert.ok(h.images('k1').at(-1).includes('未接続'));
});
test('the start waits until a busy pipe has failed twice', async () => {
  const h = harness({ reads: [{ kind: 'failed' }, { kind: 'failed' }] });
  await h.plugin.handle(appear('k1'));
  assert.deepEqual(h.launches, []);
  await h.plugin.poll();
  assert.deepEqual(h.launchArgs, [['--background']]);
});
test('a start that cannot happen leaves 未接続 without an alert; a press then says what is wrong', async () => {
  for (const options of [{ resolve: () => null }, { launch: async () => { throw new Error('nope'); } }]) {
    const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }], ...options });
    await h.plugin.handle(appear('k1'));
    assert.ok(h.images('k1').at(-1).includes('未接続'));
    assert.ok(!h.sent.some(s => s[0] === 'alert')); assert.ok(!h.timers.timeouts.some(t => t.ms === 2000));
    await press(h, 'k1', 100);
    assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  }
});
test('a first key that appears more than a minute after Stream Deck started does not start the tray app', async () => {
  const late = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }] });
  late.tick(61000);
  await late.plugin.handle(appear('k1'));
  assert.deepEqual(late.launches, []); assert.ok(late.images('k1').at(-1).includes('未接続'));
  const early = harness({ reads: [{ kind: 'absent' }] });
  early.tick(59000);
  await early.plugin.handle(appear('k1'));
  assert.deepEqual(early.launchArgs, [['--background']]);
});
test('a timer left from an earlier start does not end a later start early', async () => {
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'ok', json: SNAP }, { kind: 'absent' }, { kind: 'absent' }, { kind: 'absent' }] });
  await h.plugin.handle(appear('k1'));
  await fire(h, 2000);
  assert.ok(h.images('k1').at(-1).includes('30%'));
  await h.plugin.poll();
  assert.ok(h.images('k1').at(-1).includes('未接続'));
  await press(h, 'k1', 100);
  assert.equal(h.launches.length, 2);
  await fire(h, 5000); await fire(h, 10000);
  assert.ok(h.images('k1').at(-1).includes('起動中'));
});
const NOW = ID + '.nowdoing';
test('the いまやってること key shows its fixed picture and does not read the tray app or start it', async () => {
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }] });
  await h.plugin.handle(appear('n1', NOW));
  const svg = h.images('n1').at(-1);
  assert.ok(svg.includes('📝') && svg.includes('いま') && svg.includes('やってること'));
  assert.doesNotMatch(svg, /%|未接続|読み込み中/);
  assert.equal(h.reads(), 0); assert.deepEqual(h.timers.intervals, []); assert.deepEqual(h.launches, []); assert.deepEqual(h.settings('n1'), []);
});
test('pressing the いまやってること key starts the app with --now-doing and shows ✓, short or long', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('n1', NOW));
  await press(h, 'n1', 100); await press(h, 'n1', 600);
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe', 'C:\\Apps\\AIUsageManager.exe']);
  assert.deepEqual(h.launchArgs, [['--now-doing'], ['--now-doing']]);
  assert.deepEqual(h.sent.filter(s => s[0] === 'ok'), [['ok', 'n1'], ['ok', 'n1']]);
  assert.deepEqual(h.settings('n1'), []); assert.deepEqual(h.opened(), []);
});
test('the いまやってること key starts its window while the tray app is not connected or is starting, and never starts the tray app', async () => {
  const h = harness({ reads: closedLater() });
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('n1', NOW)); await h.plugin.poll();
  assert.ok(h.images('k1').at(-1).includes('未接続'));
  await press(h, 'n1', 100);
  assert.deepEqual(h.launchArgs, [['--now-doing']]);
  // A press on a usage key starts the tray app (起動中); the いまやってること key still opens its window and keeps its picture.
  await press(h, 'k1', 100);
  assert.ok(h.images('k1').at(-1).includes('起動中')); assert.ok(!h.images('n1').at(-1).includes('起動中'));
  await press(h, 'n1', 100);
  assert.deepEqual(h.launchArgs, [['--now-doing'], [], ['--now-doing']]);
});
test('the いまやってること key without a usable app shows アプリの場所を設定 and no ✓', async () => {
  for (const options of [{ resolve: () => null }, { launch: async () => { throw new Error('nope'); } }]) {
    const h = harness(options);
    await h.plugin.handle(appear('n1', NOW));
    await press(h, 'n1', 100);
    assert.ok(h.images('n1').at(-1).includes('アプリの場所を設定'));
    assert.ok(h.sent.some(s => s[0] === 'alert')); assert.ok(!h.sent.some(s => s[0] === 'ok'));
  }
});
test('polling stops when only いまやってること keys are left', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('n1', NOW));
  await h.plugin.handle({ event: 'willDisappear', context: 'k1' });
  assert.equal(h.timers.intervals[0].cleared, true);
});
