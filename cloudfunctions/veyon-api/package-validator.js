'use strict';

const crypto = require('node:crypto');
const net = require('node:net');
const zlib = require('node:zlib');
const { TextDecoder } = require('node:util');

const MAX_ARCHIVE_BYTES = 64 * 1024;
const MAX_FILE_BYTES = 16 * 1024;
const DEFAULT_HEARTBEAT_ENDPOINT =
  'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/v1/heartbeat';

class InvalidPackageError extends Error {}

function utf8(bytes, label) {
  try {
    let start = 0;
    if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) start = 3;
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(start));
  } catch {
    throw new InvalidPackageError(label + ' 不是有效的 UTF-8 文本。');
  }
}

function assertUniqueJsonKeys(text, label) {
  let index = 0;
  let depth = 0;
  const whitespace = () => {
    while (index < text.length && (text[index] === ' ' || text[index] === '\t' ||
      text[index] === '\r' || text[index] === '\n')) index++;
  };
  const fail = () => { throw new InvalidPackageError(label + ' 格式无效或含有重复 JSON 字段。'); };
  const readString = () => {
    if (text[index] !== '"') fail();
    const start = index++;
    while (index < text.length) {
      const code = text.charCodeAt(index);
      if (code < 0x20) fail();
      if (text[index] === '\\') {
        index += 2;
        continue;
      }
      if (text[index++] === '"') {
        try {
          return JSON.parse(text.slice(start, index));
        } catch {
          fail();
        }
      }
    }
    fail();
  };
  const readValue = () => {
    whitespace();
    if (++depth > 40) fail();
    const char = text[index];
    if (char === '{') {
      index++;
      whitespace();
      const keys = new Set();
      if (text[index] === '}') {
        index++;
      } else {
        while (true) {
          whitespace();
          const key = readString();
          if (keys.has(key)) fail();
          keys.add(key);
          whitespace();
          if (text[index++] !== ':') fail();
          readValue();
          whitespace();
          const separator = text[index++];
          if (separator === '}') break;
          if (separator !== ',') fail();
        }
      }
    } else if (char === '[') {
      index++;
      whitespace();
      if (text[index] === ']') {
        index++;
      } else {
        while (true) {
          readValue();
          whitespace();
          const separator = text[index++];
          if (separator === ']') break;
          if (separator !== ',') fail();
        }
      }
    } else if (char === '"') {
      readString();
    } else {
      const rest = text.slice(index);
      const literal = /^(?:true|false|null)/.exec(rest);
      const number = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(rest);
      if (literal) index += literal[0].length;
      else if (number) index += number[0].length;
      else fail();
    }
    depth--;
  };

  readValue();
  whitespace();
  if (index !== text.length) fail();
}

function parseJson(bytes, label) {
  const text = utf8(bytes, label);
  assertUniqueJsonKeys(text, label);
  try {
    return JSON.parse(text);
  } catch {
    throw new InvalidPackageError(label + ' JSON 格式无效。');
  }
}

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function decodeZipName(bytes, flags) {
  try {
    if (flags & 0x0800) return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    if (bytes.some((byte) => byte > 0x7f))
      throw new InvalidPackageError('ZIP 文件名未声明 UTF-8 编码。');
    return bytes.toString('ascii');
  } catch (error) {
    if (error instanceof InvalidPackageError) throw error;
    throw new InvalidPackageError('ZIP 文件名编码无效。');
  }
}

