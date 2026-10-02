'use strict';

// Downloads the Slate installer from the GitHub release matching this package's version
// and installs it per-user (no admin). Shared by postinstall and the `slate` command.

const fs = require('fs');
const os = require('os');
const path = require('path');
const https = require('https');
const { spawn, spawnSync } = require('child_process');
const { version } = require('../package.json');

const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
const installDir = path.join(localAppData, 'Programs', 'Slate');
const exePath = path.join(installDir, 'Slate.exe');

function download(url, dest, redirectsLeft = 5) {
  return new Promise((resolve, reject) => {
    https
      .get(url, { headers: { 'User-Agent': 'kazuna-slate-npm' } }, (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          if (redirectsLeft === 0) return reject(new Error('Too many redirects while downloading Slate.'));
          return resolve(download(res.headers.location, dest, redirectsLeft - 1));
        }
        if (res.statusCode !== 200) {
          res.resume();
          return reject(new Error(`Download failed (HTTP ${res.statusCode}): ${url}`));
        }
        const file = fs.createWriteStream(dest);
        res.pipe(file);
        file.on('finish', () => file.close(resolve));
        file.on('error', reject);
      })
      .on('error', reject);
  });
}

async function install() {
  if (process.platform !== 'win32') throw new Error('Slate only runs on Windows.');

  const url = `https://github.com/kazuna1/slate/releases/download/v${version}/SlateSetup.exe`;
  const setup = path.join(os.tmpdir(), `SlateSetup-${version}.exe`);

  console.log(`Downloading Slate ${version}...`);
  await download(url, setup);

  console.log('Installing...');
  const result = spawnSync(setup, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=autostart'], { stdio: 'ignore' });
  fs.rmSync(setup, { force: true });
  if (result.status !== 0) throw new Error(`Slate setup failed (exit code ${result.status}).`);
}

function uninstall() {
  const uninstaller = path.join(installDir, 'unins000.exe');
  if (!fs.existsSync(uninstaller)) throw new Error('Slate is not installed.');
  const result = spawnSync(uninstaller, ['/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART'], { stdio: 'ignore' });
  if (result.status !== 0) throw new Error(`Slate uninstall failed (exit code ${result.status}).`);
}

/** Starts Slate in the background. A second copy exits on its own if one is already running. */
function launch() {
  spawn(exePath, [], { detached: true, stdio: 'ignore' }).unref();
}

module.exports = { install, uninstall, launch, exePath, version };
