'use strict';
// Settings panel (Property Inspector). The plugin validates every value again before using it.
const PALETTES = [['green', '緑'], ['orange', 'オレンジ'], ['blue', '青'], ['violet', '紫'], ['rose', 'ピンク'], ['slate', 'グレー']];
const APP_PATH = /^[a-z]:[\\/].*[\\/]aiusagemanager\.exe$/i;
const NOTE = '全キー共通です。空欄のときは、トレイアプリが記録した場所を使います。';
const $ = id => document.getElementById(id);
let socket = null, uuid = '', settings = {};

for (const [id, label] of PALETTES) { const option = document.createElement('option'); option.value = id; option.textContent = label; $('palette').appendChild(option); }

function send(event, payload) {
  if (!socket || socket.readyState !== 1) return;
  socket.send(JSON.stringify(payload === undefined ? { event, context: uuid } : { event, context: uuid, payload }));
}
function showSettings(value) {
  settings = value || {};
  $('style').value = settings.style || 'vertical';
  $('metric').value = settings.metric || 'used';
  $('showReset').checked = settings.showReset !== false;
  if (settings.palette) $('palette').value = settings.palette;
}
function saveSettings() {
  settings = { ...settings, style: $('style').value, metric: $('metric').value, showReset: $('showReset').checked, palette: $('palette').value };
  send('setSettings', settings);
}
function saveAppPath() {
  const value = $('appPath').value.trim(), note = $('appPathNote');
  if (value && !APP_PATH.test(value)) { note.textContent = 'AIUsageManager.exe までの絶対パスを入力してください。'; note.classList.add('error'); return; }
  note.textContent = NOTE; note.classList.remove('error');
  send('setGlobalSettings', { appPath: value });
}

for (const id of ['style', 'metric', 'showReset', 'palette']) $(id).addEventListener('change', saveSettings);
$('appPath').addEventListener('change', saveAppPath);

// Called by Stream Deck when the panel opens.
window.connectElgatoStreamDeckSocket = (port, propertyInspectorUUID, registerEvent, info, actionInfo) => {
  uuid = propertyInspectorUUID;
  try { showSettings(JSON.parse(actionInfo).payload.settings); } catch { showSettings({}); }
  socket = new WebSocket('ws://127.0.0.1:' + port);
  socket.onopen = () => { socket.send(JSON.stringify({ event: registerEvent, uuid })); send('getGlobalSettings'); };
  socket.onmessage = event => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    if (message.event === 'didReceiveSettings') showSettings(message.payload && message.payload.settings);
    if (message.event === 'didReceiveGlobalSettings') {
      const value = message.payload && message.payload.settings && message.payload.settings.appPath;
      $('appPath').value = typeof value === 'string' ? value : '';
    }
  };
};
