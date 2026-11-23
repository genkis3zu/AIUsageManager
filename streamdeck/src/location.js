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

// Starts the tray app hidden; a tray app that is already running is left alone and shows no window.
const BACKGROUND_START = '--background';
// Opens the small いまやってること window as its own short process; the tray app is not involved.
const NOW_DOING = '--now-doing';
// The only arguments the plugin may pass: ask the tray app to start one service's official login, start it in the
// background, or open the いまやってること window.
const APP_ARGUMENTS = ['--login-claude', '--login-codex', '--login-grok', BACKGROUND_START, NOW_DOING];

// Detached so the tray app keeps running when Stream Deck or this plugin exits. The working directory is
// the app's own folder: inheriting the plugin folder would keep it locked and break plugin updates.
function launchApp(file, spawn = childProcess.spawn, args = []) {
  if (!validPath(file)) return Promise.reject(new Error('Invalid app path'));
  if (!Array.isArray(args) || args.length > 1 || args.some(a => !APP_ARGUMENTS.includes(a))) return Promise.reject(new Error('Invalid app arguments'));
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { shell: false, detached: true, stdio: 'ignore', windowsHide: false, cwd: path.win32.dirname(file) });
    child.once('error', () => reject(new Error('Unable to launch app')));
    child.once('spawn', () => { child.unref(); resolve(); });
  });
}

module.exports = { locationFile, readAppLocation, resolveAppPath, launchApp, BACKGROUND_START, NOW_DOING };
