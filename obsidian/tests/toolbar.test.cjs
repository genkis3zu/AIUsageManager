const { test } = require('node:test');
const assert = require('node:assert/strict');
const { UsageToolbar } = require('../src/toolbar');
const { parseSnapshot } = require('../src/usage');

// Minimal stand-in for the DOM parts the toolbar uses.
class El {
  constructor(tag) {
    this.tagName = tag; this.children = []; this.className = ''; this.textContent = ''; this.title = '';
    this.dataset = {}; this.attributes = {}; this.style = {}; this.parentElement = null; this.disabled = false;
    const set = new Set();
    this.classList = { add: c => set.add(c), remove: c => set.delete(c), contains: c => set.has(c), toggle: (c, on) => { if (on === undefined ? !set.has(c) : on) set.add(c); else set.delete(c); } };
  }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener() {}
  append(...items) { for (const item of items) { item.parentElement = this; this.children.push(item); } }
  prepend(item) { item.parentElement = this; this.children.unshift(item); }
  insertBefore(item, ref) { item.parentElement = this; const i = this.children.indexOf(ref); this.children.splice(i < 0 ? this.children.length : i, 0, item); }
  replaceChildren(...items) { for (const child of this.children) child.parentElement = null; this.children = []; this.append(...items); }
  remove() { const p = this.parentElement; if (p) p.children.splice(p.children.indexOf(this), 1); this.parentElement = null; }
}
const now = Date.parse('2026-11-19T00:00:00Z');
const W = { '週': ['週', 'week'], '週（Fable）': ['Fable', 'week'], '5時間': ['5h', '5h'] };
function snapshot(claudeLabels) {
  const win = (label, percent) => ({ label, shortLabel: W[label][0], span: W[label][1], percent, resetsAt: new Date(now + 86400000).toISOString(), pace: null, alert: null });
  return parseSnapshot(JSON.stringify({ schemaVersion: 2, connected: true, refreshing: false, services: [
    { name: 'Claude', plan: 'Ultra', source: 'オンライン', status: '', needsLogin: false, recordedAt: new Date(now).toISOString(), windows: claudeLabels.map((l, i) => win(l, 10 + i)) },
    { name: 'Codex', plan: 'Pro Lite', source: 'オンライン', status: '', needsLogin: false, recordedAt: new Date(now).toISOString(), windows: [win('週', 30)] },
    { name: 'Grok', plan: 'Ultra', source: 'オンライン', status: '', needsLogin: false, recordedAt: new Date(now).toISOString(), windows: [win('週', 40)] },
  ] }));
}
function toolbar() {
  const doc = { createElement: tag => new El(tag) }, parent = new El('div'), content = new El('div');
  content.ownerDocument = doc; parent.append(content);
  return new UsageToolbar(content, { open() {}, toggle() {}, refresh() {} });
}

test('slots follow the window list when it changes between updates', () => {
  const t = toolbar();
  t.update(snapshot(['週']), 'standard', '', now);
  assert.deepEqual(t.rows[0].slots.map(s => s.caption.textContent), ['週']);
  t.update(snapshot(['週', '週（Fable）']), 'standard', '', now);
  assert.deepEqual(t.rows[0].slots.map(s => s.caption.textContent), ['週', 'Fable']);
  assert.equal(t.rows[0].windows.children.length, 2);
  assert.deepEqual(t.rows[0].slots.map(s => s.value.textContent), ['10%', '11%']);
  assert.equal(t.rows[0].slots[1].slot.title.startsWith('週（Fable） 11%'), true);
});
test('unchanged window lists keep the same slot elements', () => {
  const t = toolbar();
  t.update(snapshot(['5時間', '週']), 'standard', '', now);
  const first = t.rows[0].slots[0].slot;
  t.update(snapshot(['5時間', '週']), 'compact', '', now);
  assert.equal(t.rows[0].slots[0].slot, first);
  assert.deepEqual(t.rows[0].slots.map(s => s.caption.textContent), ['5h', '週']);
});
test('a service with no windows shows no slots', () => {
  const t = toolbar();
  t.update(snapshot([]), 'standard', '', now);
  assert.equal(t.rows[0].slots.length, 0); assert.equal(t.rows[0].windows.children.length, 0);
});
