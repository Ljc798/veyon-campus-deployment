'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { canonicalPayload } = require('./publish-application-release.cjs');

const API_ROOT = 'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/';
const MAX_ARTIFACT_BYTES = 512 * 1024 * 1024;
const MAX_PRIVATE_KEY_BYTES = 64 * 1024;
const MAINTENANCE_PROTOCOL_VERSION = 1;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const REPOSITORY_ROOT = path.resolve(__dirname, '..');
const PINNED_PUBLIC_KEY_PATH = path.join(REPOSITORY_ROOT, 'artifacts', 'veyon-campus-release-public.pem');

async function sha256(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const bytes of fs.createReadStream(filePath)) hash.update(bytes);
  return hash.digest('hex').toUpperCase();
}

function assertPrivateKeyOutsideRepository(privateKeyPath) {
  const realPath = fs.realpathSync.native(privateKeyPath);
  const relative = path.relative(REPOSITORY_ROOT, realPath);
  if (!relative.startsWith('..') && !path.isAbsolute(relative))
    throw new Error('Keep the Developer Release private key outside the repository.');
  const info = fs.lstatSync(privateKeyPath);
  if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1 ||
      info.size < 1 || info.size > MAX_PRIVATE_KEY_BYTES)
    throw new Error('Developer Release private key must be a small, regular, unlinked file.');
  return realPath;
}

async function signLocalMaintenanceService(input) {
  const installer = path.resolve(input.installer || '');
  let privateKeyPath = path.resolve(input.privateKey || '');
  if (typeof input.passphrase !== 'string' || input.passphrase.length === 0)
    throw new Error('A private-key passphrase is required.');
  privateKeyPath = assertPrivateKeyOutsideRepository(privateKeyPath);

  const info = fs.lstatSync(installer);
  if (!info.isFile() || info.isSymbolicLink() || info.nlink > 1 ||
      info.size < 1 || info.size > MAX_ARTIFACT_BYTES)
    throw new Error('MaintenanceService installer must be a regular file within the supported size limit.');
  const match = /^VeyonCampus-Maintenance-Setup-(.+)-win-x64\.exe$/.exec(path.basename(installer));
  if (!match || !VERSION_PATTERN.test(match[1]))
    throw new Error('Installer filename must contain a valid MaintenanceService version.');

  const privateKey = crypto.createPrivateKey({
    key: fs.readFileSync(privateKeyPath),
    passphrase: input.passphrase
  });
  const pinnedPublicKey = crypto.createPublicKey(fs.readFileSync(PINNED_PUBLIC_KEY_PATH));
  if (privateKey.asymmetricKeyType !== 'rsa' ||
      privateKey.asymmetricKeyDetails.modulusLength < 2048 ||
      privateKey.asymmetricKeyDetails.modulusLength > 4096 ||
      !crypto.createPublicKey(privateKey).export({ type: 'spki', format: 'der' })
        .equals(pinnedPublicKey.export({ type: 'spki', format: 'der' })))
    throw new Error('The local private key does not match the pinned Developer Release public key.');

  const manifest = {
    schemaVersion: 5,
    product: 'VeyonCampus.MaintenanceService',
    role: 'MaintenanceService',
    version: match[1],
    architecture: 'win-x64',
    fileName: path.basename(installer),
    sizeBytes: info.size,
    sha256: await sha256(installer),
    downloadUrl: new URL(`v1/releases/${crypto.randomUUID()}/artifact`, API_ROOT).href,
    releaseChannel: 'local-test',
    maintenanceProtocolVersion: MAINTENANCE_PROTOCOL_VERSION
  };
  const payload = canonicalPayload(manifest);
  const signature = crypto.sign('sha256', payload, {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }).toString('base64');
  if (!crypto.verify('sha256', payload, {
    key: pinnedPublicKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }, Buffer.from(signature, 'base64')))
    throw new Error('Generated Developer Release signature did not verify.');

  const envelope = {
    manifest,
    signatureAlgorithm: 'RSA-PSS-SHA256',
    signature,
    publishedAt: new Date().toISOString()
  };
  const sidecar = installer + '.release.json';
  fs.writeFileSync(sidecar, JSON.stringify(envelope, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
  return { version: manifest.version, maintenanceProtocolVersion: MAINTENANCE_PROTOCOL_VERSION,
    sizeBytes: info.size, sha256: manifest.sha256, sidecar };
}

async function main() {
  let text = '';
  for await (const chunk of process.stdin) {
    text += chunk;
    if (text.length > 65536) throw new Error('Input too large.');
  }
  const result = await signLocalMaintenanceService(JSON.parse(text));
  process.stdout.write(JSON.stringify(result) + '\n');
}

if (require.main === module)
  main().catch((error) => {
    process.stderr.write(`Local MaintenanceService signing failed: ${error.message}\n`);
    process.exitCode = 1;
  });

module.exports = { signLocalMaintenanceService };
