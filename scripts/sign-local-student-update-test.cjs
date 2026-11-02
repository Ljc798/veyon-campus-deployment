'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { canonicalPayload } = require('./publish-application-release.cjs');

const API_ROOT = 'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/';
const MAX_ARTIFACT_BYTES = 512 * 1024 * 1024;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?$/;

async function sha256(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const bytes of fs.createReadStream(filePath)) hash.update(bytes);
  return hash.digest('hex').toUpperCase();
}

async function signLocalStudentSetup(input) {
  const installer = path.resolve(input.installer);
  const stat = fs.lstatSync(installer);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > MAX_ARTIFACT_BYTES)
    throw new Error('Installer must be a regular file within the supported size limit.');
  const version = input.version;
  const agentVersion = input.agentVersion;
  if (!VERSION_PATTERN.test(version) || !VERSION_PATTERN.test(agentVersion))
    throw new Error('StudentSetup and Student Agent versions must be valid SemVer values.');
  const fileName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
  if (path.basename(installer) !== fileName)
    throw new Error('Installer filename must match its StudentSetup version.');

  const privateKey = crypto.createPrivateKey({
    key: fs.readFileSync(input.privateKey),
    passphrase: input.passphrase
  });
  const publicKey = crypto.createPublicKey(fs.readFileSync(input.publicKey));
  if (!crypto.createPublicKey(privateKey).export({ type: 'spki', format: 'der' })
    .equals(publicKey.export({ type: 'spki', format: 'der' })))
    throw new Error('Local test signing key pair does not match.');

  const manifest = {
    schemaVersion: 4,
    product: 'VeyonCampus.StudentSetup',
    role: 'StudentSetup',
    version,
    architecture: 'win-x64',
    fileName,
    sizeBytes: stat.size,
    sha256: await sha256(installer),
    downloadUrl: new URL(`v1/releases/${crypto.randomUUID()}/artifact`, API_ROOT).href,
    policyCapabilities: { applicationPolicy: 1, studentSystemPolicy: 1 },
    studentAgentVersion: agentVersion,
    releaseChannel: 'local-test'
  };
  const envelope = {
    manifest,
    signatureAlgorithm: 'RSA-PSS-SHA256',
    signature: crypto.sign('sha256', canonicalPayload(manifest), {
      key: privateKey,
      padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
      saltLength: 32
    }).toString('base64'),
    publishedAt: new Date().toISOString()
  };
  const sidecar = installer + '.release.json';
  fs.writeFileSync(sidecar, JSON.stringify(envelope, null, 2) + '\n', { flag: 'wx' });
  return { version, agentVersion, sizeBytes: stat.size, sha256: manifest.sha256, sidecar };
}

async function main() {
  let text = '';
  for await (const chunk of process.stdin) {
    text += chunk;
    if (text.length > 65536) throw new Error('Input too large.');
  }
  const result = await signLocalStudentSetup(JSON.parse(text));
  process.stdout.write(JSON.stringify(result) + '\n');
}

if (require.main === module)
  main().catch(() => {
    console.error('Could not sign the local StudentSetup test release; check key paths and package metadata.');
    process.exitCode = 1;
  });

module.exports = { signLocalStudentSetup };