function readZipEntries(archive) {
  if (!Buffer.isBuffer(archive) || archive.length < 22 || archive.length > MAX_ARCHIVE_BYTES)
    throw new InvalidPackageError('ZIP 文件大小必须在 1 字节至 64 KiB 之间。');

  let eocd = -1;
  const earliest = Math.max(0, archive.length - 22 - 0xffff);
  for (let position = archive.length - 22; position >= earliest; position--) {
    if (archive.readUInt32LE(position) === 0x06054b50) {
      eocd = position;
      break;
    }
  }
  if (eocd < 0) throw new InvalidPackageError('ZIP 目录结构无效。');
  const disk = archive.readUInt16LE(eocd + 4);
  const directoryDisk = archive.readUInt16LE(eocd + 6);
  const diskEntries = archive.readUInt16LE(eocd + 8);
  const totalEntries = archive.readUInt16LE(eocd + 10);
  const directoryBytes = archive.readUInt32LE(eocd + 12);
  const directoryOffset = archive.readUInt32LE(eocd + 16);
  const commentBytes = archive.readUInt16LE(eocd + 20);
  if (disk || directoryDisk || diskEntries !== totalEntries || totalEntries < 3 ||
      totalEntries > 8 || eocd + 22 + commentBytes !== archive.length ||
      directoryOffset + directoryBytes > eocd)
    throw new InvalidPackageError('ZIP 文件数量或目录结构不符合配置包格式。');

  const entries = [];
  let cursor = directoryOffset;
  for (let count = 0; count < totalEntries; count++) {
    if (cursor + 46 > directoryOffset + directoryBytes ||
        archive.readUInt32LE(cursor) !== 0x02014b50)
      throw new InvalidPackageError('ZIP 中央目录无效。');

    const flags = archive.readUInt16LE(cursor + 8);
    const method = archive.readUInt16LE(cursor + 10);
    const expectedCrc = archive.readUInt32LE(cursor + 16);
    const compressedBytes = archive.readUInt32LE(cursor + 20);
    const unpackedBytes = archive.readUInt32LE(cursor + 24);
    const nameBytes = archive.readUInt16LE(cursor + 28);
    const extraBytes = archive.readUInt16LE(cursor + 30);
    const commentLength = archive.readUInt16LE(cursor + 32);
    const startDisk = archive.readUInt16LE(cursor + 34);
    const externalAttributes = archive.readUInt32LE(cursor + 38);
    const localOffset = archive.readUInt32LE(cursor + 42);
    const recordEnd = cursor + 46 + nameBytes + extraBytes + commentLength;
    if (recordEnd > directoryOffset + directoryBytes || startDisk !== 0 ||
        (method !== 0 && method !== 8) || (flags & 0x0001) !== 0 ||
        (flags & ~(0x0808 | (method === 8 ? 0x0006 : 0))) !== 0 ||
        unpackedBytes < 1 || unpackedBytes > MAX_FILE_BYTES ||
        compressedBytes > MAX_ARCHIVE_BYTES || localOffset >= directoryOffset)
      throw new InvalidPackageError('ZIP 含有不支持的压缩、加密、属性或超限文件。');
    const unixMode = (externalAttributes >>> 16) & 0xf000;
    const windowsAttributes = (externalAttributes >>> 16) & 0xffff;
    if (unixMode === 0xa000 || (windowsAttributes & 0x0400) !== 0)
      throw new InvalidPackageError('ZIP 不能包含链接或重解析点。');

    const rawName = archive.subarray(cursor + 46, cursor + 46 + nameBytes);
    const name = decodeZipName(rawName, flags);
    if (!name || name === '.' || name === '..' || name.includes('/') ||
        name.includes('\\') || name.includes(':') || /[\u0000-\u001f\u007f]/.test(name))
      throw new InvalidPackageError('ZIP 含有目录、路径穿越或无效文件名。');

    if (localOffset + 30 > directoryOffset ||
        archive.readUInt32LE(localOffset) !== 0x04034b50)
      throw new InvalidPackageError('ZIP 本地文件头无效。');
    const localFlags = archive.readUInt16LE(localOffset + 6);
    const localMethod = archive.readUInt16LE(localOffset + 8);
    const localNameBytes = archive.readUInt16LE(localOffset + 26);
    const localExtraBytes = archive.readUInt16LE(localOffset + 28);
    const localNameStart = localOffset + 30;
    const dataStart = localNameStart + localNameBytes + localExtraBytes;
    const dataEnd = dataStart + compressedBytes;
    if (localFlags !== flags || localMethod !== method ||
        localNameBytes !== rawName.length || dataStart > directoryOffset ||
        dataEnd > directoryOffset ||
        !archive.subarray(localNameStart, localNameStart + localNameBytes).equals(rawName))
      throw new InvalidPackageError('ZIP 本地文件头与中央目录不一致。');

    let bytes;
    try {
      const compressed = archive.subarray(dataStart, dataEnd);
      bytes = method === 0 ? Buffer.from(compressed) :
        zlib.inflateRawSync(compressed, { maxOutputLength: MAX_FILE_BYTES });
    } catch {
      throw new InvalidPackageError('ZIP 文件内容损坏或解压后超过大小限制。');
    }
    if (bytes.length !== unpackedBytes || crc32(bytes) !== expectedCrc)
      throw new InvalidPackageError('ZIP 文件内容长度或 CRC 校验失败。');
    entries.push({ name, bytes, localStart: localOffset, localEnd: dataEnd });
    cursor = recordEnd;
  }
  if (cursor !== directoryOffset + directoryBytes)
    throw new InvalidPackageError('ZIP 中央目录长度无效。');

  const ranges = [...entries].sort((a, b) => a.localStart - b.localStart);
  for (let i = 1; i < ranges.length; i++) {
    if (ranges[i].localStart < ranges[i - 1].localEnd)
      throw new InvalidPackageError('ZIP 文件条目重叠。');
  }

  const files = new Map();
  for (const entry of entries) {
    const key = entry.name.toLocaleLowerCase('en-US');
    if (files.has(key)) throw new InvalidPackageError('ZIP 含有重复文件名。');
    files.set(key, { name: entry.name, bytes: entry.bytes });
  }
  return files;
}

