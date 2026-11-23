const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseArgs, StreamDeck } = require('../src/streamdeck');

const ARGV = ['-port', '28196', '-pluginUUID', 'abc', '-registerEvent', 'registerPlugin', '-info', '{"application":{}}'];
function setup() {
  const socket = { sent: [], send(text) { this.sent.push(JSON.parse(text)); } };
  let url, exits = 0;
  const deck = new StreamDeck(parseArgs(ARGV), { createSocket: value => { url = value; return socket; }, exit: () => { exits++; } });
  return { deck, socket, url: () => url, exits: () => exits };
}

test('parseArgs reads the four launch arguments', () => {
  assert.deepEqual(parseArgs(ARGV), { port: 28196, pluginUUID: 'abc', registerEvent: 'registerPlugin', info: { application: {} } });
  assert.throws(() => parseArgs(['-port', '0', '-pluginUUID', 'abc', '-registerEvent', 'r', '-info', '{}']));
  assert.throws(() => parseArgs(['-port', 'x', '-pluginUUID', 'abc', '-registerEvent', 'r', '-info', '{}']));
  assert.throws(() => parseArgs(['-port', '28196', '-registerEvent', 'r', '-info', '{}']));
  assert.throws(() => parseArgs(['-port', '28196', '-pluginUUID', 'abc', '-registerEvent', 'r', '-info', '{bad']));
});
test('registers first, then flushes commands sent before open', () => {
  const s = setup();
  s.deck.setSettings('ctx', { a: 1 });
  s.deck.connect();
  s.deck.showAlert('ctx');
  assert.equal(s.url(), 'ws://127.0.0.1:28196');
  assert.equal(s.socket.sent.length, 0);
  s.socket.onopen();
  assert.deepEqual(s.socket.sent, [
    { event: 'registerPlugin', uuid: 'abc' },
    { event: 'setSettings', context: 'ctx', payload: { a: 1 } },
    { event: 'showAlert', context: 'ctx' },
  ]);
  s.deck.getGlobalSettings();
  assert.deepEqual(s.socket.sent.at(-1), { event: 'getGlobalSettings', context: 'abc' });
});
test('setImage sends a base64 SVG data URL', () => {
  const s = setup(); s.deck.connect(); s.socket.onopen();
  s.deck.setImage('ctx', '<svg>日本</svg>');
  assert.deepEqual(s.socket.sent.at(-1), { event: 'setImage', context: 'ctx', payload: { image: 'data:image/svg+xml;base64,' + Buffer.from('<svg>日本</svg>', 'utf8').toString('base64'), target: 0 } });
});
test('showOk asks Stream Deck to show the check mark on the key', () => {
  const s = setup(); s.deck.connect(); s.socket.onopen();
  s.deck.showOk('ctx');
  assert.deepEqual(s.socket.sent.at(-1), { event: 'showOk', context: 'ctx' });
});
test('openUrl asks Stream Deck to open the page in the browser', () => {
  const s = setup(); s.deck.connect(); s.socket.onopen();
  s.deck.openUrl('https://status.claude.com/');
  assert.deepEqual(s.socket.sent.at(-1), { event: 'openUrl', payload: { url: 'https://status.claude.com/' } });
});
test('incoming events are emitted by name and bad JSON is ignored', () => {
  const s = setup(), got = []; s.deck.connect(); s.socket.onopen();
  s.deck.on('keyUp', event => got.push(event));
  s.socket.onmessage({ data: '{"event":"keyUp","context":"c"}' });
  s.socket.onmessage({ data: '{bad' });
  s.socket.onmessage({ data: '{"no":"event"}' });
  s.socket.onmessage({ data: '{"event":"error"}' });
  assert.deepEqual(got, [{ event: 'keyUp', context: 'c' }]);
});
test('socket close exits once', () => {
  const s = setup(); s.deck.connect(); s.socket.onopen();
  s.socket.onclose(); s.socket.onclose();
  assert.equal(s.exits(), 1);
});
