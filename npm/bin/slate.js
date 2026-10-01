#!/usr/bin/env node
'use strict';

const fs = require('fs');
const { install, uninstall, launch, exePath, version } = require('../lib/slate');

const HELP = `slate ${version}

  slate              start Slate (installs it first if needed)
  slate --update     reinstall this package's version, then start it
  slate --uninstall  remove the Slate app
`;

async function main(arg) {
  switch (arg) {
    case '--help':
    case '-h':
      process.stdout.write(HELP);
      return;
    case '--version':
    case '-v':
      console.log(version);
      return;
    case '--uninstall':
      uninstall();
      console.log('Slate was uninstalled. Run `npm rm -g kazuna-slate` to remove this command too.');
      return;
    case '--update':
      await install();
      break;
    case undefined:
      if (!fs.existsSync(exePath)) await install();
      break;
    default:
      process.stderr.write(`Unknown option: ${arg}\n\n${HELP}`);
      process.exitCode = 1;
      return;
  }
  launch();
  console.log('Slate is running. Press Win + Space.');
}

main(process.argv[2]).catch((err) => {
  console.error(`slate: ${err.message}`);
  process.exitCode = 1;
});