function makeZip(entries) {
  const localParts = [];
  const centralParts = [];
  let offset = 0;

  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8');
    const bytes = entry.bytes;
    const compressed = zlib.deflateRawSync(bytes, { level: 9 });
    const checksum = crc32(bytes);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(8, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x0021, 12);
    local.writeUInt32LE(checksum, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(bytes.length, 22);
    local.writeUInt16LE(name.length, 26);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x0021, 14);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(bytes.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(0x81a40000, 38);
    central.writeUInt32LE(offset, 42);

    localParts.push(local, name, compressed);
    centralParts.push(central, name);
    offset += local.length + name.length + compressed.length;
  }

  const centralDirectory = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralDirectory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...localParts, centralDirectory, end]);
}

function nameKey(name) {
  return name.toLocaleLowerCase('en-US');
}

function validateFileName(name, field, extension = '.pem') {
    if (typeof name !== 'string' || name.length < 5 || name.length > 240 ||
      name !== name.split('/').pop() || /[\\/:]/.test(name) ||
      /\p{Cc}/u.test(name) || !name.toLowerCase().endsWith(extension) ||
      /private|secret/i.test(name) || name.toLowerCase() === 'admin.txt')
    throw new InvalidPackageError(field + ' 必须是普通的顶层 ' + extension + ' 文件名。');
}

function validateRsaPublicKey(bytes, label) {
  const pem = utf8(bytes, label);
  if (/PRIVATE KEY/i.test(pem))
    throw new InvalidPackageError(label + ' 只能包含公钥。');
  let key;
  try {
    key = crypto.createPublicKey(pem);
  } catch {
    throw new InvalidPackageError(label + ' 不是有效的 RSA 公钥 PEM。');
  }
  const bits = key.asymmetricKeyDetails && key.asymmetricKeyDetails.modulusLength;
  if (key.asymmetricKeyType !== 'rsa' || !bits || bits < 2048 || bits > 4096)
    throw new InvalidPackageError(label + ' 必须是 2048–4096 位 RSA 公钥。');
}

function machinePrefixValid(prefix) {
  if (typeof prefix !== 'string' || prefix.length === 0 || prefix.length > 15 ||
      !/^[A-Za-z0-9][A-Za-z0-9-]*$/.test(prefix)) return false;
  for (let number = 1; number <= 150; number++) {
    const computerName = prefix + String(number).padStart(2, '0');
    if (computerName.length > 15 || !/[A-Za-z]/.test(computerName) || computerName.endsWith('-'))
      return false;
  }
  return true;
}

function parseGuid(value) {
  if (typeof value !== 'string' ||
      !/^(?:\{)?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}(?:\})?$/i.test(value))
    throw new InvalidPackageError('manifest.json packageId 必须是有效的 GUID。');
  const normalized = value.replace(/[{}-]/g, '').toLowerCase();
  if (/^0{32}$/.test(normalized)) throw new InvalidPackageError('manifest.json packageId 不能是空 GUID。');
  return normalized;
}

function verifyManifestFile(files, fileEntry, field, extension = '.pem') {
  if (!fileEntry || typeof fileEntry !== 'object' || Array.isArray(fileEntry))
    throw new InvalidPackageError('校区清单缺少 ' + field + ' 文件清单。');
  const name = fileEntry.path;
  validateFileName(name, field, extension);
  const file = files.get(nameKey(name));
  if (!file || file.name !== name)
    throw new InvalidPackageError(field + ' 文件缺失或文件名与校区清单不一致。');
  if (!Number.isInteger(fileEntry.size) || fileEntry.size < 1 ||
      fileEntry.size > MAX_FILE_BYTES || file.bytes.length !== fileEntry.size)
    throw new InvalidPackageError(field + ' 文件大小无效或与清单不符。');
  if (typeof fileEntry.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(fileEntry.sha256))
    throw new InvalidPackageError(field + ' SHA-256 格式无效。');
  const actual = crypto.createHash('sha256').update(file.bytes).digest();
  const expected = Buffer.from(fileEntry.sha256, 'hex');
  if (!crypto.timingSafeEqual(actual, expected))
    throw new InvalidPackageError(field + ' SHA-256 不匹配。');
  return file;
}

function parseNumericVersion(value, segments, field) {
  const pattern = new RegExp(`^(0|[1-9][0-9]*)${Array.from({ length: segments - 1 }, () => String.raw`\.(0|[1-9][0-9]*)`).join('')}$`);
  if (typeof value !== 'string' || !pattern.test(value))
    throw new InvalidPackageError(field + ' 必须是规范的 ' + segments + ' 段数字版本。');
  const parts = value.split('.').map((part) => Number(part));
  if (parts.some((part) => !Number.isSafeInteger(part) || part > 65535))
    throw new InvalidPackageError(field + ' 每段必须在 0–65535 之间。');
  return parts;
}

