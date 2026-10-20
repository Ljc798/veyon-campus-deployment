#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { constants: cryptoConstants } = require('node:crypto');

const MAX_MANIFEST_BYTES = 16 * 1024 * 1024;
const MAX_FILES = 50_000;
const MAX_FILE_BYTES = 2 * 1024 * 1024 * 1024;
const SIGNATURE_DOMAIN = Buffer.from('VeyonCampus.FileManifest.v1\0', 'utf8');
const HASH_PATTERN = /^[a-f0-9]{64}$/;
const SEMVER_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

const ROLE_LAYOUTS = Object.freeze({
  TeacherConsole: Object.freeze({
    product: 'VeyonCampus.TeacherConsole',
    roots: Object.freeze({
      TeacherApp: Object.freeze({ location: 'ProgramFiles/Veyon Campus/Teacher', activation: 'transactional-root' }),
      TeacherUpdateHelperVersioned: Object.freeze({ location: 'ProgramFiles/Veyon Campus/Updater/Teacher/{version}', activation: 'versioned-root' })
    }),
    components: Object.freeze({
      TeacherConsole: 'TeacherApp',
      TeacherWorker: 'TeacherApp',
      TeacherUpdateHelper: 'TeacherUpdateHelperVersioned'
    }),
    requiredComponents: Object.freeze(['TeacherConsole', 'TeacherWorker', 'TeacherUpdateHelper']),
    classify(rootId, relativePath) {
      if (rootId === 'TeacherUpdateHelperVersioned') return 'TeacherUpdateHelper';
      return relativePath.toLowerCase().startsWith('worker/') ? 'TeacherWorker' : 'TeacherConsole';
    }
  }),
  StudentSetup: Object.freeze({
    product: 'VeyonCampus.StudentSetup',
    roots: Object.freeze({
      StudentApp: Object.freeze({ location: 'ProgramFiles/Veyon Campus/Student', activation: 'transactional-root' }),
      StudentAgentVersioned: Object.freeze({ location: 'ProgramData/VeyonCampus/WebsitePolicyAgent/{agentVersion}', activation: 'versioned-root' }),
      StudentUpdateHelperVersioned: Object.freeze({ location: 'ProgramFiles/Veyon Campus/Updater/Student/{version}', activation: 'versioned-root' })
    }),
    components: Object.freeze({
      StudentSetup: 'StudentApp',
      StudentWorker: 'StudentApp',
      StudentAgentPayload: 'StudentApp',
      StudentAgentRuntime: 'StudentAgentVersioned',
      StudentCompanion: 'StudentApp',
      EdgePolicyExecutor: 'StudentApp',
      EdgeNativeHost: 'StudentApp',
      EdgeExtension: 'StudentApp',
      StudentUpdateHelper: 'StudentUpdateHelperVersioned'
    }),
    requiredComponents: Object.freeze([
      'StudentSetup', 'StudentWorker', 'StudentAgentPayload', 'StudentAgentRuntime',
      'StudentCompanion', 'EdgePolicyExecutor', 'EdgeNativeHost', 'EdgeExtension', 'StudentUpdateHelper'
    ]),
    classify(rootId, relativePath) {
      if (rootId === 'StudentAgentVersioned') return 'StudentAgentRuntime';
      if (rootId === 'StudentUpdateHelperVersioned') return 'StudentUpdateHelper';
      const lower = relativePath.toLowerCase();
      if (lower.startsWith('worker/')) return 'StudentWorker';
      if (lower.startsWith('websitepolicyagent/')) return 'StudentAgentPayload';
      if (lower.startsWith('studentcompanion/')) return 'StudentCompanion';
      if (lower.startsWith('edgepolicyexecutor/')) return 'EdgePolicyExecutor';
      if (lower.startsWith('edgenativehost/')) return 'EdgeNativeHost';
      if (lower.startsWith('edgeextension/')) return 'EdgeExtension';
      return 'StudentSetup';
    }
  })
});

const MANIFEST_KEYS = new Set([
  'schemaVersion', 'product', 'role', 'architecture', 'version', 'channel', 'releaseSequence',
  'createdUtc', 'expiresUtc', 'minimumUpdaterVersion', 'requiredFileUpdateProtocolVersion',
  'roots', 'components', 'requiredRuntimes', 'migrationCatalog', 'directSources'
]);

function fail(message) {
  throw new Error(message);
}

function strictJsonParse(input, label = 'JSON input', maxBytes = MAX_MANIFEST_BYTES) {
  const bytes = Buffer.isBuffer(input) ? input : Buffer.from(input);
  if (bytes.length === 0 || bytes.length > maxBytes) fail(`${label} byte length is invalid.`);
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) fail(`${label} must not contain a UTF-8 BOM.`);
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    fail(`${label} is not valid UTF-8.`);
  }

  let index = 0;
  const whitespace = () => {
    while (index < text.length && /[\u0009\u000a\u000d\u0020]/.test(text[index])) index++;
  };
  const parseString = () => {
    const start = index;
    if (text[index] !== '"') fail(`${label} contains an invalid JSON string.`);
    index++;
    while (index < text.length) {
      const ch = text[index++];
      if (ch === '"') {
        try { return JSON.parse(text.slice(start, index)); }
        catch { fail(`${label} contains an invalid JSON string.`); }
      }
      if (ch === '\\') {
        if (index >= text.length) fail(`${label} ends inside an escape sequence.`);
        const escaped = text[index++];
        if (escaped === 'u') {
          const hex = text.slice(index, index + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) fail(`${label} contains an invalid Unicode escape.`);
          index += 4;
        } else if (!'"\\/bfnrt'.includes(escaped)) {
          fail(`${label} contains an invalid escape sequence.`);
        }
      } else if (ch.charCodeAt(0) < 0x20) {
        fail(`${label} contains an unescaped control character.`);
      }
    }
    fail(`${label} ends inside a JSON string.`);
  };
  const parseValue = (depth = 0) => {
    if (depth > 64) fail(`${label} nesting is too deep.`);
    whitespace();
    const ch = text[index];
    if (ch === '"') return parseString();
    if (ch === '{') {
      index++;
      whitespace();
      const value = Object.create(null);
      const keys = new Set();
      if (text[index] === '}') { index++; return value; }
      while (index < text.length) {
        whitespace();
        const key = parseString();
        if (keys.has(key)) fail(`${label} contains duplicate property '${key}'.`);
        keys.add(key);
        whitespace();
        if (text[index++] !== ':') fail(`${label} is missing a colon after a property name.`);
        value[key] = parseValue(depth + 1);
        whitespace();
        const separator = text[index++];
        if (separator === '}') return value;
        if (separator !== ',') fail(`${label} contains an invalid object separator.`);
      }
      fail(`${label} ends inside an object.`);
    }
    if (ch === '[') {
      index++;
      whitespace();
      const value = [];
      if (text[index] === ']') { index++; return value; }
      while (index < text.length) {
        value.push(parseValue(depth + 1));
        whitespace();
        const separator = text[index++];
        if (separator === ']') return value;
        if (separator !== ',') fail(`${label} contains an invalid array separator.`);
      }
      fail(`${label} ends inside an array.`);
    }
    const remaining = text.slice(index);
    const number = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(remaining);
    if (number) {
      index += number[0].length;
      const parsed = Number(number[0]);
      if (!Number.isFinite(parsed)) fail(`${label} contains an out-of-range number.`);
      return parsed;
    }
    for (const [literal, value] of [['true', true], ['false', false], ['null', null]]) {
      if (remaining.startsWith(literal)) { index += literal.length; return value; }
    }
    fail(`${label} contains an invalid value at byte offset ${Buffer.byteLength(text.slice(0, index), 'utf8')}.`);
  };

  const result = parseValue();
  whitespace();
  if (index !== text.length) fail(`${label} contains trailing data.`);
  return result;
}

