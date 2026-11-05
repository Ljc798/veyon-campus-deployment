#!/usr/bin/env node
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const repositoryRoot = path.resolve(__dirname, '..');
const scriptPath = path.join(__dirname, 'apply-cloudbase-release-migrations.sh');
const productionEnvId = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'cloudbaserc.json'), 'utf8')).envId;
const testEnvId = 'isolated-test-env-20261103';
const expectedFiles = [
  '20261021140000_create_file_update_release_catalog.sql',
  '20261102120000_support_signed_student_agent_targets.sql'
];
const baselineMigrations = [
  ['20260930150000', 'create_application_releases_and_teacher_heartbeat'],
  ['20261001090000', 'align_package_prefix_validation'],
  ['20261001090001', 'add_shared_package_download_rate_limit'],
  ['20261001100000', 'name_deployment_packages_by_campus'],
  ['20261006100000', 'support_student_system_policy_packages'],
  ['20261006110000', 'support_release_policy_capabilities'],
  ['20261006120000', 'support_application_policy_release_capability'],
  ['20261008100000', 'support_package_setup_recommendations'],
  ['20261016120000', 'add_deployment_package_fingerprint_key_version']
];

function makeFixture(t, scenario = 'ok') {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-cloudbase-migration-test-'));
  const bin = path.join(root, 'bin');
  const trace = path.join(root, 'tcb-trace.jsonl');
  fs.mkdirSync(bin, { mode: 0o700 });

  const fakeTcb = path.join(bin, 'tcb');
  fs.writeFileSync(fakeTcb, `#!/usr/bin/env node
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const trace = process.env.FAKE_TCB_TRACE;
const statePath = process.env.FAKE_TCB_STATE;
const configIndex = args.indexOf('--config-file');
const configPath = configIndex >= 0 ? args[configIndex + 1] : '';
const envIndex = args.indexOf('-e');
const envId = envIndex >= 0 ? args[envIndex + 1] : '';
const config = configPath ? JSON.parse(fs.readFileSync(configPath, 'utf8')) : {};
const configuredDir = config.database?.migrations?.[0] || '';
const files = configuredDir && args.includes('up')
  ? fs.readdirSync(configuredDir).filter((item) => item.endsWith('.sql')).sort()
  : [];
const call = { args, envId, configPath, configuredDir, files };
fs.appendFileSync(trace, JSON.stringify(call) + '\\n');
const scenario = process.env.FAKE_TCB_SCENARIO || 'ok';
const remoteItems = (fs.existsSync(statePath)
  ? JSON.parse(fs.readFileSync(statePath, 'utf8'))
  : JSON.parse(process.env.FAKE_TCB_BASELINE).map(([version, name]) => ({ version, name })));
if (scenario === 'missing-baseline') remoteItems.shift();
if (scenario === 'disabled-migration') remoteItems.push({
  version: '20261010090000', name: 'add_daily_operations_reporting'
});
if (scenario === 'already-applied') remoteItems.push(
  { version: '20261021140000', name: 'create_file_update_release_catalog' },
  { version: '20261102120000', name: 'support_signed_student_agent_targets' }
);
if (args.includes('list') && args.includes('--remote-only')) {
  process.stdout.write(JSON.stringify({ remote: { total: remoteItems.length, items: remoteItems } }));
  process.exit(0);
}
if (args.includes('up') && args.includes('--dry-run')) {
  let pending = [
    { Version: '20261021140000', Name: 'create_file_update_release_catalog' },
    { Version: '20261102120000', Name: 'support_signed_student_agent_targets' }
  ];
  if (scenario === 'already-applied') pending = [];
  const conflicts = scenario === 'conflict' ? [{ Version: '20261010090000', Reason: 'local_migration_before_latest_remote' }] : [];
  if (scenario === 'unexpected-pending') pending = [{ Version: '20261001090000', Name: 'unexpected_migration' }];
  process.stdout.write(JSON.stringify({
    envId: scenario === 'wrong-env' ? 'different-isolated-test-env' : envId,
    dryRun: true,
    includeAll: scenario === 'include-all',
    executable: scenario !== 'not-executable',
    pending,
    applied: [],
    conflicts,
    taskId: null
  }));
  process.exit(0);
}
if (args.includes('up') && !args.includes('--dry-run')) {
  if (scenario !== 'post-missing-target') {
    fs.writeFileSync(statePath, JSON.stringify([
      { version: '20261021140000', name: 'create_file_update_release_catalog' },
      { version: '20261102120000', name: 'support_signed_student_agent_targets' }
    ]));
  }
  process.stdout.write(JSON.stringify({ envId, dryRun: false, executable: true, pending: [] }));
  process.exit(0);
}
process.stderr.write('unexpected fake tcb invocation\\n');
process.exit(9);
`, { mode: 0o700 });

  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return { root, bin, trace, statePath: path.join(root, 'remote-state.json'), scenario };
}

function runHelper(fixture, args) {
  const result = spawnSync('bash', [scriptPath, ...args], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    env: {
      ...process.env,
      PATH: `${fixture.bin}${path.delimiter}${process.env.PATH || ''}`,
      FAKE_TCB_TRACE: fixture.trace,
      FAKE_TCB_STATE: fixture.statePath,
      FAKE_TCB_BASELINE: JSON.stringify(baselineMigrations),
      FAKE_TCB_SCENARIO: fixture.scenario
    }
  });
  const calls = fs.existsSync(fixture.trace)
    ? fs.readFileSync(fixture.trace, 'utf8').trim().split('\n').filter(Boolean).map((line) => JSON.parse(line))
    : [];
  return { ...result, calls };
}

