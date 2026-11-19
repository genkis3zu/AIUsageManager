'use strict';
const net = require('node:net');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const PREFIX = '\\\\.\\pipe\\AIUsageManager.Snapshot.';

// The tray app names its pipe after the Windows user SID, so the plugin needs the same value.
// The absolute System32 path avoids picking up another `whoami` from PATH (for example Git's coreutils).
function userSid(exec = execFileSync, systemRoot = process.env.SystemRoot || 'C:\\Windows') {
  try {
    const whoami = path.win32.join(systemRoot, 'System32', 'whoami.exe');
    const output = String(exec(whoami, ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true, timeout: 5000 }));
    const match = /^"[^"]*","([^"]*)"$/.exec(output.trim());
    return match && /^S-1-\d+(-\d+)+$/.test(match[1]) ? match[1] : null;
  } catch {
    return null;
  }
}

function pipeName(sid) { return sid ? PREFIX + sid : null; }

// One connection attempt. 'missing' means the pipe did not exist at that moment.
function attempt(name, connect, timeoutMs, maxBytes) {
  return new Promise(resolve => {
    let done = false, socket = null, size = 0;
    const chunks = [];
    const finish = result => {
      if (done) return;
      done = true; clearTimeout(timer);
      if (socket) socket.destroy();
      resolve(result);
    };
    const timer = setTimeout(() => finish({ kind: 'failed' }), timeoutMs);
    try { socket = connect(name); } catch { finish({ kind: 'failed' }); return; }
    socket.on('error', error => finish(error && error.code === 'ENOENT' ? { kind: 'missing' } : { kind: 'failed' }));
    socket.on('data', chunk => {
      size += chunk.length;
      if (size > maxBytes) finish({ kind: 'failed' }); else chunks.push(chunk);
    });
    socket.on('end', () => {
      if (size === 0) { finish({ kind: 'failed' }); return; }
      try { finish({ kind: 'ok', json: new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks)) }); }
      catch { finish({ kind: 'failed' }); }
    });
    socket.on('close', () => finish({ kind: 'failed' }));
  });
}

// The tray app closes and recreates its pipe after every reader (and waits 1 s after a write error),
// so a missing pipe is retried for a short window before the app is treated as not running.
async function readSnapshot(name, options = {}) {
  const { connect = net.connect, retryMs = 2000, retryDelayMs = 250, timeoutMs = 2000, maxBytes = 65536 } = options;
  if (!name) return { kind: 'absent' };
  const start = Date.now();
  for (;;) {
    const result = await attempt(name, connect, timeoutMs, maxBytes);
    if (result.kind !== 'missing') return result;
    if (Date.now() - start >= retryMs) return { kind: 'absent' };
    await new Promise(resolve => setTimeout(resolve, retryDelayMs));
  }
}

module.exports = { userSid, pipeName, readSnapshot };