function isSemver(value) {
  return typeof value === 'string' && value.length <= 128 && SEMVER_PATTERN.test(value);
}

function compareSemver(left, right) {
  const a = SEMVER_PATTERN.exec(left);
  const b = SEMVER_PATTERN.exec(right);
  if (!a || !b) fail('Cannot compare invalid semantic versions.');
  for (let i = 1; i <= 3; i++) {
    const x = BigInt(a[i]);
    const y = BigInt(b[i]);
    if (x < y) return -1;
    if (x > y) return 1;
  }
  const ap = a[4] ? a[4].split('.') : [];
  const bp = b[4] ? b[4].split('.') : [];
  if (!ap.length || !bp.length) return ap.length === bp.length ? 0 : (ap.length ? -1 : 1);
  for (let i = 0; i < Math.min(ap.length, bp.length); i++) {
    const an = /^(0|[1-9][0-9]*)$/.test(ap[i]);
    const bn = /^(0|[1-9][0-9]*)$/.test(bp[i]);
    if (an && bn) {
      const x = BigInt(ap[i]);
      const y = BigInt(bp[i]);
      if (x < y) return -1;
      if (x > y) return 1;
    } else if (an !== bn) {
      return an ? -1 : 1;
    } else if (ap[i] !== bp[i]) {
      return ap[i] < bp[i] ? -1 : 1;
    }
  }
  return ap.length === bp.length ? 0 : (ap.length < bp.length ? -1 : 1);
}

function normalizedPathKey(rootId, relativePath) {
  return `${rootId}\0${relativePath.normalize('NFC').toUpperCase()}`;
}

