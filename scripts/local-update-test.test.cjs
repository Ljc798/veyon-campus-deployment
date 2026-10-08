'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { signTestRelease, loadRelease, createTestServer } = require('./local-update-test.cjs');

test('signed local update serves only latest/installer and rejects changed bytes', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-local-update-fixture-'));
  let server;
  try {
    const key = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    const passphrase = 'ephemeral fixture password';
    const privateKey = path.join(directory, 'fixture-private.pem'), publicKey = path.join(directory, 'fixture-public.pem');
    fs.writeFileSync(privateKey, key.privateKey.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase }));
    fs.writeFileSync(publicKey, key.publicKey.export({ type: 'spki', format: 'pem' }));
    const artifact = path.join(directory, 'VeyonCampus-Teacher-Setup-0.4.56-win-x64.exe');
    fs.writeFileSync(artifact, Buffer.from('fixture executable bytes'));
    const manifest = await signTestRelease({ directory, privateKey, publicKey, passphrase, version: '0.4.56' });
    await loadRelease(directory);
    await assert.rejects(() => signTestRelease({ directory, privateKey, publicKey, passphrase, version: '0.4.56' }));
    server = await createTestServer(directory);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = 'http://127.0.0.1:' + server.address().port;
    const latest = await (await fetch(base + '/v3/releases/latest?role=TeacherConsole&architecture=win-x64')).json();
    assert.equal(latest.release.manifest.sha256, manifest.sha256);
    assert.equal((await (await fetch(base + '/v3/releases/latest?role=StudentSetup&architecture=win-x64')).json()).release, null);
    assert.equal((await fetch(base + '/fixture-private.pem')).status, 404);
    assert.equal((await fetch(base + '/v3/releases/latest', { method: 'POST' })).status, 405);
    const download = await fetch(base + new URL(manifest.downloadUrl).pathname);
    assert.equal(Buffer.from(await download.arrayBuffer()).toString(), 'fixture executable bytes');
    fs.appendFileSync(artifact, '!');
    await assert.rejects(() => loadRelease(directory));
  } finally {
    if (server) await new Promise(resolve => server.close(resolve));
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
