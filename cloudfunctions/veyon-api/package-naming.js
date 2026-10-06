'use strict';

const INVALID_CAMPUS_FILENAME_CHARACTER = /[\p{Cc}<>:"\/\\|?*]/u;

function createCampusFileStem(campusName) {
  if (typeof campusName !== 'string' || campusName.length === 0)
    throw new TypeError('校区名称不能为空。');

  const normalized = campusName.normalize('NFKC').trim();
  const stem = Array.from(normalized, (character) =>
    INVALID_CAMPUS_FILENAME_CHARACTER.test(character) ? '-' : character).join('').replace(/^[ .]+|[ .]+$/g, '');
  return stem || 'campus';
}

function createCampusPackageFileName(campusName, packageId) {
  if (typeof packageId !== 'string' || !/^[0-9a-f]{32}$/i.test(packageId))
    throw new TypeError('配置包编号无效。');
  return `${createCampusFileStem(campusName)}-${packageId.toLowerCase()}.zip`;
}

function createCampusPackageObjectKey(campusName, packageId, schemaVersion = 3) {
  if (![3, 4].includes(schemaVersion)) throw new TypeError('配置包版本不受支持。');
  return `deployment-packages/v${schemaVersion}/${createCampusPackageFileName(campusName, packageId)}`;
}

function isSafeCampusPackageFileName(fileName, packageId) {
  if (typeof fileName !== 'string' || typeof packageId !== 'string') return false;
  const suffix = `-${packageId.replace(/-/g, '').toLowerCase()}.zip`;
  if (!fileName.toLowerCase().endsWith(suffix)) return false;
  const stem = fileName.slice(0, -suffix.length);
  return stem.length > 0 &&
    !Array.from(stem).some((character) => INVALID_CAMPUS_FILENAME_CHARACTER.test(character)) &&
    stem === stem.trim() && !/[ .]$/.test(stem);
}

module.exports = {
  createCampusPackageFileName,
  createCampusPackageObjectKey,
  isSafeCampusPackageFileName
};