function validateRelativePath(value, label) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 1024 || value !== value.normalize('NFC'))
    fail(`${label} is not a canonical NFC relative path.`);
  if (value.startsWith('/') || value.includes('\\') || value.includes(':') || /[\u0000-\u001f\u007f<>"|?*]/.test(value))
    fail(`${label} contains an absolute-path marker, Windows-invalid character or control character.`);
  const segments = value.split('/');
  if (segments.some((part) => !part || part.length > 255 || part === '.' || part === '..' || /[ .]$/.test(part)))
    fail(`${label} contains an empty, traversal or trailing-dot/space path segment.`);
  if (segments.some((part) => {
    const base = part.replace(/[ .]+$/g, '').split('.')[0].replace(/[ .]+$/g, '');
    return /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(base);
  }))
    fail(`${label} contains a reserved Windows device name.`);
  return value;
}

function requireObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be a JSON object.`);
}

function exactKeys(value, allowed, label) {
  for (const key of Object.keys(value)) if (!allowed.has(key)) fail(`${label} contains unknown property '${key}'.`);
}

function required(value, key, label) {
  if (!Object.hasOwn(value, key)) fail(`${label} is missing '${key}'.`);
  return value[key];
}

function validateTimestamp(value, label) {
  const match = typeof value === 'string' && value.length <= 32 && /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?Z$/.exec(value);
  if (!match) fail(`${label} must be a valid UTC Z timestamp.`);
  const [, yearText, monthText, dayText, hourText, minuteText, secondText] = match;
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const hour = Number(hourText);
  const minute = Number(minuteText);
  const second = Number(secondText);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (month < 1 || month > 12 || date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day ||
      hour > 23 || minute > 59 || second > 59 || !Number.isFinite(Date.parse(value)))
    fail(`${label} must be a valid UTC Z timestamp.`);
}

function validateHash(value, label) {
  if (typeof value !== 'string' || !HASH_PATTERN.test(value)) fail(`${label} must be a lowercase SHA-256 digest.`);
}

function validateManifest(manifest, label = 'manifest') {
  requireObject(manifest, label);
  exactKeys(manifest, MANIFEST_KEYS, label);
  const requiredFields = [...MANIFEST_KEYS];
  for (const key of requiredFields) required(manifest, key, label);
  if (manifest.schemaVersion !== 1) fail(`${label} schemaVersion is unsupported.`);
  if (!['TeacherConsole', 'StudentSetup'].includes(manifest.role)) fail(`${label} role is unsupported.`);
  const layout = ROLE_LAYOUTS[manifest.role];
  if (manifest.product !== layout.product || manifest.architecture !== 'win-x64') fail(`${label} product or architecture does not match its role.`);
  if (!isSemver(manifest.version) || !isSemver(manifest.minimumUpdaterVersion)) fail(`${label} contains an invalid semantic version.`);
  if (!['stable', 'beta'].includes(manifest.channel)) fail(`${label} channel is unsupported.`);
  if (manifest.channel === 'stable' && SEMVER_PATTERN.exec(manifest.version)[4]) fail(`${label} stable version must not contain a prerelease identifier.`);
  if (!Number.isSafeInteger(manifest.releaseSequence) || manifest.releaseSequence < 1 || manifest.releaseSequence > 2147483647)
    fail(`${label} releaseSequence is invalid.`);
  validateTimestamp(manifest.createdUtc, `${label}.createdUtc`);
  validateTimestamp(manifest.expiresUtc, `${label}.expiresUtc`);
  if (Date.parse(manifest.expiresUtc) <= Date.parse(manifest.createdUtc)) fail(`${label} expiresUtc must follow createdUtc.`);
  if (!Number.isSafeInteger(manifest.requiredFileUpdateProtocolVersion) || manifest.requiredFileUpdateProtocolVersion < 1 || manifest.requiredFileUpdateProtocolVersion > 32767)
    fail(`${label} requiredFileUpdateProtocolVersion is invalid.`);

  if (!Array.isArray(manifest.roots) || manifest.roots.length !== Object.keys(layout.roots).length)
    fail(`${label} must describe every fixed root for its role exactly once.`);
  const roots = new Set();
  for (const [index, root] of manifest.roots.entries()) {
    requireObject(root, `${label}.roots[${index}]`);
    exactKeys(root, new Set(['id', 'location', 'activation']), `${label}.roots[${index}]`);
    const definition = layout.roots[root.id];
    if (!definition || roots.has(root.id) || root.location !== definition.location || root.activation !== definition.activation)
      fail(`${label}.roots[${index}] does not match a unique fixed role root.`);
    roots.add(root.id);
  }

  if (!Array.isArray(manifest.components) || manifest.components.length !== layout.requiredComponents.length)
    fail(`${label} must describe every required component for its role exactly once.`);
  const componentIds = new Set();
  const componentDependencies = new Map();
  const fileMap = new Map();
  let totalFiles = 0;
  let totalBytes = 0;
  for (const [componentIndex, component] of manifest.components.entries()) {
    requireObject(component, `${label}.components[${componentIndex}]`);
    exactKeys(component, new Set(['id', 'rootId', 'version', 'dependencies', 'files']), `${label}.components[${componentIndex}]`);
    if (typeof component.id !== 'string' || componentIds.has(component.id) || !Object.hasOwn(layout.components, component.id))
      fail(`${label}.components[${componentIndex}] has a duplicate or unsupported component ID.`);
    if (component.rootId !== layout.components[component.id] || !roots.has(component.rootId))
      fail(`${label}.components[${componentIndex}] uses an invalid component root.`);
    if (!isSemver(component.version)) fail(`${label}.components[${componentIndex}] has an invalid version.`);
    if (!Array.isArray(component.dependencies) || component.dependencies.some((dependency) => typeof dependency !== 'string' || dependency.length > 64))
      fail(`${label}.components[${componentIndex}] dependencies are invalid.`);
    if (new Set(component.dependencies).size !== component.dependencies.length || component.dependencies.includes(component.id))
      fail(`${label}.components[${componentIndex}] has duplicate or self dependencies.`);
    if (!Array.isArray(component.files) || component.files.length === 0) fail(`${label}.components[${componentIndex}] must contain files.`);
    componentIds.add(component.id);
    componentDependencies.set(component.id, component.dependencies);
    for (const [fileIndex, file] of component.files.entries()) {
      requireObject(file, `${label}.components[${componentIndex}].files[${fileIndex}]`);
      exactKeys(file, new Set(['path', 'sizeBytes', 'sha256']), `${label}.components[${componentIndex}].files[${fileIndex}]`);
      const relativePath = validateRelativePath(file.path, `${label} file path`);
      if (!Number.isSafeInteger(file.sizeBytes) || file.sizeBytes < 0 || file.sizeBytes > MAX_FILE_BYTES)
        fail(`${label} contains an invalid file size.`);
      validateHash(file.sha256, `${label} file hash`);
      const key = normalizedPathKey(component.rootId, relativePath);
      if (fileMap.has(key)) fail(`${label} contains a duplicate or case-insensitive path collision at '${relativePath}'.`);
      fileMap.set(key, { rootId: component.rootId, path: relativePath, componentId: component.id, sizeBytes: file.sizeBytes, sha256: file.sha256 });
      totalFiles++;
      totalBytes += file.sizeBytes;
      if (totalFiles > MAX_FILES || !Number.isSafeInteger(totalBytes)) fail(`${label} exceeds the supported aggregate file inventory.`);
    }
  }
  if (layout.requiredComponents.some((componentId) => !componentIds.has(componentId))) fail(`${label} is missing a required component.`);
  for (const component of manifest.components) {
    for (const dependency of component.dependencies) if (!componentIds.has(dependency)) fail(`${label} component '${component.id}' depends on unknown component '${dependency}'.`);
  }
  const dependencyState = new Map();
  const visitDependency = (componentId) => {
    const state = dependencyState.get(componentId) ?? 0;
    if (state === 1) fail(`${label} component dependencies contain a cycle.`);
    if (state === 2) return;
    dependencyState.set(componentId, 1);
    for (const dependency of componentDependencies.get(componentId) ?? []) visitDependency(dependency);
    dependencyState.set(componentId, 2);
  };
  for (const componentId of componentIds) visitDependency(componentId);

  if (!Array.isArray(manifest.requiredRuntimes) || manifest.requiredRuntimes.length > 8) fail(`${label}.requiredRuntimes is invalid.`);
  const runtimeKeys = new Set();
  for (const runtime of manifest.requiredRuntimes) {
    requireObject(runtime, `${label} runtime`);
    exactKeys(runtime, new Set(['framework', 'version', 'architecture']), `${label} runtime`);
    if (!['Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'].includes(runtime.framework) ||
        typeof runtime.version !== 'string' || runtime.version.length > 64 || runtime.architecture !== 'x64')
      fail(`${label} contains an unsupported runtime requirement.`);
    const runtimeKey = `${runtime.framework}|${runtime.version}|${runtime.architecture}`;
    if (runtimeKeys.has(runtimeKey)) fail(`${label} contains duplicate runtime requirements.`);
    runtimeKeys.add(runtimeKey);
  }

  if (!Array.isArray(manifest.migrationCatalog) || manifest.migrationCatalog.length > 128) fail(`${label}.migrationCatalog is invalid.`);
  const migrationIds = new Set();
  for (const migration of manifest.migrationCatalog) {
    requireObject(migration, `${label} migration`);
    exactKeys(migration, new Set(['id', 'runnerCapability', 'reversible', 'requiresReboot', 'roots']), `${label} migration`);
    if (typeof migration.id !== 'string' || !/^[a-z0-9][a-z0-9._-]{0,95}$/.test(migration.id) || migrationIds.has(migration.id) ||
        !Number.isSafeInteger(migration.runnerCapability) || migration.runnerCapability < 1 || migration.runnerCapability > 32767 ||
        typeof migration.reversible !== 'boolean' || typeof migration.requiresReboot !== 'boolean' || !Array.isArray(migration.roots) ||
        migration.roots.length < 1 || migration.roots.some((rootId) => !roots.has(rootId)))
      fail(`${label} contains an invalid or duplicate migration.`);
    migrationIds.add(migration.id);
  }

  if (!Array.isArray(manifest.directSources) || manifest.directSources.length > 256) fail(`${label}.directSources is invalid.`);
  const directSourceVersions = new Set();
  for (const directSource of manifest.directSources) {
    requireObject(directSource, `${label} direct source`);
    exactKeys(directSource, new Set(['version', 'manifestSha256', 'requiredMigrations', 'managedDeletes']), `${label} direct source`);
    if (!isSemver(directSource.version) || directSource.version === manifest.version || directSourceVersions.has(directSource.version))
      fail(`${label} contains an invalid or duplicate direct source version.`);
    validateHash(directSource.manifestSha256, `${label} direct source digest`);
    directSourceVersions.add(directSource.version);
    if (!Array.isArray(directSource.requiredMigrations) || new Set(directSource.requiredMigrations).size !== directSource.requiredMigrations.length ||
        directSource.requiredMigrations.some((id) => !migrationIds.has(id)))
      fail(`${label} direct source references an unknown or duplicate migration.`);
    if (!Array.isArray(directSource.managedDeletes) || directSource.managedDeletes.length > MAX_FILES)
      fail(`${label} direct source managedDeletes is invalid.`);
    const deletedPaths = new Set();
    for (const deletion of directSource.managedDeletes) {
      requireObject(deletion, `${label} managed delete`);
      exactKeys(deletion, new Set(['rootId', 'path', 'previousSha256']), `${label} managed delete`);
      if (!roots.has(deletion.rootId)) fail(`${label} managed delete uses an unknown root.`);
      const deletionPath = validateRelativePath(deletion.path, `${label} managed delete path`);
      validateHash(deletion.previousSha256, `${label} managed delete hash`);
      const key = normalizedPathKey(deletion.rootId, deletionPath);
      if (deletedPaths.has(key) || fileMap.has(key)) fail(`${label} managed delete duplicates a path or deletes a target file.`);
      deletedPaths.add(key);
    }
  }
  return { layout, fileMap, totalFiles, totalBytes, migrationIds };
}

function parseRootArguments(values, label = 'root') {
  const roots = new Map();
  for (const value of values) {
    const separator = value.indexOf('=');
    if (separator <= 0 || separator === value.length - 1) fail(`Invalid --${label} value; expected ROOT_ID=PATH.`);
    const id = value.slice(0, separator);
    const rootPath = path.resolve(value.slice(separator + 1));
    if (roots.has(id)) fail(`Duplicate root mapping '${id}'.`);
    roots.set(id, rootPath);
  }
  return roots;
}

function assertDirectoryRoot(rootId, rootPath) {
  let stat;
  try { stat = fs.lstatSync(rootPath); }
  catch { fail(`Root '${rootId}' is missing or unreadable.`); }
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`Root '${rootId}' must be a real directory, not a link.`);
  const real = fs.realpathSync(rootPath);
  const realStat = fs.lstatSync(real);
  if (!realStat.isDirectory() || realStat.isSymbolicLink()) fail(`Root '${rootId}' resolves through a link.`);
  return real;
}

function sameFileIdentity(left, right) {
  return left.isFile() && right.isFile() && left.size === right.size && left.dev === right.dev && left.ino === right.ino &&
    left.mtimeMs === right.mtimeMs;
}

function hashFileSync(filePath, expectedStat) {
  const hash = crypto.createHash('sha256');
  let fd;
  try {
    fd = fs.openSync(filePath, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
    const openedStat = fs.fstatSync(fd);
    if (!sameFileIdentity(expectedStat, openedStat)) fail('File identity changed while opening a package input.');
    const buffer = Buffer.allocUnsafe(1024 * 1024);
    while (true) {
      const bytesRead = fs.readSync(fd, buffer, 0, buffer.length, null);
      if (bytesRead === 0) break;
      hash.update(buffer.subarray(0, bytesRead));
    }
    if (!sameFileIdentity(openedStat, fs.fstatSync(fd))) fail('File changed while it was being hashed.');
    return hash.digest('hex');
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

function scanRoot(rootId, rootPath) {
  const realRoot = assertDirectoryRoot(rootId, rootPath);
  const files = [];
  const directories = [];
  const entryKeys = new Map();
  const walk = (directory, prefix = '') => {
    const entries = fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
    for (const entry of entries) {
      const fullPath = path.join(directory, entry.name);
      const relativePath = prefix ? `${prefix}/${entry.name}` : entry.name;
      validateRelativePath(relativePath, `Root '${rootId}' path`);
      const key = normalizedPathKey(rootId, relativePath);
      if (entryKeys.has(key)) fail(`Root '${rootId}' contains a case-insensitive path collision at '${relativePath}'.`);
      const stat = fs.lstatSync(fullPath);
      if (stat.isSymbolicLink()) fail(`Root '${rootId}' contains a symbolic link or junction at '${relativePath}'.`);
      if (stat.isDirectory()) {
        entryKeys.set(key, 'directory');
        directories.push(relativePath);
        walk(fullPath, relativePath);
      } else if (stat.isFile()) {
        entryKeys.set(key, 'file');
        if (stat.nlink > 1) fail(`Root '${rootId}' contains a hard-linked file at '${relativePath}'.`);
        if (stat.size > MAX_FILE_BYTES) fail(`Root '${rootId}' contains an oversized file at '${relativePath}'.`);
        const actual = fs.statSync(fullPath);
        if (!actual.isFile() || actual.size !== stat.size) fail(`Root '${rootId}' changed while it was being scanned.`);
        const sha256 = hashFileSync(fullPath, stat);
        const after = fs.statSync(fullPath);
        if (!sameFileIdentity(stat, after))
          fail(`Root '${rootId}' changed while it was being scanned.`);
        files.push({ rootId, path: relativePath, sizeBytes: stat.size, sha256 });
        if (files.length > MAX_FILES) fail(`Root '${rootId}' contains too many files.`);
      } else {
        fail(`Root '${rootId}' contains a non-file entry at '${relativePath}'.`);
      }
    }
  };
  walk(realRoot);
  return { files, directories };
}

function inventoryPayload(metadata, rootMappings) {
  requireObject(metadata, 'inventory metadata');
  const allowedTemplateKeys = new Set([
    'templateVersion', 'product', 'role', 'architecture', 'version', 'channel', 'releaseSequence',
    'createdUtc', 'expiresUtc', 'minimumUpdaterVersion', 'requiredFileUpdateProtocolVersion',
    'components', 'requiredRuntimes', 'migrationCatalog', 'directSources'
  ]);
  exactKeys(metadata, allowedTemplateKeys, 'inventory metadata');
  if (metadata.templateVersion !== 1) fail('Inventory templateVersion must be 1.');
  const layout = ROLE_LAYOUTS[metadata.role];
  if (!layout || metadata.product !== layout.product || metadata.architecture !== 'win-x64')
    fail('Inventory metadata product, role or architecture is invalid.');
  const expectedRootIds = Object.keys(layout.roots).sort();
  if (rootMappings.size !== expectedRootIds.length || expectedRootIds.some((id) => !rootMappings.has(id)))
    fail(`Inventory requires exactly these roots: ${expectedRootIds.join(', ')}.`);
  if (!Array.isArray(metadata.components) || metadata.components.length !== layout.requiredComponents.length)
    fail('Inventory metadata must describe each required component exactly once.');

  const componentDefinitions = new Map();
  for (const definition of metadata.components) {
    requireObject(definition, 'inventory component definition');
    exactKeys(definition, new Set(['id', 'version', 'dependencies']), 'inventory component definition');
    if (typeof definition.id !== 'string' || !Object.hasOwn(layout.components, definition.id) || componentDefinitions.has(definition.id) ||
        !isSemver(definition.version) || !Array.isArray(definition.dependencies))
      fail('Inventory metadata contains an invalid or duplicate component definition.');
    componentDefinitions.set(definition.id, { id: definition.id, rootId: layout.components[definition.id], version: definition.version, dependencies: definition.dependencies });
  }
  if (layout.requiredComponents.some((id) => !componentDefinitions.has(id))) fail('Inventory metadata is missing a required component.');

  const filesByComponent = new Map(layout.requiredComponents.map((id) => [id, []]));
  const roots = [];
  for (const rootId of expectedRootIds) {
    const rootDef = layout.roots[rootId];
    roots.push({ id: rootId, location: rootDef.location, activation: rootDef.activation });
    for (const file of scanRoot(rootId, rootMappings.get(rootId)).files) {
      const componentId = layout.classify(rootId, file.path);
      filesByComponent.get(componentId).push({ path: file.path, sizeBytes: file.sizeBytes, sha256: file.sha256 });
    }
  }
  const components = layout.requiredComponents.map((id) => {
    const definition = componentDefinitions.get(id);
    const files = filesByComponent.get(id).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    if (files.length === 0) fail(`Inventory root contains no files for required component '${id}'.`);
    return { id, rootId: definition.rootId, version: definition.version, dependencies: definition.dependencies.slice().sort(), files };
  }).sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0);

  const manifest = {
    schemaVersion: 1,
    product: metadata.product,
    role: metadata.role,
    architecture: metadata.architecture,
    version: metadata.version,
    channel: metadata.channel,
    releaseSequence: metadata.releaseSequence,
    createdUtc: metadata.createdUtc,
    expiresUtc: metadata.expiresUtc,
    minimumUpdaterVersion: metadata.minimumUpdaterVersion,
    requiredFileUpdateProtocolVersion: metadata.requiredFileUpdateProtocolVersion,
    roots,
    components,
    migrationCatalog: metadata.migrationCatalog.slice().sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
      .map((migration) => ({ ...migration, roots: migration.roots.slice().sort() })),
    directSources: metadata.directSources.slice().sort((a, b) => compareSemver(a.version, b.version) || (a.version < b.version ? -1 : a.version > b.version ? 1 : 0))
      .map((source) => ({ ...source,
        requiredMigrations: source.requiredMigrations.slice().sort(),
        managedDeletes: source.managedDeletes.slice().sort((a, b) => {
          const left = `${a.rootId}\0${a.path}`;
          const right = `${b.rootId}\0${b.path}`;
          return left < right ? -1 : left > right ? 1 : 0;
        })
      })),
    requiredRuntimes: metadata.requiredRuntimes.slice().sort((a, b) => {
      const left = `${a.framework}\0${a.version}\0${a.architecture}`;
      const right = `${b.framework}\0${b.version}\0${b.architecture}`;
      return left < right ? -1 : left > right ? 1 : 0;
    })
  };
  validateManifest(manifest, 'generated manifest');
  return manifest;
}

function verifyManifestSignature(manifestBytes, signatureBytes, publicKeyBytes) {
  const envelope = strictJsonParse(signatureBytes, 'detached signature', 64 * 1024);
  requireObject(envelope, 'detached signature');
  exactKeys(envelope, new Set(['schemaVersion', 'algorithm', 'keyId', 'signature']), 'detached signature');
  if (envelope.schemaVersion !== 1 || envelope.algorithm !== 'RSA-PSS-SHA256' ||
      typeof envelope.keyId !== 'string' || !HASH_PATTERN.test(envelope.keyId) || typeof envelope.signature !== 'string')
    fail('Detached signature metadata is invalid.');
  const publicKeyText = Buffer.isBuffer(publicKeyBytes) ? publicKeyBytes.toString('utf8') : String(publicKeyBytes);
  if (/PRIVATE KEY/i.test(publicKeyText)) fail('A public key is required; private keys are not accepted.');
  const key = crypto.createPublicKey(publicKeyText);
  if (key.asymmetricKeyType !== 'rsa' || (key.asymmetricKeyDetails?.modulusLength ?? 0) < 3072)
    fail('The pinned key must be RSA-3072 or stronger.');
  const keyId = crypto.createHash('sha256').update(key.export({ type: 'spki', format: 'der' })).digest('hex');
  if (keyId !== envelope.keyId) fail('Detached signature keyId does not match the pinned public key.');
  const signature = Buffer.from(envelope.signature, 'base64');
  if (signature.toString('base64') !== envelope.signature) fail('Detached signature Base64 is not canonical.');
  const payload = Buffer.concat([SIGNATURE_DOMAIN, Buffer.isBuffer(manifestBytes) ? manifestBytes : Buffer.from(manifestBytes)]);
  const ok = crypto.verify('sha256', payload,
    { key, padding: cryptoConstants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, signature);
  if (!ok) fail('Detached manifest signature verification failed.');
  return { verified: true, keyId };
}

function readManifest(pathName, label) {
  const bytes = readBoundedFile(pathName, label, MAX_MANIFEST_BYTES);
  const manifest = strictJsonParse(bytes, label);
  validateManifest(manifest, label);
  return { bytes, manifest, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
}

function readBoundedFile(pathName, label, maxBytes) {
  let before;
  try { before = fs.lstatSync(pathName); }
  catch { fail(`${label} is missing or unreadable.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.size < 1 || before.size > maxBytes)
    fail(`${label} must be a regular file no larger than ${maxBytes} bytes.`);
  let bytes;
  try { bytes = fs.readFileSync(pathName); }
  catch { fail(`${label} could not be read.`); }
  let after;
  try { after = fs.lstatSync(pathName); }
  catch { fail(`${label} changed while it was being read.`); }
  if (!sameFileIdentity(before, after) || bytes.length !== before.size || bytes.length > maxBytes)
    fail(`${label} changed while it was being read.`);
  return bytes;
}

