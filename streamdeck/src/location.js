'use strict';
const nodeFs = require('node:fs');
const path = require('node:path');
const childProcess = require('node:child_process');
const { validPath } = require('../../obsidian/src/usage');

// The tray app writes this per-user file at start-up. Values from the named pipe are never used to launch anything.
function locationFile(env = process.env) {
  const base = env.LOCALAPPDATA;
  return typeof base === 'string' && /^[a-z]:[\\/]/i.test(base) ? path.win32.join(base, 'AIUsageManager', 'app-location.json') : null;
}

const isFile = (file, fs) => { try { return fs.statSync(file).isFile(); } catch { return false; } };

function readAppLocation(file, fs = nodeFs) {
  try {
    if (!file || fs.statSync(file).size > 4096) return null;
    const value = JSON.parse(fs.readFileSync(file, 'utf8'));
    return value && value.schemaVersion === 1 && validPath(value.path) && isFile(value.path, fs) ? value.path : null;
  } catch {
    return null;
  }
}

function resolveAppPath(manualPath, file, fs = nodeFs) {
  if (validPath(manualPath) && isFile(manualPath, fs)) return manualPath;
  return readAppLocation(file, fs);
}

// Detached so the tray app keeps running when Stream Deck or this plugin exits. The working directory is
// the app's own folder: inheriting the plugin folder would keep it locked and break plugin updates.
function launchApp(file, spawn = childProcess.spawn) {
  if (!validPath(file)) return Promise.reject(new Error('Invalid app path'));
  return new Promise((resolve, reject) => {
    const child = spawn(file, [], { shell: false, detached: true, stdio: 'ignore', windowsHide: false, cwd: path.win32.dirname(file) });
    child.once('error', () => reject(new Error('Unable to launch app')));
    child.once('spawn', () => { child.unref(); resolve(); });
  });
}

module.exports = { locationFile, readAppLocation, resolveAppPath, launchApp };
