'use strict';
const { presentService } = require('../../obsidian/src/usage');
const { paletteFor } = require('./settings');

const shorten = text => { const chars = Array.from(text || ''); return chars.length > 9 ? chars.slice(0, 9).join('') + '…' : chars.join(''); };

// What one key shows, decided from the shared snapshot with the same rules as the Obsidian toolbar.
function keyView(name, connection, settings, now) {
  const base = { title: name, kind: 'message', message: '', caption: '', tone: 'muted', bars: [], needsLogin: false };
  if (connection.status === 'loading') return { ...base, kind: 'loading', message: '読み込み中' };
  if (connection.status !== 'connected' || !connection.snapshot) return { ...base, message: '未接続' };
  const service = connection.snapshot.services.find(s => s.name === name);
  if (!service) return { ...base, message: '未取得' };
  const presented = presentService(service, true, now);
  if (presented.windows.every(w => w.percent === null)) return { ...base, message: service.needsLogin ? '要ログイン' : '未取得', needsLogin: service.needsLogin === true };
  // A window with neither a percent nor a reset time was not reported by the service at all
  // (Claude no longer returns a 5-hour window), so it is left out when another window has data.
  const reported = presented.windows.filter(w => w.percent !== null || w.resetsAt !== null);
  // Each bar has its own tone, like the Obsidian toolbar: an expired or stale window is muted,
  // and only the window that has an alert takes the warning colour.
  // A key has room for three bars (Claude Max: 5時間, 週, 週（Fable）); further windows are not drawn.
  const bars = reported.slice(0, 3).map(w => {
    const value = w.percent === null ? null : settings.metric === 'remaining' ? Math.max(0, 100 - w.percent) : w.percent;
    const tone = !w.active ? 'muted' : w.alert ? (w.percent >= 95 ? 'critical' : 'warning') : 'normal';
    return { label: w.label, short: w.shortLabel, value, text: value === null ? '未取得' : Math.round(value) + '%', reset: settings.showReset ? w.reset : '', tone };
  });
  const caption = service.needsLogin ? '要ログイン' : presented.severity === 'muted' ? shorten(presented.state) : '';
  return { ...base, kind: 'usage', tone: presented.severity, caption, bars, needsLogin: service.needsLogin === true };
}

const STATUS_TITLE = 'AIの状況';
const STATE_WORDS = { normal: '正常', warning: '一部不調', critical: '障害', unknown: '確認できず' };
// The AIの状況 key: the company (or service) on the first line, what happened on the second.
function statusView(connection) {
  const base = { title: STATUS_TITLE, kind: 'message', message: '', caption: '', tone: 'muted', bars: [] };
  if (connection.status === 'loading') return { ...base, kind: 'loading', message: '読み込み中' };
  if (connection.status !== 'connected' || !connection.snapshot) return { ...base, message: '未接続' };
  const status = connection.snapshot.status;
  if (!status) return { ...base, message: '未取得' };
  // One line per company says how each one is; the line under them gives the affected parts or a recent reset.
  if (status.providers && status.providers.length > 0) {
    const rows = status.providers.map(p => ({ name: p.company, state: STATE_WORDS[p.tone], tone: p.tone }));
    const reset = status.tone === 'reset';
    const affected = status.providers.filter(p => p.tone === 'critical' || p.tone === 'warning').map(p => p.detail).filter(Boolean);
    return { ...base, kind: 'status', tone: status.tone, rows, note: reset ? status.title : affected.join('、'), noteTone: reset ? 'reset' : 'muted', page: status.page };
  }
  // A tray app that does not send company lines: the summary as a headline.
  const space = status.title.indexOf(' ');
  const lines = space > 0 ? [status.title.slice(0, space), status.title.slice(space + 1)] : [status.title];
  return { ...base, kind: 'status', tone: status.tone, lines, detail: status.detail, page: status.page };
}