function makeTrust(args, sourceBytes, targetBytes) {
  const signatureArgs = [args['source-signature'], args['target-signature']];
  const keyArgs = [args['developer-public-key'], args['source-public-key'], args['target-public-key']];
  const anySignatureArg = signatureArgs.some(Boolean);
  const anyKeyArg = keyArgs.some(Boolean);
  if (anySignatureArg && signatureArgs.some((value) => !value)) fail('Provide both --source-signature and --target-signature.');
  if (anySignatureArg && !anyKeyArg) fail('Signed planning requires a pinned developer public key.');
  if (!anySignatureArg && anyKeyArg) fail('Public-key options require both detached signatures.');
  if (args['developer-public-key'] && (args['source-public-key'] || args['target-public-key']))
    fail('Use either --developer-public-key or the source/target public-key pair, not both.');
  if (anySignatureArg) {
    let sourcePublicKey;
    let targetPublicKey;
    if (args['developer-public-key']) {
      sourcePublicKey = readBoundedFile(args['developer-public-key'], 'developer public key', 64 * 1024);
      targetPublicKey = sourcePublicKey;
    } else {
      if (!args['source-public-key'] || !args['target-public-key'])
        fail('Provide both --source-public-key and --target-public-key for key rotation.');
      sourcePublicKey = readBoundedFile(args['source-public-key'], 'source public key', 64 * 1024);
      targetPublicKey = readBoundedFile(args['target-public-key'], 'target public key', 64 * 1024);
    }
    const sourceSignature = readBoundedFile(args['source-signature'], 'source signature', 64 * 1024);
    const targetSignature = readBoundedFile(args['target-signature'], 'target signature', 64 * 1024);
    const sourceTrust = verifyManifestSignature(sourceBytes, sourceSignature, sourcePublicKey);
    const targetTrust = verifyManifestSignature(targetBytes, targetSignature, targetPublicKey);
    return { source: 'verified', target: 'verified', keyId: targetTrust.keyId, sourceKeyId: sourceTrust.keyId };
  }
  if (!args['allow-unsigned-development']) fail('Unsigned manifests are accepted only with --allow-unsigned-development for read-only diagnostics.');
  return { source: 'unverified-development', target: 'unverified-development', keyId: null, sourceKeyId: null };
}

