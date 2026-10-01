'use strict';

const { install, launch } = require('./lib/slate');

if (process.env.SLATE_SKIP_INSTALL || process.platform !== 'win32') process.exit(0);

install()
  .then(() => {
    launch();
    console.log('Slate is installed and running. Press Win + Space.');
  })
  .catch((err) => {
    // Never fail `npm install`: the `slate` command retries the install on first run.
    console.warn(`kazuna-slate: ${err.message}`);
    console.warn('Run `slate` to try again, or download from https://github.com/kazuna1/slate');
  });