const WAITING_TITLE = '対応待ち';
const REASON_WORDS = { question: '質問', confirm: '確認', finished: '完了' };
// The 対応待ち key: how many sessions wait, and the one that has waited longest.
function waitingView(connection, now) {
  const base = { title: WAITING_TITLE, kind: 'message', message: '', caption: '', tone: 'muted', bars: [] };
  if (connection.status === 'loading') return { ...base, kind: 'loading', message: '読み込み中' };
  if (connection.status !== 'connected' || !connection.snapshot) return { ...base, message: '未接続' };
  const { items, unreadable } = connection.snapshot.waiting;
  const waits = items.filter(i => i.reason !== 'finished'), first = waits[0];
  const top = first ? { service: first.service, place: first.place, reason: REASON_WORDS[first.reason], minutes: Math.max(0, Math.floor((now - Date.parse(first.since)) / 60000)) } : null;
  const tone = !top ? 'muted' : top.minutes >= 10 ? 'critical' : 'warning';
  return { ...base, kind: 'waiting', count: waits.length, top, finished: items.length - waits.length, unreadable: unreadable.map(u => u.service), tone };
}

const FONT = "'Segoe UI','Yu Gothic UI','Meiryo',sans-serif";
const esc = value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const text = (x, y, value, size, fill, weight = 400, anchor = 'middle') =>
  `<text x="${x}" y="${y}" font-family="${FONT}" font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="${anchor}">${esc(value)}</text>`;
const fraction = value => value === null ? 0 : Math.max(0, Math.min(100, value)) / 100;
// Names come from the service; cut them to what fits the layout (12, 8 or 5 characters for 1, 2 or 3 bars).
const fit = (value, max) => { const chars = Array.from(value || ''); return chars.length > max ? chars.slice(0, max - 1).join('') + '…' : chars.join(''); };
const caption = (bar, n) => fit(bar.short || bar.label, n === 3 ? 5 : n === 2 ? 8 : 12);

const toneColor = (palette, tone) => ({ normal: palette.accent, warning: palette.warning, critical: palette.critical }[tone] || palette.muted);

// Approximate text width in multiples of the font size: Latin letters are about half as wide as Japanese ones.
const widthEm = value => Array.from(value).reduce((sum, c) => sum + (c.charCodeAt(0) < 0x2000 ? 0.6 : 1), 0);
const fitWidth = (value, maxEm) => {
  if (widthEm(value) <= maxEm) return value;
  let out = '';
  for (const c of Array.from(value)) { if (widthEm(out + c) + 1 > maxEm) break; out += c; }
  return out + '…';
};
// At most two lines; the first ends after a space or 、, or before （, when one falls in its second half.
function wrap(value, maxEm) {
  if (widthEm(value) <= maxEm) return [value];
  const chars = Array.from(value);
  let cut = 0;
  while (cut < chars.length && widthEm(chars.slice(0, cut + 1).join('')) <= maxEm) cut++;
  const soft = Math.max(chars.lastIndexOf(' ', cut - 1) + 1, chars.lastIndexOf('、', cut - 1) + 1, chars.lastIndexOf('（', cut - 1));
  const end = soft >= cut / 2 ? soft : cut;
  return [chars.slice(0, end).join('').trimEnd(), fitWidth(chars.slice(end).join('').trimStart(), maxEm)];
}
function statusRows(view, palette) {
  const colors = { critical: palette.critical, warning: palette.warning, normal: palette.accent };
  const rows = view.rows.slice(0, 3), ys = rows.length <= 2 ? [58, 84] : [50, 72, 94];
  const body = rows.map((row, i) => {
    // The company name is 14 px; the state takes the rest of the line, up to 15 px.
    const name = fitWidth(row.name, 7), room = 120 - widthEm(name) * 14 - 6;
    const size = Math.max(10, Math.min(15, Math.floor(room / widthEm(row.state))));
    return text(12, ys[i], name, 14, palette.text, 700, 'start') + text(132, ys[i], row.state, size, colors[row.tone] || palette.muted, 700, 'end');
  }).join('');
  const note = view.note ? wrap(view.note, 120 / 11) : [];
  const noteYs = rows.length <= 2 ? (note.length === 1 ? [116] : [110, 126]) : (note.length === 1 ? [120] : [114, 128]);
  return body + note.map((line, i) => text(72, noteYs[i], line, 11, view.noteTone === 'reset' ? palette.accent : palette.muted)).join('');
}
const minutesText = minutes => minutes < 60 ? minutes + '分' : Math.floor(minutes / 60) + '時間' + minutes % 60 + '分';
function waitingBody(view, palette) {
  const color = toneColor(palette, view.tone);
  const note = view.unreadable.length ? text(72, 136, fitWidth(view.unreadable.join('・') + ' 読めません', 120 / 11), 11, palette.muted) : '';
  if (!view.top) return text(72, 76, '対応待ちなし', 16, palette.text, 700) + (view.finished ? text(72, 102, '完了 ' + view.finished + '件', 13, palette.accent, 700) : '') + note;
  const time = minutesText(view.top.minutes), size = Math.max(14, Math.min(26, Math.floor(124 / widthEm(time))));
  return text(132, 22, String(view.count), 18, color, 700, 'end') +
    text(72, 56, fitWidth(view.top.service, 124 / 18), 18, palette.text, 700) +
    (view.top.place ? text(72, 78, fitWidth(view.top.place, 124 / 13), 13, palette.muted) : '') +
    text(72, 100, view.top.reason, 13, color, 700) + text(72, 128, time, size, color, 700);
}
function statusBody(view, palette) {
  if (view.rows) return statusRows(view, palette);
  const color = { critical: palette.critical, warning: palette.warning, reset: palette.accent, normal: palette.text }[view.tone] || palette.muted;
  const ys = view.lines.length === 1 ? [80] : [58, 86];
  // 22 px when the line fits, smaller for longer lines (not below 13 px); what still does not fit is cut.
  const lines = view.lines.map((line, i) => {
    const size = Math.max(13, Math.min(22, Math.floor(124 / Math.max(1, widthEm(line)))));
    return text(72, ys[i], fitWidth(line, 124 / size), size, i === view.lines.length - 1 ? color : palette.text, 700);
  }).join('');
  const detail = view.detail ? wrap(view.detail, 120 / 11) : [];
  const detailYs = detail.length === 1 ? [118] : [112, 128];
  return lines + detail.map((line, i) => text(72, detailYs[i], line, 11, palette.muted)).join('');
}