function createPlan(sourceRecord, targetRecord, rootMappings, options, trust) {
  const source = sourceRecord.manifest;
  const target = targetRecord.manifest;
  const sourceValidated = validateManifest(source, 'source manifest');
  const targetValidated = validateManifest(target, 'target manifest');
  const problems = [];
  const targetProblems = [];
  const now = options.now ? new Date(options.now) : new Date();
  const pushSourceProblem = (code, detail, rootId = null, filePath = null) => problems.push({ code, detail, rootId, path: filePath });
  const pushTargetProblem = (code, detail) => targetProblems.push({ code, detail });

  if (source.role !== target.role || source.product !== target.product || source.architecture !== target.architecture || source.channel !== target.channel)
    pushSourceProblem('identity-mismatch', 'Source and target role, product, architecture and channel must match.');
  if (compareSemver(target.version, source.version) <= 0 || target.releaseSequence <= source.releaseSequence)
    pushSourceProblem('downgrade-or-replay', 'Target version and release sequence must both advance.');
  if (!isSemver(options.updaterVersion) || compareSemver(options.updaterVersion, target.minimumUpdaterVersion) < 0)
    pushSourceProblem('updater-too-old', 'Installed updater version is missing or below the target minimum.');
  if (!Number.isSafeInteger(options.fileUpdateProtocol) || options.fileUpdateProtocol < target.requiredFileUpdateProtocolVersion)
    pushSourceProblem('file-update-capability-missing', 'Installed file-update protocol capability is missing or too old.');
  if (!Number.isSafeInteger(options.migrationRunnerCapability) || options.migrationRunnerCapability < 0)
    pushSourceProblem('migration-runner-unknown', 'Migration runner capability is invalid.');
  if (Date.parse(target.createdUtc) > now.getTime() + 5 * 60 * 1000 || Date.parse(target.expiresUtc) <= now.getTime())
    pushTargetProblem('manifest-expired-or-not-yet-valid', 'Target manifest is expired or created too far in the future.');

  const directSource = target.directSources.find((entry) => entry.version === source.version);
  if (!directSource) {
    pushSourceProblem('source-not-supported', 'Target manifest has no direct-update entry for this exact source version.');
  } else if (directSource.manifestSha256 !== sourceRecord.sha256) {
    pushSourceProblem('source-manifest-digest-mismatch', 'Source manifest bytes do not match the target’s direct-source digest.');
  }
  for (const [key, targetFile] of targetValidated.fileMap) {
    const sourceFile = sourceValidated.fileMap.get(key);
    if (sourceFile && sourceFile.path !== targetFile.path)
      pushSourceProblem('path-case-change', 'A case-only file path rename is not supported by the read-only planner.', targetFile.rootId, targetFile.path);
  }

  const expectedRootIds = Object.keys(sourceValidated.layout.roots).sort();
  if (rootMappings.size !== expectedRootIds.length || expectedRootIds.some((id) => !rootMappings.has(id))) {
    pushSourceProblem('root-mapping-incomplete', `Read-only scan requires every fixed source root: ${expectedRootIds.join(', ')}.`);
  } else {
    for (const rootId of expectedRootIds) {
      try {
        const scan = scanRoot(rootId, rootMappings.get(rootId));
        const actualFiles = scan.files;
        const expectedFiles = [...sourceValidated.fileMap.values()].filter((file) => file.rootId === rootId);
        const expectedByKey = new Map(expectedFiles.map((file) => [normalizedPathKey(rootId, file.path), file]));
        const actualByKey = new Map(actualFiles.map((file) => [normalizedPathKey(rootId, file.path), file]));
        for (const [key, expected] of expectedByKey) {
          const actual = actualByKey.get(key);
          if (!actual) pushSourceProblem('source-file-missing', 'A file recorded by the source manifest is missing.', rootId, expected.path);
          else if (actual.sha256 !== expected.sha256 || actual.sizeBytes !== expected.sizeBytes)
            pushSourceProblem('source-file-mismatch', 'A source file does not match its trusted manifest hash and size.', rootId, expected.path);
        }
        for (const [key, actual] of actualByKey) {
          if (!expectedByKey.has(key)) pushSourceProblem('unknown-source-file', 'An unowned file exists inside a managed root.', rootId, actual.path);
        }
        const expectedDirectories = new Set();
        for (const expected of expectedFiles) {
          const segments = expected.path.split('/');
          for (let index = 1; index < segments.length; index++) {
            expectedDirectories.add(normalizedPathKey(rootId, segments.slice(0, index).join('/')));
          }
        }
        for (const directory of scan.directories) {
          if (!expectedDirectories.has(normalizedPathKey(rootId, directory)))
            pushSourceProblem('unknown-source-directory', 'An unowned directory exists inside a managed root.', rootId, directory);
        }
      } catch (error) {
        pushSourceProblem('source-root-unreadable', error.message, rootId);
      }
    }
  }

  const actions = [];
  const migrations = [];
  if (directSource) {
    for (const id of directSource.requiredMigrations) {
      const migration = target.migrationCatalog.find((entry) => entry.id === id);
      if (!migration) {
        pushSourceProblem('migration-unknown', `Required migration '${id}' is not in the target catalog.`);
        continue;
      }
      migrations.push({ id: migration.id, runnerCapability: migration.runnerCapability, reversible: migration.reversible,
        requiresReboot: migration.requiresReboot, roots: migration.roots });
      if (!migration.reversible || options.migrationRunnerCapability < migration.runnerCapability)
        pushSourceProblem('migration-not-safely-supported', `Migration '${id}' is irreversible or exceeds the known runner capability.`);
    }
  }

  if (directSource && targetProblems.length === 0) {
    for (const migration of migrations) {
      actions.push({ action: 'migration-required', rootId: null, componentId: null, sourceComponentId: null,
        path: null, sourceSha256: null, targetSha256: null, sizeBytes: 0,
        migrationId: migration.id, reason: 'Migration metadata is reported for review; this tool never runs it.' });
    }
  }

  if (problems.length === 0 && targetProblems.length === 0 && directSource) {
    const sourceFiles = sourceValidated.fileMap;
    const targetFiles = targetValidated.fileMap;
    const deleteRules = new Map(directSource.managedDeletes.map((rule) => [normalizedPathKey(rule.rootId, rule.path), rule]));
    for (const [key, targetFile] of targetFiles) {
      const sourceFile = sourceFiles.get(key);
      const action = !sourceFile ? 'add' : sourceFile.sha256 === targetFile.sha256 && sourceFile.sizeBytes === targetFile.sizeBytes ? 'unchanged' : 'download';
      actions.push({
        action,
        rootId: targetFile.rootId,
        componentId: targetFile.componentId,
        sourceComponentId: sourceFile?.componentId ?? null,
        path: targetFile.path,
        sourceSha256: sourceFile?.sha256 ?? null,
        targetSha256: targetFile.sha256,
        sizeBytes: targetFile.sizeBytes,
        reason: action === 'unchanged' ? 'Source and target bytes match.' : action === 'add' ? 'Target file is not in the source manifest.' : 'Target hash or size differs from the source.'
      });
    }
    for (const [key, sourceFile] of sourceFiles) {
      if (targetFiles.has(key)) continue;
      const deletion = deleteRules.get(key);
      if (!deletion || deletion.previousSha256 !== sourceFile.sha256) {
        pushSourceProblem('delete-not-authorized', 'A source-owned file is absent from the target but lacks an exact hash-bound delete rule.', sourceFile.rootId, sourceFile.path);
        continue;
      }
      actions.push({
        action: 'managed-delete', rootId: sourceFile.rootId, componentId: sourceFile.componentId,
        sourceComponentId: sourceFile.componentId, path: sourceFile.path,
        sourceSha256: sourceFile.sha256, targetSha256: null, sizeBytes: sourceFile.sizeBytes,
        reason: 'Target direct-source entry authorizes deletion of this exact prior file hash.'
      });
    }
  }

  actions.sort((a, b) => {
    const ka = `${a.rootId ?? ''}\0${a.path ?? ''}\0${a.action}`;
    const kb = `${b.rootId ?? ''}\0${b.path ?? ''}\0${b.action}`;
    return ka < kb ? -1 : ka > kb ? 1 : 0;
  });
  const status = targetProblems.length ? 'invalid-target' : problems.length ? 'unsupported-source' : 'planned';
  const sourceFileCount = sourceValidated.totalFiles;
  const targetFileCount = targetValidated.totalFiles;
  return {
    schemaVersion: 1,
    kind: 'VeyonCampus.FileUpdatePlan',
    readOnly: true,
    deployable: false,
    status,
    fallback: status === 'invalid-target' ? 'refetch-valid-target-manifest' : 'complete-signed-installer',
    trust: { source: trust.source, target: trust.target, sourceKeyId: trust.sourceKeyId ?? null, targetKeyId: trust.keyId },
    source: { role: source.role, version: source.version, releaseSequence: source.releaseSequence,
      manifestSha256: sourceRecord.sha256, fileCount: sourceFileCount, bytes: sourceValidated.totalBytes },
    target: { role: target.role, version: target.version, releaseSequence: target.releaseSequence,
      manifestSha256: targetRecord.sha256, fileCount: targetFileCount, bytes: targetValidated.totalBytes,
      requiredFileUpdateProtocolVersion: target.requiredFileUpdateProtocolVersion },
    actions,
    problems: [...problems, ...targetProblems.map((problem) => ({ ...problem, rootId: null, path: null }))],
    requiredMigrations: migrations
  };
}