function compareNumericVersions(left, right, segments, field) {
  const a = parseNumericVersion(left, segments, field);
  const b = parseNumericVersion(right, segments, field);
  for (let index = 0; index < segments; index++) {
    if (a[index] !== b[index]) return a[index] < b[index] ? -1 : 1;
  }
  return 0;
}

function validateCompatibility(value, schemaVersion) {
  if (schemaVersion >= 5 && (!value || typeof value !== 'object' || !Object.hasOwn(value, 'studentAgent')))
    throw new InvalidPackageError('schemaVersion=5/6 compatibility 必须提供 studentAgent 兼容范围。');
  const expectedFields = schemaVersion >= 5 ? 'studentAgent,studentApp,veyon' : 'studentApp,veyon';
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      Object.keys(value).sort().join(',') !== expectedFields)
    throw new InvalidPackageError('schemaVersion=' + schemaVersion + ' compatibility 字段无效。');
  const ranges = [['studentApp', 3], ['veyon', 4]];
  if (schemaVersion >= 5) ranges.push(['studentAgent', 3]);
  for (const [name, segments] of ranges) {
    const range = value[name];
    if (!range || typeof range !== 'object' || Array.isArray(range) ||
        Object.keys(range).sort().join(',') !== 'maxExclusive,minInclusive')
      throw new InvalidPackageError('compatibility.' + name + ' 范围无效。');
    if (compareNumericVersions(range.minInclusive, range.maxExclusive, segments, 'compatibility.' + name) >= 0)
      throw new InvalidPackageError('compatibility.' + name + ' 必须满足 minInclusive < maxExclusive。');
  }
}

function verifyPayloadManifest(files, entries, schemaVersion) {
  const expectedCount = 5 + (schemaVersion >= 5 ? 1 : 0) + (schemaVersion >= 7 ? 1 : 0);
  if (!Array.isArray(entries) || entries.length !== expectedCount)
    throw new InvalidPackageError(schemaVersion >= 7
      ? 'schemaVersion=7/8 files 必须完整列出七个载荷文件。'
      : schemaVersion >= 5
        ? 'schemaVersion=5/6 files 必须完整列出六个载荷文件。'
        : 'schemaVersion=4 files 必须完整列出五个载荷文件。');
  const seen = new Set();
  const names = [];
  for (const entry of entries) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry) ||
        Object.keys(entry).sort().join(',') !== 'path,sha256,size')
      throw new InvalidPackageError('files 项只允许 path、size 和 sha256。');
    const name = entry.path;
    if (typeof name !== 'string' || name.length < 1 || name.length > 240 || name === 'manifest.json' ||
        name !== name.split('/').pop() || /[\\/:]/.test(name) || /[\p{Cc}]/u.test(name) ||
        name === '.' || name === '..' || seen.has(nameKey(name)))
      throw new InvalidPackageError('files 含有重复、路径不规范或禁止的文件名。');
    seen.add(nameKey(name));
    const file = files.get(nameKey(name));
    if (!file || file.name !== name || !Number.isInteger(entry.size) || entry.size < 1 ||
        entry.size > MAX_FILE_BYTES || file.bytes.length !== entry.size)
      throw new InvalidPackageError('files 文件缺失、大小无效或与实际内容不符。');
    if (typeof entry.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(entry.sha256))
      throw new InvalidPackageError('files SHA-256 格式无效。');
    const actual = crypto.createHash('sha256').update(file.bytes).digest();
    if (!crypto.timingSafeEqual(actual, Buffer.from(entry.sha256, 'hex')))
      throw new InvalidPackageError('files SHA-256 不匹配。');
    names.push(name);
  }
  return names;
}

function hasCertificateSigningUsage(certificateBytes) {
  const oid = Buffer.from([0x06, 0x03, 0x55, 0x1d, 0x0f]);
  const oidOffset = certificateBytes.indexOf(oid);
  if (oidOffset < 0) return false;
  const readNode = (offset) => {
    if (offset + 2 > certificateBytes.length) return null;
    const tag = certificateBytes[offset];
    let cursor = offset + 1;
    let length = certificateBytes[cursor++];
    if ((length & 0x80) !== 0) {
      const count = length & 0x7f;
      if (count === 0 || count > 4 || cursor + count > certificateBytes.length) return null;
      length = 0;
      for (let index = 0; index < count; index++) length = length * 256 + certificateBytes[cursor++];
    }
    const end = cursor + length;
    if (end > certificateBytes.length) return null;
    return { tag, contentStart: cursor, end };
  };
  let cursor = oidOffset + oid.length;
  if (certificateBytes[cursor] === 0x01) {
    const critical = readNode(cursor);
    if (!critical) return false;
    cursor = critical.end;
  }
  const extensionValue = readNode(cursor);
  if (!extensionValue || extensionValue.tag !== 0x04) return false;
  const bitString = readNode(extensionValue.contentStart);
  if (!bitString || bitString.tag !== 0x03 || bitString.end - bitString.contentStart < 2) return false;
  // X.509 KeyUsage bit 5 is keyCertSign (0x04 in the first content octet).
  return (certificateBytes[bitString.contentStart + 1] & 0x04) !== 0;
}

