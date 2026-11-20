'use strict';
const PLUGIN = 'io.github.genkis3zu.aiusagemanager';
const ACTIONS = { [PLUGIN + '.claude']: 'Claude', [PLUGIN + '.codex']: 'Codex', [PLUGIN + '.grok']: 'Grok' };
// The AIの状況 key (outages and weekly resets) has no settings of its own.
const STATUS_ACTION = PLUGIN + '.status';
// The 対応待ち key (sessions waiting for the user) has no settings either; pressing it opens one of these four apps.
const WAITING_ACTION = PLUGIN + '.waiting';
const APP_LINKS = { claude: 'claude://', codex: 'codex://', vscode: 'vscode://', grokbot: 'grokbot://' };
const STYLES = ['ring', 'vertical', 'horizontal'];
const METRICS = ['used', 'remaining'];
const PALETTES = [
  { id: 'green', label: '緑', background: '#07110d', track: '#1d2c25', text: '#eefaf3', muted: '#7f9a8e', accent: '#3fcf8e', warning: '#f5c24c', critical: '#ff6464' },
  { id: 'orange', label: 'オレンジ', background: '#130b05', track: '#30211a', text: '#fff3e9', muted: '#a98c77', accent: '#ff9448', warning: '#ffd463', critical: '#ff5470' },
  { id: 'blue', label: '青', background: '#060c14', track: '#1a2636', text: '#edf5ff', muted: '#7d93ab', accent: '#4fb2ff', warning: '#f5c24c', critical: '#ff6464' },
  { id: 'violet', label: '紫', background: '#0d0814', track: '#251d35', text: '#f4efff', muted: '#9a8db3', accent: '#a98cff', warning: '#f5c24c', critical: '#ff6464' },
  { id: 'rose', label: 'ピンク', background: '#140709', track: '#331c24', text: '#fff0f4', muted: '#b08c96', accent: '#ff7eaa', warning: '#ffd463', critical: '#ff4545' },
  { id: 'slate', label: 'グレー', background: '#0a0c0f', track: '#232831', text: '#f1f4f8', muted: '#8a94a3', accent: '#b4bfcd', warning: '#f5c24c', critical: '#ff6464' },
];

function defaultSettings(service) {
  return { style: 'vertical', metric: 'used', showReset: true, palette: service === 'Claude' ? 'orange' : 'green' };
}
function normalizeKeySettings(service, raw) {
  const value = raw && typeof raw === 'object' ? raw : {}, fallback = defaultSettings(service);
  const showReset = value.showReset === true || value.showReset === 'true' ? true : value.showReset === false || value.showReset === 'false' ? false : fallback.showReset;
  return {
    style: STYLES.includes(value.style) ? value.style : fallback.style,
    metric: METRICS.includes(value.metric) ? value.metric : fallback.metric,
    showReset,
    palette: PALETTES.some(p => p.id === value.palette) ? value.palette : fallback.palette,
  };
}
const nextIn = (list, value) => list[(Math.max(0, list.indexOf(value)) + 1) % list.length];
function nextStyle(style) { return nextIn(STYLES, style); }
function nextPalette(id) { return nextIn(PALETTES.map(p => p.id), id); }
function paletteFor(id) { return PALETTES.find(p => p.id === id) || PALETTES[0]; }

module.exports = { ACTIONS, STATUS_ACTION, WAITING_ACTION, APP_LINKS, PALETTES, STYLES, METRICS, defaultSettings, normalizeKeySettings, nextStyle, nextPalette, paletteFor };
