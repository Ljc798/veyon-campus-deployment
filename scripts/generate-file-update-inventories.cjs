#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const {
  inventoryPayload,
  strictJsonParse,
  validateManifest
} = require('./file-update-planner.cjs');

const ROOT = path.resolve(__dirname, '..');
const TEMPLATE_PATH = path.join(ROOT, 'specs/cloud-updates-and-installers/file-manifest-template.example.json');
const RUNTIME_PATH = path.join(ROOT, 'packaging/third-party/dotnet-runtime-manifest.json');
const UPDATE_HELPER_PROJECT_PATH = path.join(ROOT, 'tools/VeyonCampus.UpdateHelper/VeyonCampus.UpdateHelper.csproj');

const ROLE_DEFINITIONS = Object.freeze({
  StudentSetup: Object.freeze({
    product: 'VeyonCampus.StudentSetup',
    roleInfo: 'veyon-campus-role.json',
    components: Object.freeze([
      { id: 'StudentSetup', dependencies: [] },
      { id: 'StudentWorker', dependencies: ['StudentSetup'] },
      { id: 'StudentAgentPayload', dependencies: [] },
      { id: 'StudentAgentRuntime', dependencies: [] },
      { id: 'StudentCompanion', dependencies: ['StudentSetup'] },
      { id: 'EdgePolicyExecutor', dependencies: [] },
      { id: 'EdgeNativeHost', dependencies: [] },
      { id: 'EdgeExtension', dependencies: ['EdgeNativeHost'] },
      { id: 'StudentUpdateHelper', dependencies: [] }
    ])
  }),
  TeacherConsole: Object.freeze({
    product: 'VeyonCampus.TeacherConsole',
    roleInfo: 'veyon-campus-role.json',
    components: Object.freeze([
      { id: 'TeacherConsole', dependencies: [] },
      { id: 'TeacherWorker', dependencies: ['TeacherConsole'] },
      { id: 'TeacherUpdateHelper', dependencies: [] }
    ])
  })
});

function fail(message) {
  throw new Error(message);
}

function parseArgs(argv) {
  const allowed = new Set([
    'student-app', 'student-helper', 'teacher-app', 'teacher-helper',
    'output-directory', 'release-sequence', 'channel'
  ]);
  const result = Object.create(null);
  for (let index = 0; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const key = token.slice(2);
    if (!allowed.has(key)) fail(`Unknown argument '--${key}'.`);
    if (Object.hasOwn(result, key)) fail(`Duplicate argument '--${key}'.`);
    const value = argv[++index];
    if (value === undefined || value.startsWith('--')) fail(`Missing value for '--${key}'.`);
    result[key] = value;
  }
  for (const key of allowed) {
    if (!Object.hasOwn(result, key)) fail(`Missing required argument '--${key}'.`);
  }
  return result;
}

function readJsonFile(filePath, label, allowBom = false) {
  let bytes;
  try { bytes = fs.readFileSync(filePath); }
  catch { fail(`${label} is missing or unreadable: ${filePath}`); }
  let text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  if (allowBom && text.startsWith('\uFEFF')) text = text.slice(1);
  return strictJsonParse(text, label);
}

function resolveDirectory(value, label) {
  const resolved = path.resolve(value);
  let stat;
  try { stat = fs.lstatSync(resolved); }
  catch { fail(`${label} is missing: ${resolved}`); }
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`${label} must be a real directory: ${resolved}`);
  const real = fs.realpathSync(resolved);
  const realStat = fs.lstatSync(real);
  if (!realStat.isDirectory() || realStat.isSymbolicLink()) fail(`${label} resolves through a link: ${resolved}`);
  return real;
}

function pathsOverlap(left, right) {
  const a = path.resolve(left);
  const b = path.resolve(right);
  const relativeA = path.relative(a, b);
  const relativeB = path.relative(b, a);
  const isWithin = (relative) => relative !== '' && relative !== '..' &&
    !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
  return relativeA === '' || relativeB === '' ||
    isWithin(relativeA) || isWithin(relativeB);
}

function requireRoleInfo(appRoot, expectedRole) {
  const definition = ROLE_DEFINITIONS[expectedRole];
  const filePath = path.join(appRoot, definition.roleInfo);
  const info = readJsonFile(filePath, `${expectedRole} package role info`, true);
  if (info.schemaVersion !== 1 || info.role !== expectedRole || info.product !== definition.product ||
      typeof info.version !== 'string') {
    fail(`${expectedRole} package role info does not match the expected product and role.`);
  }
  return info;
}