function validateTeacherChannel(files, channel) {
  if (!channel || typeof channel !== 'object' || Array.isArray(channel) ||
      Object.keys(channel).sort().join(',') !== 'endpoints,rootCertificate' ||
      !Array.isArray(channel.endpoints) || channel.endpoints.length < 1 || channel.endpoints.length > 16)
    throw new InvalidPackageError('teacherChannel 必须包含 1 至 16 个连接地址和根证书。');
  const uniqueEndpoints = new Set();
  for (const endpoint of channel.endpoints) {
    if (typeof endpoint !== 'string' || endpoint.length < 1 || endpoint.length > 256)
      throw new InvalidPackageError('teacherChannel.endpoints 地址格式无效。');
    let uri;
    try { uri = new URL(endpoint); }
    catch { throw new InvalidPackageError('teacherChannel.endpoints 地址格式无效。'); }
    const address = uri.hostname.split('.').map(Number);
    const privateIpv4 = net.isIPv4(uri.hostname) &&
      (address[0] === 10 || address[0] === 172 && address[1] >= 16 && address[1] <= 31 ||
       address[0] === 192 && address[1] === 168);
    if (uri.protocol !== 'https:' || uri.port !== '39176' || uri.pathname !== '/' || uri.search || uri.hash ||
        uri.username || uri.password || !privateIpv4 || uri.href !== endpoint || uniqueEndpoints.has(endpoint))
      throw new InvalidPackageError('教师连接地址必须是唯一的私有 IPv4 HTTPS 地址。');
    uniqueEndpoints.add(endpoint);
  }

  const rootCertificate = verifyManifestFile(files, channel.rootCertificate, 'teacherChannel.rootCertificate', '.cer');
  let certificate;
  try { certificate = new crypto.X509Certificate(rootCertificate.bytes); }
  catch { throw new InvalidPackageError('教师 TLS 根证书无效。'); }
  const validTo = Date.parse(certificate.validTo);
  if (certificate.subject !== 'CN=Veyon Campus Mobile Control CA' || certificate.issuer !== certificate.subject ||
      !certificate.ca || !hasCertificateSigningUsage(certificate.raw) ||
      !Number.isFinite(validTo) || validTo <= Date.now() + 30 * 24 * 60 * 60 * 1000)
    throw new InvalidPackageError('教师 TLS 根证书主题、用途或有效期无效。');
  return rootCertificate;
}

function validatePolicyTrustRotations(files, campus, rotations, policyFiles) {
  if (!Array.isArray(rotations) || rotations.length < 1 || rotations.length > 3)
    throw new InvalidPackageError('schemaVersion=8 必须包含 1 至 3 个策略信任根轮换授权。');
  const roles = new Set();
  const roleKeys = [policyFiles.website, policyFiles.application, policyFiles.system];
  for (const envelopeText of rotations) {
    if (typeof envelopeText !== 'string' || Buffer.byteLength(envelopeText, 'utf8') < 1 ||
        Buffer.byteLength(envelopeText, 'utf8') > 16 * 1024)
      throw new InvalidPackageError('policyTrustRotations 项必须是 16 KiB 以内的签名授权 JSON 字符串。');
    const envelope = parseJson(Buffer.from(envelopeText, 'utf8'), 'policyTrustRotations');
    const expectedFields = 'campusId,issuedUtc,newFingerprint,newPublicKeyPem,newRootProofSignature,previousFingerprint,previousRootSignature,retirePreviousRootUtc,role,schemaVersion';
    if (!envelope || typeof envelope !== 'object' || Array.isArray(envelope) ||
        Object.keys(envelope).sort().join(',') !== expectedFields || envelope.schemaVersion !== 1 ||
        envelope.campusId !== campus || !Number.isInteger(envelope.role) || envelope.role < 0 || envelope.role > 2 ||
        roles.has(envelope.role))
      throw new InvalidPackageError('策略信任根轮换授权版本、校区、密钥用途或字段无效。');
    roles.add(envelope.role);
    if (typeof envelope.previousFingerprint !== 'string' || !/^[0-9a-f]{64}$/i.test(envelope.previousFingerprint) ||
        typeof envelope.newFingerprint !== 'string' || !/^[0-9a-f]{64}$/i.test(envelope.newFingerprint) ||
        envelope.previousFingerprint.toUpperCase() === envelope.newFingerprint.toUpperCase())
      throw new InvalidPackageError('策略信任根轮换授权中的旧/新指纹无效。');
    if (typeof envelope.newPublicKeyPem !== 'string' || /PRIVATE KEY/i.test(envelope.newPublicKeyPem))
      throw new InvalidPackageError('策略信任根轮换只允许包含公钥。');
    let newKey;
    try { newKey = crypto.createPublicKey(envelope.newPublicKeyPem); }
    catch { throw new InvalidPackageError('策略信任根轮换新公钥无效。'); }
    const bits = newKey.asymmetricKeyDetails && newKey.asymmetricKeyDetails.modulusLength;
    if (newKey.asymmetricKeyType !== 'rsa' || !bits || bits < 2048 || bits > 4096)
      throw new InvalidPackageError('策略信任根 RSA 公钥长度必须为 2048 至 4096 位。');
    const canonicalPem = newKey.export({ type: 'spki', format: 'pem' }).toString('ascii');
    const fingerprint = crypto.createHash('sha256')
      .update(newKey.export({ type: 'spki', format: 'der' })).digest('hex').toUpperCase();
    const packagedKey = roleKeys[envelope.role];
    if (fingerprint !== envelope.newFingerprint.toUpperCase() ||
        !packagedKey || canonicalPem !== utf8(packagedKey.bytes, '策略公钥'))
      throw new InvalidPackageError('策略轮换授权的新公钥与配置包对应策略公钥不一致。');
    const issuedUtc = envelope.issuedUtc;
    const retireUtc = envelope.retirePreviousRootUtc;
    const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})$/;
    const issued = typeof issuedUtc === 'string' && timestampPattern.test(issuedUtc) ? Date.parse(issuedUtc) : NaN;
    const retire = typeof retireUtc === 'string' && timestampPattern.test(retireUtc) ? Date.parse(retireUtc) : NaN;
    if (!Number.isFinite(issued) || !Number.isFinite(retire) || retire <= issued ||
        retire - issued > 90 * 24 * 60 * 60 * 1000)
      throw new InvalidPackageError('策略信任根过渡期必须大于零且不超过 90 天。');
    for (const field of ['previousRootSignature', 'newRootProofSignature']) {
      const encoded = envelope[field];
      if (typeof encoded !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encoded))
        throw new InvalidPackageError('策略信任根轮换签名编码无效。');
      const signature = Buffer.from(encoded, 'base64');
      if (signature.length < 256 || signature.length > 512)
        throw new InvalidPackageError('策略信任根轮换签名长度不正确。');
    }
  }
}