// Layouts for one, two and three bars on a 144×144 key. When every bar resets at the same time (Claude: 週 and
// Fable), the time is shown once at the bottom; otherwise (`own`) each bar shows its own time under it.
const timeColor = (palette, bar) => bar.tone === 'muted' ? palette.muted : palette.text;
// The word リセット small, the time itself large, right-aligned.
const resetText = (y, value, labelSize, size, fill, muted) =>
  `<text x="132" y="${y}" font-family="${FONT}" text-anchor="end"><tspan font-size="${labelSize}" fill="${muted}">リセット </tspan>` +
  `<tspan font-size="${size}" font-weight="700" fill="${fill}">${esc(value)}</tspan></text>`;
function vertical(view, palette, own) {
  const n = view.bars.length, three = n === 3, xs = n === 1 ? [72] : n === 2 ? [44, 100] : [30, 72, 114];
  const w = n === 1 ? 30 : n === 2 ? 26 : 18, top = 44, height = own ? 44 : 46;
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), x = xs[i], fill = Math.round(height * fraction(bar.value));
    const size = bar.value === null ? (three ? 11 : 13) : (three ? 15 : n === 2 ? 20 : 24);
    return text(x, 38, caption(bar, n), three ? 11 : 13, palette.muted) +
      `<rect x="${x - w / 2}" y="${top}" width="${w}" height="${height}" rx="5" fill="${palette.track}"/>` +
      (fill > 0 ? `<rect x="${x - w / 2}" y="${top + height - fill}" width="${w}" height="${fill}" rx="5" fill="${color}"/>` : '') +
      text(x, own ? 108 : 112, bar.text, size, bar.value === null ? palette.muted : color, 700) +
      (own && bar.reset ? text(x, 128, bar.reset, three ? 11 : 14, timeColor(palette, bar), 700) : '');
  }).join('');
}
// Sizes follow the number of bars: one thick bar with a large number, two medium bars, three thin ones.
const ROW_SIZES = { 1: { label: 14, percent: 28, missing: 16, bar: 20 }, 2: { label: 13, percent: 20, missing: 14, bar: 12 }, 3: { label: 12, percent: 15, missing: 12, bar: 8 } };
function horizontal(view, palette, own) {
  const n = view.bars.length, three = n === 3, size = ROW_SIZES[n] || ROW_SIZES[3];
  const rows = n === 1 ? [62] : n === 2 ? (own ? [40, 92] : [44, 90]) : (own ? [38, 74, 110] : [36, 66, 96]);
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), y = rows[i], top = y + (n === 1 ? 8 : 5), width = 120, fill = Math.round(width * fraction(bar.value));
    // Three bars have no room for a line under each bar, so each bar's own time follows its name.
    const nameLine = three && own && bar.reset ?
      `<text x="12" y="${y}" font-family="${FONT}" text-anchor="start"><tspan font-size="${size.label}" fill="${palette.muted}">${esc(caption(bar, n))}</tspan>` +
      `<tspan dx="5" font-size="11" font-weight="700" fill="${timeColor(palette, bar)}">${esc(bar.reset)}</tspan></text>` : text(12, y, caption(bar, n), size.label, palette.muted, 400, 'start');
    const reset = !own || !bar.reset || three ? '' : resetText(y + 30, bar.reset, 12, 15, timeColor(palette, bar), palette.muted);
    return nameLine +
      text(132, y, bar.text, bar.value === null ? size.missing : size.percent, bar.value === null ? palette.muted : color, 700, 'end') +
      `<rect x="12" y="${top}" width="${width}" height="${size.bar}" rx="${Math.min(5, size.bar / 2)}" fill="${palette.track}"/>` +
      (fill > 0 ? `<rect x="12" y="${top}" width="${fill}" height="${size.bar}" rx="${Math.min(5, size.bar / 2)}" fill="${color}"/>` : '') + reset;
  }).join('');
}
function ring(view, palette, own) {
  const n = view.bars.length, single = n === 1, three = n === 3;
  const xs = single ? [72] : three ? [26, 72, 118] : [40, 104], r = single ? 32 : three ? 17 : 24;
  const cy = single ? 72 : three ? 58 : 62, stroke = single ? 9 : three ? 6 : 7, circumference = 2 * Math.PI * r;
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), x = xs[i], arc = (circumference * fraction(bar.value)).toFixed(1);
    const size = bar.value === null ? (single ? 14 : three ? 10 : 12) : (single ? 22 : three ? 11 : 15);
    // One ring has its window name beside the key title; several rings have theirs under each ring.
    const name = single ? text(132, 21, caption(bar, n), 12, palette.muted, 400, 'end') : text(x, cy + r + (three ? 14 : 16), caption(bar, n), three ? 11 : 12, palette.muted);
    return `<circle cx="${x}" cy="${cy}" r="${r}" fill="none" stroke="${palette.track}" stroke-width="${stroke}"/>` +
      (bar.value ? `<circle cx="${x}" cy="${cy}" r="${r}" fill="none" stroke="${color}" stroke-width="${stroke}" stroke-linecap="round" stroke-dasharray="${arc} ${circumference.toFixed(1)}" transform="rotate(-90 ${x} ${cy})"/>` : '') +
      text(x, cy + (single ? 7 : three ? 4 : 5), bar.text, size, bar.value === null ? palette.muted : color, 700) + name +
      (own && bar.reset ? text(x, three ? 110 : 124, bar.reset, three ? 11 : 14, timeColor(palette, bar), 700) : '');
  }).join('');
}
const sharedReset = view => { const first = view.bars.length > 0 ? view.bars[0].reset : ''; return first && view.bars.every(b => b.reset === first) ? first : ''; };