function validDryRunArgs() {
  return [
    '--env-id', testEnvId,
    '--confirm-isolated-env', testEnvId,
    '--dry-run'
  ];
}

test('migration helper dry-run confines CLI to the two allowlisted SQL files', (t) => {
  const fixture = makeFixture(t);
  const result = runHelper(fixture, validDryRunArgs());

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /只读预演结束/);
  assert.equal(result.calls.length, 2);
  assert.deepEqual(result.calls[0].files, []);
  assert.deepEqual(result.calls[1].files, expectedFiles);
  assert.equal(result.calls[0].envId, testEnvId);
  assert.equal(result.calls[1].envId, testEnvId);
  assert.ok(result.calls.every((call) => !call.args.includes('--include-all')));
  assert.ok(result.calls.every((call) => call.configPath && path.isAbsolute(call.configPath)));
  assert.ok(result.calls[1].args.includes('--dry-run'));
  assert.equal(result.calls.some((call) => call.args.includes('up') && !call.args.includes('--dry-run')), false);
});

test('migration helper apply requires exact environment and migration confirmations', (t) => {
  const fixture = makeFixture(t);
  const result = runHelper(fixture, [
    '--env-id', testEnvId,
    '--confirm-isolated-env', testEnvId,
    '--apply',
    '--confirm-migration-ids', '20261021140000,20261102120000'
  ]);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.calls.length, 4);
  assert.deepEqual(result.calls[1].files, expectedFiles);
  assert.deepEqual(result.calls[2].files, expectedFiles);
  assert.deepEqual(result.calls[3].files, []);
  assert.ok(result.calls[2].args.includes('up'));
  assert.equal(result.calls[2].args.includes('--dry-run'), false);
  assert.equal(result.calls[2].args.includes('--include-all'), false);
  assert.match(result.stdout, /读回确认/);
});

test('migration helper confirms both targets from the ledger when apply finds no pending work', (t) => {
  const fixture = makeFixture(t, 'already-applied');
  const result = runHelper(fixture, [
    '--env-id', testEnvId,
    '--confirm-isolated-env', testEnvId,
    '--apply',
    '--confirm-migration-ids', '20261021140000,20261102120000'
  ]);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.calls.length, 2);
  assert.match(result.stdout, /无需写入/);
  assert.equal(result.calls.some((call) => call.args.includes('up') && !call.args.includes('--dry-run')), false);
});

test('migration helper fails closed when post-apply ledger readback is incomplete', (t) => {
  const fixture = makeFixture(t, 'post-missing-target');
  const result = runHelper(fixture, [
    '--env-id', testEnvId,
    '--confirm-isolated-env', testEnvId,
    '--apply',
    '--confirm-migration-ids', '20261021140000,20261102120000'
  ]);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ledger 读回不完整/);
  assert.equal(result.calls.length, 4);
  assert.ok(result.calls[2].args.includes('up'));
  assert.equal(result.calls[3].args.includes('--remote-only'), true);
});

test('migration helper refuses production before contacting CloudBase', (t) => {
  const fixture = makeFixture(t);
  const result = runHelper(fixture, [
    '--env-id', productionEnvId,
    '--confirm-isolated-env', productionEnvId,
    '--dry-run'
  ]);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /production/);
  assert.equal(result.calls.length, 0);
});

test('migration helper refuses missing confirmation and incomplete migration IDs before contacting CloudBase', (t) => {
  const noEnvConfirmation = makeFixture(t);
  const noEnvResult = runHelper(noEnvConfirmation, [
    '--env-id', testEnvId,
    '--dry-run'
  ]);
  assert.notEqual(noEnvResult.status, 0);
  assert.equal(noEnvResult.calls.length, 0);

  const wrongIds = makeFixture(t);
  const wrongIdsResult = runHelper(wrongIds, [
    '--env-id', testEnvId,
    '--confirm-isolated-env', testEnvId,
    '--apply',
    '--confirm-migration-ids', '20261102120000'
  ]);
  assert.notEqual(wrongIdsResult.status, 0);
  assert.equal(wrongIdsResult.calls.length, 0);
});

for (const scenario of ['missing-baseline', 'disabled-migration']) {
  test(`migration helper stops when remote baseline is unsafe (${scenario})`, (t) => {
    const fixture = makeFixture(t, scenario);
    const result = runHelper(fixture, validDryRunArgs());

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /基线不符合预期/);
    assert.equal(result.calls.length, 1);
    assert.ok(result.calls[0].args.includes('list'));
    assert.equal(result.calls.some((call) => call.args.includes('up')), false);
  });
}

for (const scenario of ['unexpected-pending', 'conflict', 'not-executable', 'wrong-env', 'include-all']) {
  test(`migration helper stops on unsafe preflight results (${scenario})`, (t) => {
    const fixture = makeFixture(t, scenario);
    const result = runHelper(fixture, [
      '--env-id', testEnvId,
      '--confirm-isolated-env', testEnvId,
      '--apply',
      '--confirm-migration-ids', '20261021140000,20261102120000'
    ]);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /预演结果超出 allowlist/);
    assert.equal(result.calls.length, 2);
    assert.equal(result.calls.some((call) => call.args.includes('up') && !call.args.includes('--dry-run')), false);
  });
}