function validatePackageFiles(files, allowReadme) {
  const manifestFile = files.get('manifest.json');
  const campusFile = files.get('campus.json');
  if (!manifestFile || !campusFile)
    throw new InvalidPackageError('校区配置包缺少 manifest.json 或 campus.json。');
  if (files.size < 3 || files.size > 8)
    throw new InvalidPackageError('校区配置包文件数量不符合格式要求。');

  const manifest = parseJson(manifestFile.bytes, 'manifest.json');
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest))
    throw new InvalidPackageError('manifest.json 必须是 JSON 对象。');
  const allowedManifestFields = new Set([
    'schemaVersion', 'packageId', 'targetOs', 'architecture', 'campus',
    'computerPrefix', 'telemetryEndpoint', 'publicKey', 'websitePolicyPublicKey', 'applicationPolicyPublicKey', 'studentSystemPolicyPublicKey',
    'compatibility', 'files', 'recommendedOperations', 'teacherChannel', 'policyTrustRotations'
  ]);
  if (Object.keys(manifest).some((field) => !allowedManifestFields.has(field)) ||
      ![3, 4, 5, 6, 7, 8].includes(manifest.schemaVersion) ||
      (manifest.schemaVersion >= 4) !== Object.hasOwn(manifest, 'applicationPolicyPublicKey') ||
      (manifest.schemaVersion >= 5) !== Object.hasOwn(manifest, 'studentSystemPolicyPublicKey') ||
      (manifest.schemaVersion >= 4) !== Object.hasOwn(manifest, 'compatibility') ||
      (manifest.schemaVersion >= 4) !== Object.hasOwn(manifest, 'files') ||
      (manifest.schemaVersion === 6 && !Object.hasOwn(manifest, 'recommendedOperations')) ||
      (manifest.schemaVersion < 6 && Object.hasOwn(manifest, 'recommendedOperations')) ||
      (manifest.schemaVersion >= 7) !== Object.hasOwn(manifest, 'teacherChannel') ||
      (manifest.schemaVersion === 8) !== Object.hasOwn(manifest, 'policyTrustRotations'))
    throw new InvalidPackageError('当前只接受字段完整的 schemaVersion=3 至 8 校区清单。');
  const packageId = parseGuid(manifest.packageId);
  if (manifest.targetOs !== 'windows' || manifest.architecture !== 'x64')
    throw new InvalidPackageError('配置包目标必须是 Windows x64。');
  if (typeof manifest.campus !== 'string' || manifest.campus.trim().length === 0 ||
      manifest.campus.length > 100 || /\p{Cc}/u.test(manifest.campus))
    throw new InvalidPackageError('校区名称无效。');
  if (!machinePrefixValid(manifest.computerPrefix))
    throw new InvalidPackageError('电脑名前缀不能生成合法的 1–150 号电脑名。');
  if (Object.prototype.hasOwnProperty.call(manifest, 'telemetryEndpoint')) {
    if (typeof manifest.telemetryEndpoint !== 'string' || manifest.telemetryEndpoint.length > 2048 ||
        (!/^\s*$/.test(manifest.telemetryEndpoint) &&
          manifest.telemetryEndpoint !== DEFAULT_HEARTBEAT_ENDPOINT))
      throw new InvalidPackageError('遥测端点只能留空或使用项目配置的 CloudBase 心跳地址。');
  }

  const publicKeyFile = verifyManifestFile(files, manifest.publicKey, 'publicKey');
  const policyKeyFile = verifyManifestFile(files, manifest.websitePolicyPublicKey, 'websitePolicyPublicKey');
  validateRsaPublicKey(publicKeyFile.bytes, 'Veyon 校区公钥');
  validateRsaPublicKey(policyKeyFile.bytes, '网站策略公钥');
  let applicationPolicyKeyFile = null;
  let studentSystemPolicyKeyFile = null;
  let teacherChannelRootCertificate = null;
  if (manifest.schemaVersion >= 4) {
    validateCompatibility(manifest.compatibility, manifest.schemaVersion);
    applicationPolicyKeyFile = verifyManifestFile(files, manifest.applicationPolicyPublicKey, 'applicationPolicyPublicKey');
    validateRsaPublicKey(applicationPolicyKeyFile.bytes, '应用策略公钥');
  }
  if (manifest.schemaVersion >= 5) {
    studentSystemPolicyKeyFile = verifyManifestFile(files, manifest.studentSystemPolicyPublicKey, 'studentSystemPolicyPublicKey');
    validateRsaPublicKey(studentSystemPolicyKeyFile.bytes, '学生机系统策略公钥');
  }
  if (Object.hasOwn(manifest, 'recommendedOperations')) {
    const recommendations = manifest.recommendedOperations;
    const expectedRecommendationFields = [
      'changeAdminPassword', 'createStudentAccount', 'installVeyon', 'renameComputer'
    ];
    if (!recommendations || typeof recommendations !== 'object' || Array.isArray(recommendations) ||
        Object.keys(recommendations).sort().join(',') !== expectedRecommendationFields.join(','))
      throw new InvalidPackageError('recommendedOperations 必须恰好包含四项已知建议。');
    if (Object.values(recommendations).some(value => typeof value !== 'boolean'))
      throw new InvalidPackageError('recommendedOperations 的四个字段必须是布尔值。');
  }
  if (manifest.schemaVersion >= 7)
    teacherChannelRootCertificate = validateTeacherChannel(files, manifest.teacherChannel);
  if (manifest.schemaVersion >= 8)
    validatePolicyTrustRotations(files, manifest.campus, manifest.policyTrustRotations, {
      website: policyKeyFile,
      application: applicationPolicyKeyFile,
      system: studentSystemPolicyKeyFile
    });

  const manifestPayloadNames = manifest.schemaVersion >= 4
    ? verifyPayloadManifest(files, manifest.files, manifest.schemaVersion) : null;

  const expectedNames = [
    'manifest.json', 'campus.json', publicKeyFile.name, policyKeyFile.name,
    ...(applicationPolicyKeyFile ? [applicationPolicyKeyFile.name] : []),
    ...(studentSystemPolicyKeyFile ? [studentSystemPolicyKeyFile.name] : []),
    ...(teacherChannelRootCertificate ? [teacherChannelRootCertificate.name] : []),
    ...(manifest.schemaVersion >= 4 ? ['README.md'] : [])
  ];
  const uniqueNames = new Set(expectedNames.map(nameKey));
  if (uniqueNames.size !== expectedNames.length)
    throw new InvalidPackageError('校区配置包中的文件名重复。');
  if (allowReadme && files.has('readme.md')) {
    const readme = files.get('readme.md');
    if (readme.name.toLowerCase() !== 'readme.md' ||
        readme.bytes.length < 1 || readme.bytes.length > MAX_FILE_BYTES)
      throw new InvalidPackageError('README.md 文件无效或超过 16 KiB。');
  }
  if (files.size !== expectedNames.length + (allowReadme && manifest.schemaVersion === 3 && files.has('readme.md') ? 1 : 0) ||
      expectedNames.some((name) => !files.has(nameKey(name))))
    throw new InvalidPackageError('校区配置包包含未允许的文件或缺少固定配置文件。');
  for (const key of files.keys()) {
    if (!uniqueNames.has(key) && !(allowReadme && key === 'readme.md'))
      throw new InvalidPackageError('校区配置包包含未允许的文件。');
  }
  if (manifest.schemaVersion >= 4) {
    const requiredPayloadNames = [
      'campus.json', publicKeyFile.name, policyKeyFile.name, applicationPolicyKeyFile.name,
      ...(studentSystemPolicyKeyFile ? [studentSystemPolicyKeyFile.name] : []),
      ...(teacherChannelRootCertificate ? [teacherChannelRootCertificate.name] : []), 'README.md'
    ];
    if (manifestPayloadNames.length !== requiredPayloadNames.length ||
        requiredPayloadNames.some((name) => !manifestPayloadNames.includes(name)) ||
        manifestPayloadNames.some((name) => !requiredPayloadNames.includes(name)))
      throw new InvalidPackageError('schemaVersion=' + manifest.schemaVersion + ' files 必须和固定配置载荷完全一致。');
  }

  const campus = parseJson(campusFile.bytes, 'campus.json');
  if (!campus || typeof campus !== 'object' || Array.isArray(campus) ||
      Object.keys(campus).some((field) =>
        !['campus', 'computerPrefix', 'keyFile', 'websitePolicyKeyFile', 'applicationPolicyKeyFile', 'systemPolicyKeyFile'].includes(field)) ||
      campus.campus !== manifest.campus ||
      campus.computerPrefix !== manifest.computerPrefix ||
      campus.keyFile !== publicKeyFile.name ||
      campus.websitePolicyKeyFile !== policyKeyFile.name ||
      campus.applicationPolicyKeyFile !== (applicationPolicyKeyFile?.name) ||
      campus.systemPolicyKeyFile !== (studentSystemPolicyKeyFile?.name))
    throw new InvalidPackageError('campus.json 与已校验的配置清单不一致。');

  const totalBytes = expectedNames.reduce((sum, name) => sum + files.get(nameKey(name)).bytes.length, 0);
  if (totalBytes > MAX_ARCHIVE_BYTES)
    throw new InvalidPackageError('校区配置包解压后超过 64 KiB。');
  const canonicalEntries = expectedNames.map((name) => ({
    name,
    bytes: files.get(nameKey(name)).bytes
  }));
  const archiveBytes = makeZip(canonicalEntries);
  if (archiveBytes.length > MAX_ARCHIVE_BYTES)
    throw new InvalidPackageError('校区配置包超过 64 KiB 的大小限制。');

  return {
    packageId,
    campus: manifest.campus,
    computerPrefix: manifest.computerPrefix,
    schemaVersion: manifest.schemaVersion,
    archiveBytes
  };
}

