'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const http = require('node:http');
const { canonicalPayload } = require('./publish-application-release.cjs');
const { generateSigningKeyPair } = require('./generate-release-signing-key.cjs');
const API_ROOT = 'http://127.0.0.1:39176/';

async function sha256(file) {
  const hash = crypto.createHash('sha256');
  for await (const bytes of fs.createReadStream(file)) hash.update(bytes);
  return hash.digest('hex').toUpperCase();
}

async function signTestRelease(input) {
  const directory = fs.realpathSync(input.directory);
  const key = crypto.createPrivateKey({ key: fs.readFileSync(input.privateKey), passphrase: input.passphrase });
  const publicKey = crypto.createPublicKey(fs.readFileSync(input.publicKey));
  if (!crypto.createPublicKey(key).export({ type: 'spki', format: 'der' }).equals(publicKey.export({ type: 'spki', format: 'der' })))
    throw new Error('Test key pair does not match.');
  const version = input.version;
  if (!/^0\.4\.[0-9]+$/.test(version)) throw new Error('Expected a 0.4 test patch version.');
  const fileName = `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`;
  const file = path.join(directory, fileName);
  const manifest = {
    schemaVersion: 3, product: 'VeyonCampus.TeacherConsole', role: 'TeacherConsole',
    version, architecture: 'win-x64', fileName, sizeBytes: fs.statSync(file).size,
    sha256: await sha256(file), downloadUrl: new URL(`v1/releases/${crypto.randomUUID()}/artifact`, API_ROOT).href,
    policyCapabilities: { applicationPolicy: 1, studentSystemPolicy: 1 }
  };
  const envelope = {
    manifest, signatureAlgorithm: 'RSA-PSS-SHA256',
    signature: crypto.sign('sha256', canonicalPayload(manifest), {
      key, padding: crypto.constants.RSA_PKCS1_PSS_PADDING, saltLength: 32
    }).toString('base64'), publishedAt: new Date().toISOString()
  };
  fs.writeFileSync(file + '.release.json', JSON.stringify(envelope, null, 2) + '\n', { flag: 'wx' });
  fs.copyFileSync(input.publicKey, path.join(directory, 'release-public.pem'), fs.constants.COPYFILE_EXCL);
  return manifest;
}

async function loadRelease(directory) {
  const root = fs.realpathSync(directory);
  const files = fs.readdirSync(root).filter(name => /^VeyonCampus-Teacher-Setup-0\.4\.[0-9]+-win-x64\.exe\.release\.json$/.test(name));
  if (files.length !== 1) throw new Error('Test directory must contain exactly one signed target release.');
  const envelope = JSON.parse(fs.readFileSync(path.join(root, files[0]), 'utf8'));
  const m = envelope.manifest;
  if (m.role !== 'TeacherConsole' || m.product !== 'VeyonCampus.TeacherConsole' || m.architecture !== 'win-x64' ||
      m.schemaVersion !== 3 || m.fileName + '.release.json' !== files[0] ||
      envelope.signatureAlgorithm !== 'RSA-PSS-SHA256' ||
      !/^http:\/\/127\.0\.0\.1:39176\/v1\/releases\/[0-9a-f-]{36}\/artifact$/.test(m.downloadUrl) ||
      m.policyCapabilities?.applicationPolicy !== 1 || m.policyCapabilities?.studentSystemPolicy !== 1)
    throw new Error('Invalid local test manifest.');
  const file = path.join(root, m.fileName);
  if (fs.lstatSync(file).isSymbolicLink() || fs.statSync(file).size !== m.sizeBytes || await sha256(file) !== m.sha256)
    throw new Error('Installer bytes do not match the signed test manifest.');
  if (!crypto.verify('sha256', canonicalPayload(m), {
    key: fs.readFileSync(path.join(root, 'release-public.pem')),
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING, saltLength: 32
  }, Buffer.from(envelope.signature, 'base64'))) throw new Error('Invalid test release signature.');
  return { envelope, file };
}

async function createTestServer(directory) {
  const { envelope, file } = await loadRelease(directory);
  const artifactPath = new URL(envelope.manifest.downloadUrl).pathname;
  return http.createServer((request, response) => {
    if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405); response.end(); return; }
    const url = new URL(request.url, API_ROOT);
    if (url.pathname === '/v3/releases/latest' && url.searchParams.get('architecture') === 'win-x64') {
      const release = url.searchParams.get('role') === 'TeacherConsole' ? envelope : null;
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(request.method === 'HEAD' ? undefined : JSON.stringify({ release }));
    } else if (url.pathname === artifactPath && !url.search) {
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': envelope.manifest.sizeBytes });
      if (request.method === 'HEAD') { response.end(); return; }
      const stream = fs.createReadStream(file);
      stream.on('error', () => response.destroy());
      response.on('close', () => stream.destroy());
      stream.pipe(response);
    } else { response.writeHead(404); response.end(); }
  });
}

async function main() {
  if (process.argv[2] === 'serve' && process.argv.length === 4) {
    const server = await createTestServer(process.argv[3]);
    server.listen(39176, '127.0.0.1', () => console.log(`Local update test ready: ${API_ROOT} (Ctrl+C to stop)`));
    server.on('error', error => { console.error(error.code || 'SERVER_ERROR'); process.exitCode = 1; });
    return;
  }
  let body = '';
  for await (const chunk of process.stdin) { body += chunk; if (body.length > 65536) throw new Error('Input too large.'); }
  const input = JSON.parse(body);
  if (input.action === 'key') console.log(JSON.stringify(generateSigningKeyPair(input)));
  else if (input.action === 'sign') console.log(JSON.stringify(await signTestRelease(input)));
  else throw new Error('Expected key, sign, or serve action.');
}

if (require.main === module) main().catch(() => { console.error('Local update test preparation failed; check paths/key access.'); process.exitCode = 1; });
module.exports = { signTestRelease, loadRelease, createTestServer };