function readUpdateHelperVersion() {
  let text;
  try { text = new TextDecoder('utf-8', { fatal: true }).decode(fs.readFileSync(UPDATE_HELPER_PROJECT_PATH)); }
  catch { fail('The UpdateHelper project file is missing or unreadable.'); }
  const match = /<Version>\s*([^<\s]+)\s*<\/Version>/i.exec(text);
  if (!match) fail('Could not read the UpdateHelper project version.');
  return match[1];
}

function componentMetadata(role, version, updaterVersion) {
  return ROLE_DEFINITIONS[role].components.map(({ id, dependencies }) => ({
    id,
    version: id.endsWith('UpdateHelper') ? updaterVersion : version,
    dependencies: [...dependencies]
  }));
}

function makeMetadata(template, runtime, role, version, updaterVersion, options) {
  const now = new Date(options.createdUtc);
  if (!Number.isFinite(now.getTime())) fail('createdUtc is not a valid UTC timestamp.');
  const expires = new Date(now.getTime() + 30 * 24 * 60 * 60 * 1000);
  const frameworks = [{ framework: 'Microsoft.NETCore.App', version: runtime.version, architecture: 'x64' }];
  if (role === 'TeacherConsole') {
    frameworks.push({ framework: 'Microsoft.AspNetCore.App', version: runtime.version, architecture: 'x64' });
  }
  return {
    templateVersion: 1,
    product: ROLE_DEFINITIONS[role].product,
    role,
    architecture: 'win-x64',
    version,
    channel: options.channel,
    releaseSequence: options.releaseSequence,
    createdUtc: now.toISOString().replace('.000Z', 'Z'),
    expiresUtc: expires.toISOString().replace('.000Z', 'Z'),
    minimumUpdaterVersion: updaterVersion,
    requiredFileUpdateProtocolVersion: template.requiredFileUpdateProtocolVersion,
    components: componentMetadata(role, version, updaterVersion),
    requiredRuntimes: frameworks,
    migrationCatalog: [],
    directSources: []
  };
}

function summarizeManifest(manifest) {
  const rows = [];
  for (const component of manifest.components) {
    const bytes = component.files.reduce((total, file) => total + file.sizeBytes, 0);
    rows.push({
      component: component.id,
      root: component.rootId,
      version: component.version,
      files: component.files.length,
      bytes,
      sha256: crypto.createHash('sha256').update(JSON.stringify(component.files)).digest('hex')
    });
  }
  return rows;
}

function assertStudentAgentCopiesMatch(manifest) {
  const payload = manifest.components.find((component) => component.id === 'StudentAgentPayload');
  const runtime = manifest.components.find((component) => component.id === 'StudentAgentRuntime');
  const normalizePayload = (component) => component.files.map((file) => {
    const prefix = 'WebsitePolicyAgent/';
    if (!file.path.startsWith(prefix)) fail(`StudentAgentPayload contains an unexpected path '${file.path}'.`);
    return `${file.path.slice(prefix.length)}\0${file.sizeBytes}\0${file.sha256}`;
  }).sort();
  const normalizeRuntime = (component) => component.files.map((file) =>
    `${file.path}\0${file.sizeBytes}\0${file.sha256}`).sort();
  if (JSON.stringify(normalizePayload(payload)) !== JSON.stringify(normalizeRuntime(runtime))) {
    fail('StudentAgentPayload and StudentAgentRuntime differ; the versioned runtime inventory must match the exact staged payload bytes.');
  }
}

function writeAtomically(filePath, contents) {
  const temporaryPath = `${filePath}.${crypto.randomUUID()}.tmp`;
  fs.writeFileSync(temporaryPath, contents, { flag: 'wx' });
  fs.renameSync(temporaryPath, filePath);
}

function renderReport(records, sequence, channel, createdUtc) {
  const lines = [
    '# Read-only file update inventory',
    '',
    `Generated: ${createdUtc}`,
    `Channel: ${channel}; CI sequence: ${sequence}`,
    '',
    '**Unsigned diagnostic artifact only. No source baselines, signatures, or apply operation are included. Existing full installers remain the supported distribution path.**',
    '',
    '| Role | Version | Root | Files | Bytes | Manifest SHA-256 |',
    '|---|---|---|---:|---:|---|'
  ];
  for (const record of records) {
    for (const root of record.manifest.roots) {
      const components = record.rows.filter((row) => row.root === root.id);
      const files = components.reduce((sum, row) => sum + row.files, 0);
      const bytes = components.reduce((sum, row) => sum + row.bytes, 0);
      lines.push(`| ${record.manifest.role} | ${record.manifest.version} | ${root.id} | ${files} | ${bytes} | ${record.digest} |`);
    }
  }
  lines.push('', '## Components', '');
  for (const record of records) {
    lines.push(`### ${record.manifest.role} ${record.manifest.version}`, '');
    lines.push('| Component | Version | Root | Files | Bytes | File-list SHA-256 |', '|---|---|---|---:|---:|---|');
    for (const row of record.rows) lines.push(`| ${row.component} | ${row.version} | ${row.root} | ${row.files} | ${row.bytes} | ${row.sha256} |`);
    lines.push('', 'Required runtimes:');
    for (const runtime of record.manifest.requiredRuntimes)
      lines.push(`- ${runtime.framework} ${runtime.version} (${runtime.architecture})`);
    lines.push('');
  }
  return `${lines.join('\n')}\n`;
}

