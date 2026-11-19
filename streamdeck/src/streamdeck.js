'use strict';
const { EventEmitter } = require('node:events');

// Stream Deck starts the plugin with: -port <n> -pluginUUID <id> -registerEvent <name> -info <json>
function parseArgs(argv) {
  const value = name => { const i = argv.indexOf(name); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : undefined; };
  const port = Number(value('-port')), pluginUUID = value('-pluginUUID'), registerEvent = value('-registerEvent');
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid port');
  if (!pluginUUID || !registerEvent) throw new Error('Missing registration arguments');
  return { port, pluginUUID, registerEvent, info: JSON.parse(value('-info') || '{}') };
}

// Talks to Stream Deck over its local WebSocket using the plugin protocol, without the SDK package.
class StreamDeck extends EventEmitter {
  constructor(args, { createSocket = url => new WebSocket(url), exit = () => process.exit(0) } = {}) {
    super();
    this.args = args; this.createSocket = createSocket; this.exit = exit;
    this.socket = null; this.open = false; this.closed = false; this.queue = [];
  }
  connect() {
    if (this.socket) return;
    const socket = this.createSocket(`ws://127.0.0.1:${this.args.port}`);
    this.socket = socket;
    socket.onopen = () => {
      socket.send(JSON.stringify({ event: this.args.registerEvent, uuid: this.args.pluginUUID }));
      this.open = true;
      for (const text of this.queue.splice(0)) socket.send(text);
    };
    socket.onmessage = event => {
      let message;
      try { message = JSON.parse(String(event.data)); } catch { return; }
      // 'error' has special meaning for EventEmitter and is not a Stream Deck event.
      if (message && typeof message.event === 'string' && message.event !== 'error') this.emit(message.event, message);
    };
    // Stream Deck closes the socket when it quits or unloads the plugin; the process must end, not reconnect.
    socket.onclose = () => { if (this.closed) return; this.closed = true; this.exit(); };
  }
  send(message) {
    if (this.closed) return;
    const text = JSON.stringify(message);
    if (this.open) this.socket.send(text); else this.queue.push(text);
  }
  setImage(context, svg) {
    this.send({ event: 'setImage', context, payload: { image: 'data:image/svg+xml;base64,' + Buffer.from(svg, 'utf8').toString('base64'), target: 0 } });
  }
  setSettings(context, settings) { this.send({ event: 'setSettings', context, payload: settings }); }
  getGlobalSettings() { this.send({ event: 'getGlobalSettings', context: this.args.pluginUUID }); }
  showAlert(context) { this.send({ event: 'showAlert', context }); }
  // Stream Deck opens the page in the default browser; the plugin itself makes no request.
  openUrl(url) { this.send({ event: 'openUrl', payload: { url } }); }
}

module.exports = { parseArgs, StreamDeck };
