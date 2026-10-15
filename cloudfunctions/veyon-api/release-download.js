'use strict';

const GITCODE_REPOSITORY = 'ljc_1126/veyon-campus-deployment';

function downloadConfiguration(environment = {}) {
  const source = (environment.CloudBase__ApplicationReleaseDownloadSource || 'gitcode').trim();
  if (source !== 'github' && source !== 'gitcode')
    throw new Error('Application installer downloads must use the configured GitCode Release.');
  return { source: 'gitcode' };
}

function installerDownloadUrl(configuration, release) {
  const role = { TeacherConsole: 'Teacher', StudentSetup: 'Student' }[release.role];
  if (!role || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(release.version) ||
      release.fileName !== `VeyonCampus-${role}-Setup-${release.version}-win-x64.exe`)
    throw new Error('External installer downloads require matching stable release metadata.');
  // Validate caller-supplied configuration too; this cannot become an arbitrary redirect endpoint.
  downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: configuration.source });
  return `https://gitcode.com/${GITCODE_REPOSITORY}/releases/download/v${release.version}/${release.fileName}`;
}

module.exports = { downloadConfiguration, installerDownloadUrl };
