'use strict';

const GITHUB_REPOSITORY = 'Ljc798/veyon-campus-deployment';

function downloadConfiguration(environment = {}) {
  const source = (environment.CloudBase__ApplicationReleaseDownloadSource || 'github').trim();
  if (source !== 'github')
    throw new Error('Application installer downloads must use the configured GitHub Release.');
  return { source };
}

function installerDownloadUrl(configuration, release) {
  const role = { TeacherConsole: 'Teacher', StudentSetup: 'Student' }[release.role];
  if (!role || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(release.version) ||
      release.fileName !== `VeyonCampus-${role}-Setup-${release.version}-win-x64.exe`)
    throw new Error('External installer downloads require matching stable release metadata.');
  // Validate caller-supplied configuration too; this cannot become an arbitrary redirect endpoint.
  downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: configuration.source });
  return `https://github.com/${GITHUB_REPOSITORY}/releases/download/v${release.version}/${release.fileName}`;
}

module.exports = { downloadConfiguration, installerDownloadUrl };
