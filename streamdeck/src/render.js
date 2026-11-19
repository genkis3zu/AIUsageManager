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

const FONT = "'Segoe UI','Yu Gothic UI','Meiryo',sans-serif";
const esc = value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const text = (x, y, value, size, fill, weight = 400, anchor = 'middle') =>
  `<text x="${x}" y="${y}" font-family="${FONT}" font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="${anchor}">${esc(value)}</text>`;
const fraction = value => value === null ? 0 : Math.max(0, Math.min(100, value)) / 100;
// Names come from the service; cut them to what fits the layout (12, 8 or 5 characters for 1, 2 or 3 bars).
const fit = (value, max) => { const chars = Array.from(value || ''); return chars.length > max ? chars.slice(0, max - 1).join('') + '…' : chars.join(''); };
const caption = (bar, n) => fit(bar.short || bar.label, n === 3 ? 5 : n === 2 ? 8 : 12);

const toneColor = (palette, tone) => ({ normal: palette.accent, warning: palette.warning, critical: palette.critical }[tone] || palette.muted);

// Layouts for one, two and three bars on a 144×144 key.
function vertical(view, palette) {
  const n = view.bars.length, three = n === 3, xs = n === 1 ? [72] : n === 2 ? [44, 100] : [30, 72, 114], w = three ? 18 : 22;
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), x = xs[i], height = 54, top = 40, fill = Math.round(height * fraction(bar.value));
    return text(x, 35, caption(bar, n), 11, palette.muted) +
      `<rect x="${x - w / 2}" y="${top}" width="${w}" height="${height}" rx="5" fill="${palette.track}"/>` +
      (fill > 0 ? `<rect x="${x - w / 2}" y="${top + height - fill}" width="${w}" height="${fill}" rx="5" fill="${color}"/>` : '') +
      text(x, 114, bar.text, bar.value === null ? (three ? 11 : 13) : (three ? 15 : 17), bar.value === null ? palette.muted : color, 700) +
      (view.caption ? '' : text(x, 133, bar.reset, three ? 10 : 11, palette.muted));
  }).join('');
}
function horizontal(view, palette) {
  const n = view.bars.length, three = n === 3, rows = n === 1 ? [62] : n === 2 ? [46, 92] : [34, 68, 102];
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), y = rows[i], width = 120, fill = Math.round(width * fraction(bar.value)), h = three ? 7 : 9;
    return text(12, y, caption(bar, n), three ? 11 : 12, palette.muted, 400, 'start') +
      text(132, y, bar.text, bar.value === null ? (three ? 11 : 13) : (three ? 14 : 17), bar.value === null ? palette.muted : color, 700, 'end') +
      `<rect x="12" y="${y + 5}" width="${width}" height="${h}" rx="3" fill="${palette.track}"/>` +
      (fill > 0 ? `<rect x="12" y="${y + 5}" width="${fill}" height="${h}" rx="3" fill="${color}"/>` : '') +
      (view.caption || !bar.reset ? '' : text(12, y + (three ? 23 : 29), (three ? '' : 'リセット ') + bar.reset, three ? 10 : 11, palette.muted, 400, 'start'));
  }).join('');
}
function ring(view, palette) {
  const n = view.bars.length, single = n === 1, three = n === 3;
  const xs = single ? [72] : three ? [26, 72, 118] : [42, 102], r = single ? 34 : three ? 16 : 22, cy = single ? 76 : three ? 66 : 70, stroke = single ? 9 : three ? 6 : 7;
  const circumference = 2 * Math.PI * r;
  return view.bars.map((bar, i) => {
    const color = toneColor(palette, bar.tone), x = xs[i], arc = (circumference * fraction(bar.value)).toFixed(1);
    const size = single ? (bar.value === null ? 13 : 19) : three ? (bar.value === null ? 10 : 11) : (bar.value === null ? 10 : 14);
    return `<circle cx="${x}" cy="${cy}" r="${r}" fill="none" stroke="${palette.track}" stroke-width="${stroke}"/>` +
      (bar.value ? `<circle cx="${x}" cy="${cy}" r="${r}" fill="none" stroke="${color}" stroke-width="${stroke}" stroke-linecap="round" stroke-dasharray="${arc} ${circumference.toFixed(1)}" transform="rotate(-90 ${x} ${cy})"/>` : '') +
      text(x, cy + (single ? 6 : 4), bar.text, size, bar.value === null ? palette.muted : color, 700) +
      (single ? '' : text(x, cy + r + 15, caption(bar, n), three ? 10 : 11, palette.muted)) +
      (view.caption || !bar.reset ? '' : text(x, 136, bar.reset, three ? 10 : 11, palette.muted));
  }).join('');
}

function renderKey(view, settings) {
  const palette = paletteFor(settings.palette);
  let body;
  if (view.kind !== 'usage') body = text(72, 86, view.message, view.message.length > 6 ? 14 : 18, view.kind === 'loading' ? palette.muted : palette.text, 700);
  else body = (settings.style === 'ring' ? ring : settings.style === 'horizontal' ? horizontal : vertical)(view, palette) +
    (view.caption ? text(72, 134, view.caption, 12, palette.muted) : '');
  return '<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">' +
    `<rect width="144" height="144" rx="18" fill="${palette.background}"/>` +
    text(12, 21, view.title, 15, palette.text, 700, 'start') + body + '</svg>';
}

module.exports = { keyView, renderKey };
