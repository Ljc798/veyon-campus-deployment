'use strict';

const GITHUB_REPOSITORY = 'Ljc798/veyon-campus-deployment';

function downloadConfiguration(environment = {}) {
  const source = (environment.CloudBase__ApplicationReleaseDownloadSource || 'github').trim();
  const giteeRepository = (environment.CloudBase__ApplicationReleaseGiteeRepository || '').trim();
  if (!['github', 'gitee'].includes(source) ||
      (source === 'gitee' && (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(giteeRepository) ||
        giteeRepository.split('/').some(segment => ['.', '..'].includes(segment)))))
    throw new Error('Application installer download source must be GitHub or a configured Gitee repository.');
  return { source, giteeRepository };
}

function installerDownloadUrl(configuration, release) {
  const role = { TeacherConsole: 'Teacher', StudentSetup: 'Student' }[release.role];
  if (!role || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(release.version) ||
      release.fileName !== `VeyonCampus-${role}-Setup-${release.version}-win-x64.exe`)
    throw new Error('External installer downloads require matching stable release metadata.');
  const repository = configuration.source === 'gitee' ? configuration.giteeRepository : GITHUB_REPOSITORY;
  const host = configuration.source === 'gitee' ? 'gitee.com' : 'github.com';
  // Validate caller-supplied configuration too; this cannot become an arbitrary redirect endpoint.
  downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: configuration.source,
    CloudBase__ApplicationReleaseGiteeRepository: configuration.giteeRepository });
  return `https://${host}/${repository}/releases/download/v${release.version}/${release.fileName}`;
}

module.exports = { downloadConfiguration, installerDownloadUrl };
