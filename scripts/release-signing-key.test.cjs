#!/usr/bin/env node
'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  generateSigningKeyPair,
  resolvePathThroughExistingAncestors,
  validatePassphrase
} = require('./generate-release-signing-key.cjs');
const {
  resolvePemFile,
  verifySigningKeyPair
} = require('./verify-release-signing-key.cjs');

function makeTempDirectory(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-release-key-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  return directory;
}

function testPassphrase() {
  return `test-only-${crypto.randomBytes(24).toString('base64url')}`;
}

test('release signing passphrases must be sufficiently long printable ASCII', () => {
  assert.doesNotThrow(() => validatePassphrase('1234567890123456'));
  assert.throws(() => validatePassphrase('short'), /16–1024/);
  assert.throws(() => validatePassphrase('123456789012345é'), /printable ASCII/);
  assert.throws(() => validatePassphrase('x'.repeat(1025)), /16–1024/);
});

test('generated keys are encrypted RSA-3072, match, and pass an RSA-PSS challenge', t => {
  const temp = makeTempDirectory(t);
  const repositoryRoot = path.join(temp, 'repo');
  const outputDirectory = path.join(temp, 'outside-repo', 'Production');
  fs.mkdirSync(repositoryRoot);
  const passphrase = testPassphrase();

  const generated = generateSigningKeyPair({ outputDirectory, passphrase, repositoryRoot });
  const privateKeyPem = fs.readFileSync(generated.privateKeyPath, 'utf8');
  const publicKeyPem = fs.readFileSync(generated.publicKeyPath, 'utf8');

  assert.match(privateKeyPem, /-----BEGIN ENCRYPTED PRIVATE KEY-----/);
  assert.doesNotMatch(publicKeyPem, /PRIVATE KEY/);
  const verified = verifySigningKeyPair({ privateKeyPem, publicKeyPem, passphrase });
  assert.equal(verified.modulusLength, 3072);
  assert.equal(verified.fingerprint, generated.fingerprint);

  const unrelated = crypto.generateKeyPairSync('rsa', {
    modulusLength: 2048,
    publicKeyEncoding: { type: 'spki', format: 'pem' },
    privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
  });
  assert.throws(() => verifySigningKeyPair({
    privateKeyPem,
    publicKeyPem: unrelated.publicKey,
    passphrase
  }), /do not match/);
  assert.throws(() => verifySigningKeyPair({
    privateKeyPem,
    publicKeyPem,
    passphrase: testPassphrase()
  }));

  if (process.platform !== 'win32') {
    assert.equal(fs.statSync(generated.privateKeyPath).mode & 0o777, 0o600);
    assert.equal(fs.statSync(generated.publicKeyPath).mode & 0o777, 0o644);
    assert.equal(fs.statSync(outputDirectory).mode & 0o777, 0o700);
  }
});

test('key generation rejects repository paths and never overwrites existing keys', t => {
  const temp = makeTempDirectory(t);
  const repositoryRoot = path.join(temp, 'repo');
  const outputDirectory = path.join(temp, 'keys');
  fs.mkdirSync(repositoryRoot);
  const passphrase = testPassphrase();

  assert.throws(() => generateSigningKeyPair({
    outputDirectory: path.join(repositoryRoot, 'keys'),
    passphrase,
    repositoryRoot
  }), /outside the repository/);
  assert.equal(fs.existsSync(path.join(repositoryRoot, 'keys')), false);

  const repositoryAlias = path.join(temp, 'repository-alias');
  fs.symlinkSync(repositoryRoot, repositoryAlias, 'dir');
  assert.throws(() => generateSigningKeyPair({
    outputDirectory: path.join(repositoryAlias, 'keys'),
    passphrase,
    repositoryRoot
  }), /outside the repository/);
  assert.equal(fs.existsSync(path.join(repositoryRoot, 'keys')), false);
  assert.equal(resolvePathThroughExistingAncestors(path.join(repositoryAlias, 'new', 'keys')),
    path.join(fs.realpathSync(repositoryRoot), 'new', 'keys'));

  const generated = generateSigningKeyPair({ outputDirectory, passphrase, repositoryRoot });
  const before = {
    privateKey: fs.readFileSync(generated.privateKeyPath, 'utf8'),
    publicKey: fs.readFileSync(generated.publicKeyPath, 'utf8')
  };
  assert.throws(() => generateSigningKeyPair({ outputDirectory, passphrase, repositoryRoot }), /never overwritten/);
  assert.equal(fs.readFileSync(generated.privateKeyPath, 'utf8'), before.privateKey);
  assert.equal(fs.readFileSync(generated.publicKeyPath, 'utf8'), before.publicKey);
});

test('offline verifier accepts only regular key files outside the repository', t => {
  const temp = makeTempDirectory(t);
  const repositoryRoot = path.join(temp, 'repo');
  const outputDirectory = path.join(temp, 'keys');
  fs.mkdirSync(repositoryRoot);
  const generated = generateSigningKeyPair({ outputDirectory, passphrase: testPassphrase(), repositoryRoot });

  assert.equal(resolvePemFile(generated.privateKeyPath, 'Private key', repositoryRoot), generated.privateKeyPath);
  const inRepositoryPath = path.join(repositoryRoot, 'private.pem');
  fs.writeFileSync(inRepositoryPath, 'test');
  assert.throws(() => resolvePemFile(inRepositoryPath, 'Private key', repositoryRoot), /outside the repository/);

  const symlinkPath = path.join(temp, 'private-link.pem');
  fs.symlinkSync(generated.privateKeyPath, symlinkPath);
  assert.throws(() => resolvePemFile(symlinkPath, 'Private key', repositoryRoot), /regular, non-symlink/);
});
