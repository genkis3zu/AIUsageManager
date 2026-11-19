'use strict';
const { userSid, pipeName, readSnapshot } = require('./pipe');
const { initialConnection, nextConnection } = require('./state');
const { ACTIONS, normalizeKeySettings, nextStyle, nextPalette } = require('./settings');
const { keyView, renderKey } = require('./render');
const location = require('./location');
const { parseArgs, StreamDeck } = require('./streamdeck');

// After a launch the tray app needs a moment to open its pipe and publish its first values.
const POLL_MS = 15000, LONG_PRESS_MS = 500, RELAUNCH_POLLS_MS = [2000, 5000, 10000], NOTICE_MS = 5000;
const SETTING_KEYS = ['style', 'metric', 'showReset', 'palette'];

// Shows the tray app's shared values on the keys. It never queries an AI service itself.
function createPlugin({ deck, pipe, readSnapshot: read, resolveAppPath, launchApp, now = Date.now, timers }) {
  const keys = new Map();
  let connection = initialConnection(), interval = null, pending = null, manualPath = '', starting = false;
  const message = (key, text) => ({ title: key.service, kind: 'message', message: text, caption: '', tone: 'muted', bars: [] });
  const background = promise => { promise.catch(() => {}); };

  function render(context) {
    const key = keys.get(context);
    if (!key) return;
    const view = key.notice ? message(key, key.notice) : starting ? message(key, '起動中') : keyView(key.service, connection, key.settings, now());
    if (!key.notice && !starting) key.needsLogin = view.needsLogin === true;
    const svg = renderKey(view, key.settings);
    if (svg === key.lastImage) return;
    key.lastImage = svg;
    deck.setImage(context, svg);
  }
  const renderAll = () => { for (const context of keys.keys()) render(context); };

  function poll() {
    if (pending) return pending;
    pending = (async () => {
      let result;
      // The pipe name may be a function: the Windows user SID is looked up again until it is found.
      try { result = await read(typeof pipe === 'function' ? pipe() : pipe); } catch { result = { kind: 'failed' }; }
      connection = nextConnection(connection, result);
      if (connection.status === 'connected') starting = false;
      pending = null;
      renderAll();
    })();
    return pending;
  }

  function showNotice(context) {
    const key = keys.get(context);
    if (!key) return;
    key.notice = 'アプリの場所を設定';
    deck.showAlert(context);
    render(context);
    timers.setTimeout(() => { const current = keys.get(context); if (current) { current.notice = null; render(context); } }, NOTICE_MS);
  }

  async function press(context, long) {
    const key = keys.get(context);
    // While the tray app is starting, presses are ignored so a second press does not start it again.
    if (!key || starting) return;
    if (connection.status === 'disconnected') {
      const file = resolveAppPath(manualPath);
      if (!file) { showNotice(context); return; }
      starting = true;
      renderAll();
      try { await launchApp(file); } catch { starting = false; renderAll(); showNotice(context); return; }
      RELAUNCH_POLLS_MS.forEach((ms, i) => timers.setTimeout(() => {
        background(poll().then(() => {
          if (i === RELAUNCH_POLLS_MS.length - 1 && starting) { starting = false; renderAll(); }
        }));
      }, ms));
      return;
    }
    // A key showing 要ログイン starts that service's official login through the tray app.
    if (!long && key.needsLogin) {
      const file = resolveAppPath(manualPath);
      if (!file) { showNotice(context); return; }
      try { await launchApp(file, ['--login-' + key.service.toLowerCase()]); } catch { showNotice(context); }
      return;
    }
    key.settings = long ? { ...key.settings, palette: nextPalette(key.settings.palette) } : { ...key.settings, style: nextStyle(key.settings.style) };
    deck.setSettings(context, key.settings);
    render(context);
  }

  async function handle(message) {
    const event = message && message.event, context = message && message.context;
    const payload = (message && message.payload) || {};
    switch (event) {
      case 'willAppear': {
        const service = ACTIONS[message.action];
        if (!service) return;
        const raw = payload.settings || {}, settings = normalizeKeySettings(service, raw);
        keys.set(context, { service, settings, notice: null, lastImage: null, pressedAt: null });
        // Saving normalized values keeps the settings panel and the key in agreement.
        if (SETTING_KEYS.some(name => raw[name] !== settings[name])) deck.setSettings(context, settings);
        render(context);
        if (!interval) { interval = timers.setInterval(() => { background(poll()); }, POLL_MS); await poll(); }
        return;
      }
      case 'willDisappear':
        keys.delete(context);
        if (keys.size === 0 && interval) { timers.clearInterval(interval); interval = null; }
        return;
      case 'didReceiveSettings': {
        const key = keys.get(context);
        if (!key) return;
        key.settings = normalizeKeySettings(key.service, payload.settings);
        render(context);
        return;
      }
      case 'didReceiveGlobalSettings': {
        const settings = payload.settings || {};
        manualPath = typeof settings.appPath === 'string' ? settings.appPath : '';
        return;
      }
      case 'keyDown': {
        const key = keys.get(context);
        if (key) key.pressedAt = now();
        return;
      }
      case 'keyUp': {
        const key = keys.get(context);
        if (!key) return;
        const long = key.pressedAt !== null && now() - key.pressedAt > LONG_PRESS_MS;
        key.pressedAt = null;
        await press(context, long);
        return;
      }
      default:
    }
  }

  return { handle, poll };
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const deck = new StreamDeck(args);
  let sid = null;
  const file = location.locationFile();
  const plugin = createPlugin({
    deck, pipe: () => { if (!sid) sid = userSid(); return pipeName(sid); }, readSnapshot,
    resolveAppPath: manual => location.resolveAppPath(manual, file),
    launchApp: (file, args) => location.launchApp(file, undefined, args),
    timers: { setInterval, clearInterval, setTimeout },
  });
  for (const event of ['willAppear', 'willDisappear', 'didReceiveSettings', 'didReceiveGlobalSettings', 'keyDown', 'keyUp'])
    deck.on(event, message => { plugin.handle(message).catch(() => {}); });
  deck.connect();
  deck.getGlobalSettings();
}

module.exports = { createPlugin, main };