function canonicalizeArchive(archiveBytes) {
  const files = readZipEntries(archiveBytes);
  return validatePackageFiles(files, false);
}

function canonicalizeFolderFiles(uploadedFiles) {
  if (!Array.isArray(uploadedFiles) || uploadedFiles.length < 1 || uploadedFiles.length > 8)
    throw new InvalidPackageError('配置文件夹必须包含 1 至 8 个文件。');

  let selectedFolder = null;
  let hasFlatName = false;
  let totalBytes = 0;
  const files = new Map();
  for (const uploaded of uploadedFiles) {
    const relativePath = uploaded.fileName.replace(/\\/g, '/');
    const parts = relativePath.split('/');
    if (!relativePath || relativePath.startsWith('/') || relativePath.includes(':') ||
        /\p{Cc}/u.test(relativePath) ||
        parts.some((part) => !part || part === '.' || part === '..') ||
        parts.length < 1 || parts.length > 2)
      throw new InvalidPackageError('文件夹上传只能包含同一平面目录的普通文件，不能包含链接或路径穿越。');
    if (parts.length === 2) {
      if (hasFlatName || (selectedFolder !== null && selectedFolder !== parts[0]))
        throw new InvalidPackageError('上传文件必须来自同一个配置包文件夹。');
      selectedFolder = parts[0];
    } else {
      if (selectedFolder !== null)
        throw new InvalidPackageError('上传文件必须来自同一个配置包文件夹。');
      hasFlatName = true;
    }
    const name = parts[parts.length - 1];
    const key = nameKey(name);
    if (files.has(key)) throw new InvalidPackageError('配置包文件名不能重复。');
    if (!Buffer.isBuffer(uploaded.bytes) || uploaded.bytes.length < 1 ||
        uploaded.bytes.length > MAX_FILE_BYTES)
      throw new InvalidPackageError('每个配置包文件必须在 1 字节至 16 KiB 之间。');
    totalBytes += uploaded.bytes.length;
    if (totalBytes > MAX_ARCHIVE_BYTES)
      throw new InvalidPackageError('配置包文件夹内容超过 64 KiB。');
    files.set(key, { name, bytes: uploaded.bytes });
  }
  return validatePackageFiles(files, true);
}

module.exports = {
  InvalidPackageError,
  MAX_ARCHIVE_BYTES,
  canonicalizeArchive,
  canonicalizeFolderFiles,
  parseJson
};