function parseArgs(argv) {
  const [command, ...tokens] = argv;
  const args = Object.create(null);
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const equal = token.indexOf('=');
    const key = token.slice(2, equal < 0 ? undefined : equal);
    let value = equal >= 0 ? token.slice(equal + 1) : null;
    if (value === null) {
      if (key === 'allow-unsigned-development') { value = true; }
      else {
        if (i + 1 >= tokens.length || tokens[i + 1].startsWith('--')) fail(`Option --${key} requires a value.`);
        value = tokens[++i];
      }
    }
    if (key === 'allow-unsigned-development' && value !== true) fail('--allow-unsigned-development does not take a value.');
    if (key === 'root') (args.root ??= []).push(value);
    else if (Object.hasOwn(args, key)) fail(`Option --${key} was supplied more than once.`);
    else args[key] = value;
  }
  return { command, args };
}

function formatHelp() {
  return [
    'Read-only Veyon Campus file manifest inventory and planner.',
    '',
    'Inventory a built payload (writes JSON only to stdout):',
    '  node scripts/file-update-planner.cjs inventory --metadata FILE --root ROOT_ID=PATH [--root ROOT_ID=PATH ...]',
    '',
    'Plan against a source installation (never applies actions):',
    '  node scripts/file-update-planner.cjs plan --source-manifest FILE --target-manifest FILE',
    '    --root ROOT_ID=PATH [--root ROOT_ID=PATH ...] --updater-version VERSION --file-update-protocol N',
    '    [--migration-runner-capability N] [--source-signature FILE --target-signature FILE --developer-public-key FILE]',
    '    [or --source-public-key FILE --target-public-key FILE for a trusted key rotation]',
    '    [--allow-unsigned-development]',
    '',
    'Unsigned manifests require the explicit development flag and always produce deployable=false.'
  ].join('\n');
}

