const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPlugin } = require('../src/plugin');

const ID = 'io.github.genkis3zu.aiusagemanager';
const RECORDED = new Date(1000).toISOString();
const win = (label, percent) => ({ label, percent, resetsAt: '2030-01-01T00:00:00.000Z', pace: null, alert: null });
const service = (name, windows) => ({ name, plan: 'Pro', source: 'オンライン', status: '', needsLogin: false, recordedAt: RECORDED, windows });
const SNAP = JSON.stringify({ schemaVersion: 1, connected: true, generatedAt: RECORDED, refreshing: false, services: [
  service('Claude', [win('5時間', 10), win('週', 20)]), service('Codex', [win('週', 30)]), service('Grok', [win('週', 40)]),
] });
const appear = (context, action = ID + '.codex', settings = {}) => ({ event: 'willAppear', action, context, payload: { settings } });

function harness({ reads = [], resolve = () => 'C:\\Apps\\AIUsageManager.exe', launch = async () => {} } = {}) {
  const sent = [], resolveCalls = [], launches = [];
  let clock = 1000, readCount = 0;
  const deck = { setImage: (c, svg) => sent.push(['image', c, svg]), setSettings: (c, s) => sent.push(['settings', c, s]), showAlert: c => sent.push(['alert', c]) };
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
    launchApp: async file => { launches.push(file); return launch(file); },
  });
  return { plugin, sent, timers, resolveCalls, launches, reads: () => readCount, tick: ms => { clock += ms; },
    images: context => sent.filter(s => s[0] === 'image' && s[1] === context).map(s => s[2]),
    settings: context => sent.filter(s => s[0] === 'settings' && s[1] === context).map(s => s[2]) };
}
async function press(h, context, ms) { await h.plugin.handle({ event: 'keyDown', context }); h.tick(ms); await h.plugin.handle({ event: 'keyUp', context }); }

test('first key polls immediately and starts one 15 s timer', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  assert.equal(h.reads(), 1);
  assert.deepEqual(h.timers.intervals.map(t => t.ms), [15000]);
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
test('disconnected press launches the resolved path and polls again after 2 s', async () => {
  const h = harness({ reads: [{ kind: 'absent' }] });
  await h.plugin.handle(appear('k1'));
  assert.ok(h.images('k1').at(-1).includes('未接続'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.launches, ['C:\\Apps\\AIUsageManager.exe']);
  assert.deepEqual(h.settings('k1'), [{ style: 'vertical', metric: 'used', showReset: true, palette: 'green' }]);
  const retry = h.timers.timeouts.find(t => t.ms === 2000);
  assert.ok(retry);
  retry.fn(); await h.plugin.poll();
  assert.equal(h.reads(), 2);
  assert.ok(h.images('k1').at(-1).includes('30%'));
});
const flush = async () => { for (let i = 0; i < 3; i++) await new Promise(r => setImmediate(r)); };
async function fire(h, ms) { const t = h.timers.timeouts.find(x => x.ms === ms && !x.fired); t.fired = true; t.fn(); await flush(); }

test('after a launch every key shows 起動中, presses are ignored, and polls repeat at 2, 5 and 10 s', async () => {
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }, { kind: 'ok', json: SNAP }] });
  await h.plugin.handle(appear('k1')); await h.plugin.handle(appear('k2', ID + '.grok'));
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
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }, { kind: 'absent' }, { kind: 'absent' }] });
  await h.plugin.handle(appear('k1'));
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
  const h = harness({ reads: [{ kind: 'absent' }], launch: () => new Promise((_, reject) => { fail = () => reject(new Error('nope')); }) });
  await h.plugin.handle(appear('k1'));
  await h.plugin.handle({ event: 'keyDown', context: 'k1' });
  const pending = h.plugin.handle({ event: 'keyUp', context: 'k1' });
  await h.plugin.handle({ event: 'willDisappear', context: 'k1' });
  fail();
  await pending;
});
test('disconnected press without a path shows アプリの場所を設定 and alerts, without launching', async () => {
  const h = harness({ reads: [{ kind: 'absent' }, { kind: 'absent' }], resolve: () => null });
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.launches, []);
  assert.ok(h.sent.some(s => s[0] === 'alert' && s[1] === 'k1'));
  assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  const clear = h.timers.timeouts.find(t => t.ms === 5000);
  clear.fn();
  assert.ok(h.images('k1').at(-1).includes('未接続'));
});
test('a failed launch shows アプリの場所を設定', async () => {
  const h = harness({ reads: [{ kind: 'absent' }], launch: async () => { throw new Error('nope'); } });
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 100);
  assert.ok(h.images('k1').at(-1).includes('アプリの場所を設定'));
  assert.ok(!h.timers.timeouts.some(t => t.ms === 2000));
});
test('manual appPath from global settings reaches resolveAppPath', async () => {
  const h = harness({ reads: [{ kind: 'absent' }] });
  await h.plugin.handle({ event: 'didReceiveGlobalSettings', payload: { settings: { appPath: 'D:\\Tools\\AIUsageManager.exe' } } });
  await h.plugin.handle(appear('k1'));
  await press(h, 'k1', 100);
  assert.deepEqual(h.resolveCalls, ['D:\\Tools\\AIUsageManager.exe']);
});
test('didReceiveSettings replaces the key settings', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  await h.plugin.handle({ event: 'didReceiveSettings', context: 'k1', payload: { settings: { style: 'vertical', metric: 'remaining', showReset: true, palette: 'green' } } });
  assert.ok(h.images('k1').at(-1).includes('70%'));
});
test('identical images are not resent', async () => {
  const h = harness();
  await h.plugin.handle(appear('k1'));
  const count = h.images('k1').length;
  await h.plugin.poll();
  assert.equal(h.images('k1').length, count);
});
