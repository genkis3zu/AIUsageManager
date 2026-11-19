'use strict';
const { parseSnapshot } = require('../../obsidian/src/usage');

function initialConnection() { return { status: 'loading', snapshot: null, failures: 0 }; }

// A missing pipe or connected:false means the tray app is not running: show 未接続 at once.
// Other failures (busy, timeout, broken data) keep the last values until two happen in a row.
function nextConnection(prev, result) {
  if (result && result.kind === 'absent') return { status: 'disconnected', snapshot: null, failures: 0 };
  if (result && result.kind === 'ok') {
    let snapshot = null;
    try { snapshot = parseSnapshot(result.json); } catch { snapshot = null; }
    if (snapshot) return snapshot.connected ? { status: 'connected', snapshot, failures: 0 } : { status: 'disconnected', snapshot: null, failures: 0 };
  }
  const failures = prev.failures + 1;
  return failures >= 2 ? { status: 'disconnected', snapshot: null, failures } : { status: prev.status, snapshot: prev.snapshot, failures };
}

module.exports = { initialConnection, nextConnection };
