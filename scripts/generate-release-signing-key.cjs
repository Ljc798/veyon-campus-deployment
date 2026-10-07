#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const REPOSITORY_ROOT = fs.realpathSync(path.resolve(__dirname, '..'));

function isInside(parentPath, childPath) {
  const relative = path.relative(parentPath, childPath);
  return relative === '' || (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative));
}

function resolvePathThroughExistingAncestors(filePath) {
  let cursor = path.resolve(filePath);
  const missingComponents = [];
  while (true) {
    try {
      fs.lstatSync(cursor);
      return path.resolve(fs.realpathSync(cursor), ...missingComponents);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      const parent = path.dirname(cursor);
      if (parent === cursor) throw new Error('Unable to resolve the requested output directory.');
      missingComponents.unshift(path.basename(cursor));
      cursor = parent;
    }
  }
}

function defaultOutputDirectory(environment = process.env) {
  if (process.platform === 'win32') {
    const localAppData = environment.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(localAppData, 'VeyonCampus', 'ReleaseKeys', 'Production');
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'VeyonCampus',
      'ReleaseKeys', 'Production');
  }
  return path.join(os.homedir(), '.local', 'share', 'veyon-campus', 'release-keys', 'production');
}

function validatePassphrase(passphrase) {
  if (typeof passphrase !== 'string' || passphrase.length < 16 || passphrase.length > 1024 ||
      !/^[\x20-\x7e]+$/.test(passphrase)) {
    throw new Error('Passphrase must contain 16–1024 printable ASCII characters.');
  }
}

function generateSigningKeyPair({ outputDirectory, passphrase, repositoryRoot = REPOSITORY_ROOT }) {
  validatePassphrase(passphrase);
  if (typeof outputDirectory !== 'string' || outputDirectory.trim().length === 0)
    throw new Error('An output directory is required.');

  const repositoryPath = fs.realpathSync(path.resolve(repositoryRoot));
  const requestedDirectory = resolvePathThroughExistingAncestors(outputDirectory);
  if (isInside(repositoryPath, requestedDirectory))
    throw new Error('Release signing keys must be stored outside the repository.');

  fs.mkdirSync(requestedDirectory, { recursive: true, mode: 0o700 });
  const realOutputDirectory = fs.realpathSync(requestedDirectory);
  if (isInside(repositoryPath, realOutputDirectory))
    throw new Error('Release signing keys must be stored outside the repository.');
  if (process.platform !== 'win32') fs.chmodSync(realOutputDirectory, 0o700);

  const privateKeyPath = path.join(realOutputDirectory, 'release-private.pem');
  const publicKeyPath = path.join(realOutputDirectory, 'release-public.pem');
  if (fs.existsSync(privateKeyPath) || fs.existsSync(publicKeyPath))
    throw new Error(`A key file already exists in ${realOutputDirectory}. Choose a new directory; existing keys are never overwritten.`);

  const keyPair = crypto.generateKeyPairSync('rsa', {
    modulusLength: 3072,
    publicKeyEncoding: { type: 'spki', format: 'pem' },
    privateKeyEncoding: {
      type: 'pkcs8',
      format: 'pem',
      cipher: 'aes-256-cbc',
      passphrase
    }
  });

  let privateKeyCreated = false;
  let publicKeyCreated = false;
  try {
    const privateDescriptor = fs.openSync(privateKeyPath, 'wx', 0o600);
    privateKeyCreated = true;
    try {
      fs.writeFileSync(privateDescriptor, keyPair.privateKey, 'utf8');
      fs.fsyncSync(privateDescriptor);
    } finally {
      fs.closeSync(privateDescriptor);
    }

    const publicDescriptor = fs.openSync(publicKeyPath, 'wx', process.platform === 'win32' ? 0o600 : 0o644);
    publicKeyCreated = true;
    try {
      fs.writeFileSync(publicDescriptor, keyPair.publicKey, 'utf8');
      fs.fsyncSync(publicDescriptor);
    } finally {
      fs.closeSync(publicDescriptor);
    }
  } catch (error) {
    if (privateKeyCreated) fs.rmSync(privateKeyPath, { force: true });
    if (publicKeyCreated) fs.rmSync(publicKeyPath, { force: true });
    throw error;
  }

  const publicKey = crypto.createPublicKey(keyPair.publicKey);
  const fingerprint = crypto.createHash('sha256')
    .update(publicKey.export({ type: 'spki', format: 'der' }))
    .digest('hex')
    .toUpperCase();

  return { privateKeyPath, publicKeyPath, fingerprint };
}

function readHidden(prompt) {
  const input = process.stdin;
  if (!input.isTTY || typeof input.setRawMode !== 'function')
    return Promise.reject(new Error('Run this command directly in an interactive terminal.'));

  process.stdout.write(prompt);
  input.setRawMode(true);
  input.resume();
  return new Promise((resolve, reject) => {
    const bytes = [];
    let invalidCharacter = false;
    let settled = false;

    const cleanup = () => {
      if (settled) return;
      settled = true;
      input.removeListener('data', onData);
      input.setRawMode(false);
      input.pause();
      process.stdout.write('\n');
    };

    const onData = chunk => {
      for (const byte of chunk) {
        if (byte === 3) {
          cleanup();
          reject(new Error('Key generation cancelled.'));
          return;
        }
        if (byte === 10 || byte === 13) {
          cleanup();
          if (invalidCharacter) reject(new Error('Passphrase must use printable ASCII characters.'));
          else resolve(Buffer.from(bytes).toString('ascii'));
          return;
        }
        if (byte === 8 || byte === 127) {
          bytes.pop();
          continue;
        }
        if (byte >= 32 && byte <= 126 && bytes.length < 1024) bytes.push(byte);
        else invalidCharacter = true;
      }
    };

    input.on('data', onData);
  });
}

function parseArguments(args) {
  let outputDirectory = defaultOutputDirectory();
  let showHelp = false;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--help' || args[index] === '-h') {
      showHelp = true;
    } else if (args[index] === '--output-directory' && args[index + 1]) {
      outputDirectory = args[++index];
    } else {
      throw new Error(`Unknown or incomplete option: ${args[index]}`);
    }
  }
  return { outputDirectory, showHelp };
}

async function main() {
  const { outputDirectory, showHelp } = parseArguments(process.argv.slice(2));
  if (showHelp) {
    process.stdout.write('Usage: node scripts/generate-release-signing-key.cjs [--output-directory PATH]\n');
    return;
  }

  let firstPassphrase;
  let confirmation;
  try {
    firstPassphrase = await readHidden('Set a passphrase (at least 16 printable ASCII characters): ');
    confirmation = await readHidden('Enter the same passphrase again: ');
    validatePassphrase(firstPassphrase);
    if (firstPassphrase !== confirmation) throw new Error('The passphrases did not match.');

    const result = generateSigningKeyPair({ outputDirectory, passphrase: firstPassphrase });
    process.stdout.write(`Encrypted private key: ${result.privateKeyPath}\n`);
    process.stdout.write(`Public key: ${result.publicKeyPath}\n`);
    process.stdout.write(`Public key SHA-256 fingerprint: ${result.fingerprint}\n`);
    process.stdout.write('Back up the encrypted private key offline and keep its passphrase separately. Only the public key may be shared.\n');
  } finally {
    firstPassphrase = undefined;
    confirmation = undefined;
  }
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  defaultOutputDirectory,
  generateSigningKeyPair,
  parseArguments,
  readHidden,
  resolvePathThroughExistingAncestors,
  validatePassphrase
};