function main(argv) {
  if (!argv.length || argv.includes('--help') || argv.includes('-h')) {
    process.stdout.write(formatHelp() + '\n');
    return 0;
  }
  const { command, args } = parseArgs(argv);
  if (command === 'inventory') {
    exactKeys(args, new Set(['metadata', 'root']), 'inventory arguments');
    const metadataPath = required(args, 'metadata', 'inventory arguments');
    if (args.root === undefined) fail('inventory requires at least one --root ROOT_ID=PATH.');
    const metadata = strictJsonParse(readBoundedFile(metadataPath, 'inventory metadata', MAX_MANIFEST_BYTES), 'inventory metadata');
    const roots = parseRootArguments(args.root);
    const manifest = inventoryPayload(metadata, roots);
    process.stdout.write(JSON.stringify(manifest, null, 2) + '\n');
    return 0;
  }
  if (command === 'plan') {
    exactKeys(args, new Set([
      'source-manifest', 'target-manifest', 'root', 'updater-version', 'file-update-protocol',
      'migration-runner-capability', 'source-signature', 'target-signature', 'developer-public-key',
      'source-public-key', 'target-public-key', 'allow-unsigned-development'
    ]), 'plan arguments');
    const sourceRecord = readManifest(required(args, 'source-manifest', 'plan arguments'), 'source manifest');
    const targetRecord = readManifest(required(args, 'target-manifest', 'plan arguments'), 'target manifest');
    const roots = parseRootArguments(args.root ?? []);
    const trust = makeTrust(args, sourceRecord.bytes, targetRecord.bytes);
    const migrationRunnerCapability = args['migration-runner-capability'] === undefined ? 0 : Number(args['migration-runner-capability']);
    const plan = createPlan(sourceRecord, targetRecord, roots, {
      updaterVersion: args['updater-version'],
      fileUpdateProtocol: Number(args['file-update-protocol']),
      migrationRunnerCapability
    }, trust);
    process.stdout.write(JSON.stringify(plan, null, 2) + '\n');
    return plan.status === 'planned' ? 0 : 2;
  }
  fail(`Unknown command '${command}'. Use --help.`);
}

if (require.main === module) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`file-update-planner: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  compareSemver,
  createPlan,
  inventoryPayload,
  normalizedPathKey,
  scanRoot,
  strictJsonParse,
  validateManifest,
  validateRelativePath,
  verifyManifestSignature,
  ROLE_LAYOUTS,
  MAX_FILES,
  MAX_MANIFEST_BYTES
};
