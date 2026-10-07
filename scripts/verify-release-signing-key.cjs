#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { readHidden, validatePassphrase } = require('./generate-release-signing-key.cjs');

const REPOSITORY_ROOT = fs.realpathSync(path.resolve(__dirname, '..'));

function isInside(parentPath, childPath) {
  const relative = path.relative(parentPath, childPath);
  return relative === '' || (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative));
}

function parseArguments(args) {
  let privateKeyPath;
  let publicKeyPath;
  let showHelp = false;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--help' || args[index] === '-h') {
      showHelp = true;
    } else if (args[index] === '--private-key' && args[index + 1]) {
      privateKeyPath = args[++index];
    } else if (args[index] === '--public-key' && args[index + 1]) {
      publicKeyPath = args[++index];
    } else {
      throw new Error(`Unknown or incomplete option: ${args[index]}`);
    }
  }
  if (!showHelp && (!privateKeyPath || !publicKeyPath))
    throw new Error('Both --private-key and --public-key are required.');
  return { privateKeyPath, publicKeyPath, showHelp };
}

function resolvePemFile(filePath, label, repositoryRoot = REPOSITORY_ROOT) {
  if (typeof filePath !== 'string' || filePath.trim().length === 0)
    throw new Error(`${label} path is required.`);
  const absolutePath = path.resolve(filePath);
  const info = fs.lstatSync(absolutePath);
  if (!info.isFile() || info.isSymbolicLink())
    throw new Error(`${label} must be a regular, non-symlink file.`);
  const realPath = fs.realpathSync(absolutePath);
  if (label === 'Private key' && isInside(fs.realpathSync(repositoryRoot), realPath))
    throw new Error('Private signing keys must remain outside the repository.');
  return realPath;
}

function verifySigningKeyPair({ privateKeyPem, publicKeyPem, passphrase }) {
  validatePassphrase(passphrase);
  if (typeof privateKeyPem !== 'string' || !privateKeyPem.includes('-----BEGIN ENCRYPTED PRIVATE KEY-----'))
    throw new Error('Private key must be passphrase-encrypted PKCS#8 PEM.');
  if (typeof publicKeyPem !== 'string' || publicKeyPem.includes('PRIVATE KEY'))
    throw new Error('Public key file must contain only a public key.');

  const privateKey = crypto.createPrivateKey({ key: privateKeyPem, format: 'pem', passphrase });
  const publicKey = crypto.createPublicKey(publicKeyPem);
  if (privateKey.asymmetricKeyType !== 'rsa' || publicKey.asymmetricKeyType !== 'rsa')
    throw new Error('Release signing key must use RSA.');
  if (privateKey.asymmetricKeyDetails.modulusLength !== 3072)
    throw new Error('Release signing key must be RSA-3072.');

  const privatePublicKey = crypto.createPublicKey(privateKey);
  const publicKeyDer = publicKey.export({ type: 'spki', format: 'der' });
  if (!publicKeyDer.equals(privatePublicKey.export({ type: 'spki', format: 'der' })))
    throw new Error('The private and public release keys do not match.');

  const challenge = crypto.randomBytes(32);
  const signature = crypto.sign('sha256', challenge, {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  });
  if (!crypto.verify('sha256', challenge, {
    key: publicKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }, signature))
    throw new Error('Release key pair failed its RSA-PSS challenge.');

  return {
    modulusLength: privateKey.asymmetricKeyDetails.modulusLength,
    fingerprint: crypto.createHash('sha256').update(publicKeyDer).digest('hex').toUpperCase()
  };
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  if (options.showHelp) {
    process.stdout.write('Usage: node scripts/verify-release-signing-key.cjs --private-key PATH --public-key PATH\n');
    return;
  }

  const privateKeyPath = resolvePemFile(options.privateKeyPath, 'Private key');
  const publicKeyPath = resolvePemFile(options.publicKeyPath, 'Public key');
  let passphrase;
  try {
    passphrase = await readHidden('Enter the release-key passphrase (input is hidden): ');
    const result = verifySigningKeyPair({
      privateKeyPem: fs.readFileSync(privateKeyPath, 'utf8'),
      publicKeyPem: fs.readFileSync(publicKeyPath, 'utf8'),
      passphrase
    });
    process.stdout.write(`Release key pair verified: RSA-${result.modulusLength}\n`);
    process.stdout.write(`Public key SHA-256 fingerprint: ${result.fingerprint}\n`);
  } finally {
    passphrase = undefined;
  }
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = { parseArguments, resolvePemFile, verifySigningKeyPair };