function main(argv) {
  const args = parseArgs(argv);
  const studentApp = resolveDirectory(args['student-app'], 'StudentApp root');
  const studentHelper = resolveDirectory(args['student-helper'], 'StudentUpdateHelperVersioned root');
  const teacherApp = resolveDirectory(args['teacher-app'], 'TeacherApp root');
  const teacherHelper = resolveDirectory(args['teacher-helper'], 'TeacherUpdateHelperVersioned root');
  const outputDirectory = path.resolve(args['output-directory']);
  for (const inputRoot of [studentApp, studentHelper, teacherApp, teacherHelper]) {
    if (pathsOverlap(outputDirectory, inputRoot)) fail('The manifest output directory must be outside every payload root.');
  }

  const releaseSequence = Number(args['release-sequence']);
  if (!Number.isSafeInteger(releaseSequence) || releaseSequence < 1 || releaseSequence > 2147483647)
    fail('release-sequence must be a positive 32-bit integer.');
  const channel = args.channel;
  if (channel !== 'stable' && channel !== 'beta') fail('channel must be stable or beta.');

  const studentInfo = requireRoleInfo(studentApp, 'StudentSetup');
  const teacherInfo = requireRoleInfo(teacherApp, 'TeacherConsole');
  if (studentInfo.version !== teacherInfo.version) fail('Student and Teacher package versions do not match.');
  const createdUtc = new Date().toISOString();
  const template = readJsonFile(TEMPLATE_PATH, 'file manifest template');
  const runtime = readJsonFile(RUNTIME_PATH, 'pinned .NET runtime manifest');
  if (runtime.schemaVersion !== 1 || typeof runtime.version !== 'string') fail('Pinned .NET runtime manifest is invalid.');
  const updaterVersion = readUpdateHelperVersion();

  const studentMetadata = makeMetadata(template, runtime, 'StudentSetup', studentInfo.version,
    updaterVersion, { channel, releaseSequence, createdUtc });
  const teacherMetadata = makeMetadata(template, runtime, 'TeacherConsole', teacherInfo.version,
    updaterVersion, { channel, releaseSequence, createdUtc });
  const manifests = [
    {
      name: 'StudentSetup.file-manifest.json',
      manifest: inventoryPayload(studentMetadata, new Map([
        ['StudentApp', studentApp],
        ['StudentAgentVersioned', path.join(studentApp, 'WebsitePolicyAgent')],
        ['StudentUpdateHelperVersioned', studentHelper]
      ]))
    },
    {
      name: 'TeacherConsole.file-manifest.json',
      manifest: inventoryPayload(teacherMetadata, new Map([
        ['TeacherApp', teacherApp],
        ['TeacherUpdateHelperVersioned', teacherHelper]
      ]))
    }
  ];

  assertStudentAgentCopiesMatch(manifests[0].manifest);
  for (const item of manifests) {
    validateManifest(item.manifest, item.manifest.role);
    if (item.manifest.directSources.length !== 0 || item.manifest.migrationCatalog.length !== 0)
      fail('CI inventory must not invent source baselines or executable migrations.');
  }

  fs.mkdirSync(outputDirectory, { recursive: true });
  const records = manifests.map((item) => {
    const bytes = Buffer.from(`${JSON.stringify(item.manifest, null, 2)}\n`, 'utf8');
    const filePath = path.join(outputDirectory, item.name);
    writeAtomically(filePath, bytes);
    return {
      manifest: item.manifest,
      rows: summarizeManifest(item.manifest),
      digest: crypto.createHash('sha256').update(bytes).digest('hex')
    };
  });
  const report = renderReport(records, releaseSequence, channel, createdUtc);
  writeAtomically(path.join(outputDirectory, 'README.md'), report);
  if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${report}\n`);
  for (const record of records) {
    process.stdout.write(`${record.manifest.role} ${record.manifest.version}: ${record.manifest.components.length} components, ` +
      `${record.rows.reduce((sum, row) => sum + row.files, 0)} files; sha256 ${record.digest}\n`);
  }
  process.stdout.write('Unsigned inventory only; no install or publication action was performed.\n');
  return 0;
}

if (require.main === module) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`generate-file-update-inventories: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { main, makeMetadata, pathsOverlap, summarizeManifest };