function renderKey(view, settings) {
  const palette = paletteFor(settings.palette);
  let body;
  if (view.kind === 'status') body = statusBody(view, palette);
  else if (view.kind === 'waiting') body = waitingBody(view, palette);
  else if (view.kind !== 'usage') body = text(72, 86, view.message, view.message.length > 6 ? 14 : 18, view.kind === 'loading' ? palette.muted : palette.text, 700);
  else {
    // A caption (e.g. 保存された値) takes the bottom line instead of the reset time.
    const shared = view.caption ? '' : sharedReset(view), own = !view.caption && !shared;
    const time = view.bars.every(b => b.tone === 'muted') ? palette.muted : palette.text;
    body = (settings.style === 'ring' ? ring : settings.style === 'horizontal' ? horizontal : vertical)(view, palette, own) +
      (view.caption ? text(72, 134, view.caption, 12, palette.muted) : shared ? resetText(132, shared, 12, 18, time, palette.muted) : '');
  }
  return '<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">' +
    `<rect width="144" height="144" rx="18" fill="${palette.background}"/>` +
    text(12, 21, view.title, 15, palette.text, 700, 'start') + body + '</svg>';
}

module.exports = { keyView, statusView, waitingView, renderKey, STATUS_TITLE, WAITING_TITLE };
