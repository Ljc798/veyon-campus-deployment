const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { licenseMetadataBlockers, packageForModuleId } = require('./check-website-bundle-licenses.cjs');

function withPackage(root, relativePath, packageJson) {
  const packageRoot = path.join(root, relativePath);
  fs.mkdirSync(packageRoot, { recursive: true });
  fs.writeFileSync(path.join(packageRoot, 'package.json'), JSON.stringify(packageJson));
  return packageRoot;
}

test('maps bundled scoped packages to the nearest installed package metadata', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-license-audit-'));
  try {
    const pkgRoot = withPackage(root, path.join('node_modules', '@cloudbase', 'sdk'), {
      name: '@cloudbase/sdk', version: '1.2.3', license: 'Apache-2.0',
    });
    const record = packageForModuleId(path.join(pkgRoot, 'dist', 'index.js'), root);
    assert.deepEqual(record, {
      name: '@cloudbase/sdk', version: '1.2.3', license: 'Apache-2.0', packageRoot: pkgRoot,
    });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('maps nested dependencies to their own version and metadata', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-license-audit-'));
  try {
    const pkgRoot = withPackage(root, path.join('node_modules', 'outer', 'node_modules', 'inner'), {
      name: 'inner', version: '4.5.6', license: 'MIT',
    });
    const record = packageForModuleId(path.join(pkgRoot, 'lib', 'index.js'), root);
    assert.equal(record.name, 'inner');
    assert.equal(record.version, '4.5.6');
    assert.equal(record.license, 'MIT');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('ignores virtual, local, and out-of-root modules', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-license-audit-'));
  try {
    assert.equal(packageForModuleId('\0virtual:module', root), null);
    assert.equal(packageForModuleId(path.join(root, 'src', 'main.js'), root), null);
    assert.equal(packageForModuleId(path.join(root, '..', 'outside', 'node_modules', 'pkg', 'index.js'), root), null);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('flags included packages with missing or non-redistributable license metadata', () => {
  const packages = [
    { name: 'known', version: '1.0.0', license: 'MIT' },
    { name: 'unknown', version: '2.0.0', license: null },
    { name: 'unlicensed', version: '3.0.0', license: 'UNLICENSED' },
    { name: 'invalid', version: '4.0.0', license: { type: 'MIT' } },
  ];
  assert.deepEqual(licenseMetadataBlockers(packages), [packages[1], packages[2], packages[3]]);
});
