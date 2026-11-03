using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App.Maintenance;

public sealed record TeacherMaintenanceRelease(ApplicationReleaseEnvelope Release,
    string? StudentAgentVersion, MaintenanceReleaseChannel Channel);

/// <summary>Resolves only the teacher-selected release channel and stages its verified StudentSetup artifact.</summary>
public sealed class TeacherMaintenanceArtifactProvider : ILegacyStudentSetupArtifactProvider
{
    private readonly ApplicationReleaseClient _releaseClient;
    private readonly Func<string?> _localTestInstallerPath;

    public TeacherMaintenanceArtifactProvider(ApplicationReleaseClient releaseClient,
        Func<string?> localTestInstallerPath)
    {
        _releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
        _localTestInstallerPath = localTestInstallerPath ?? throw new ArgumentNullException(nameof(localTestInstallerPath));
    }

    public async Task<TeacherMaintenanceRelease> GetLatestReleaseAsync(MaintenanceReleaseChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
        if (channel == MaintenanceReleaseChannel.LocalTest)
        {
            var installerPath = _localTestInstallerPath()
                                ?? throw new InvalidOperationException("请选择本机测试版 StudentSetup 安装器。");
            var release = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath,
                ApplicationReleaseRole.StudentSetup, _releaseClient.ApiBaseAddress, publicKeyPem), cancellationToken)
                .ConfigureAwait(false);
            ValidateLocalTestPackage(release, _releaseClient.ApiBaseAddress, publicKeyPem);
            return new TeacherMaintenanceRelease(release, null, channel);
        }

        var check = await _releaseClient.CheckLatestAsync(ApplicationReleaseRole.StudentSetup, "0.0.0",
            cancellationToken).ConfigureAwait(false);
        var stableRelease = check.Release ?? throw new InvalidOperationException("当前没有可用的 StudentSetup 发行包。");
        ApplicationReleaseClient.Verify(stableRelease, ApplicationReleaseRole.StudentSetup,
            _releaseClient.ApiBaseAddress, publicKeyPem);
        if (stableRelease.Manifest.SchemaVersion != 3 || stableRelease.Manifest.ReleaseChannel == "local-test")
            throw new InvalidDataException("旧 Agent 维护通道只允许使用已验证的 stable Schema 3 StudentSetup 包。");
        ValidateAgentTargetBinding(check, stableRelease);
        ApplicationReleaseCompatibility.EnsureSupports(stableRelease.Manifest,
            applicationPolicyRequired: true, studentSystemPolicyRequired: true);
        return new TeacherMaintenanceRelease(stableRelease,
            stableRelease.Manifest.StudentAgentVersion ?? check.StudentAgentTarget?.Manifest.StudentAgentVersion,
            channel);
    }

    public async Task<LegacyStudentSetupArtifact> GetVerifiedArtifactAsync(MaintenancePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetSetupVersion is null || plan.TargetReleaseSha256 is null)
            throw new InvalidDataException("维护计划缺少 StudentSetup 目标版本或发行摘要。");
        var channel = plan.Channel;
        var latest = await GetLatestReleaseAsync(channel, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(latest.Release.Manifest.Version, plan.TargetSetupVersion, StringComparison.Ordinal) ||
            !string.Equals(latest.Release.Manifest.Sha256, plan.TargetReleaseSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(latest.StudentAgentVersion, plan.TargetAgentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前受信任发行目标已变化；请重新检查并生成维护计划。");

        var updateDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
            "VeyonCampus", "Updates");
        var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
        var installerPath = channel == MaintenanceReleaseChannel.LocalTest
            ? await Task.Run(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(
                _localTestInstallerPath() ?? throw new InvalidOperationException("本机测试 StudentSetup 包已取消选择。"),
                updateDirectory, ApplicationReleaseRole.StudentSetup, _releaseClient.ApiBaseAddress, publicKeyPem),
                cancellationToken).ConfigureAwait(false)
            : await _releaseClient.DownloadAsync(latest.Release, ApplicationReleaseRole.StudentSetup,
                updateDirectory, cancellationToken).ConfigureAwait(false);
        var staged = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath,
            ApplicationReleaseRole.StudentSetup, _releaseClient.ApiBaseAddress, publicKeyPem), cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(staged.Manifest.Sha256, plan.TargetReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(staged.Manifest.Version, plan.TargetSetupVersion, StringComparison.Ordinal))
            throw new InvalidDataException("本机暂存 StudentSetup 与维护计划不一致；未发送学生端命令。");
        return new LegacyStudentSetupArtifact(installerPath, _releaseClient.ApiBaseAddress, publicKeyPem,
            latest.StudentAgentVersion);
    }

    private static void ValidateLocalTestPackage(ApplicationReleaseEnvelope release, Uri apiBaseAddress,
        string publicKeyPem)
    {
        ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.StudentSetup, apiBaseAddress, publicKeyPem);
        if (release.Manifest.SchemaVersion != 3 || release.Manifest.ReleaseChannel is not null ||
            release.Manifest.StudentAgentVersion is not null)
            throw new InvalidDataException("本机测试包须为 Developer Release 签名的 stable Schema 3 StudentSetup，且不得伪造 Agent 目标版本。");
        ApplicationReleaseCompatibility.EnsureSupports(release.Manifest,
            applicationPolicyRequired: true, studentSystemPolicyRequired: true);
    }

    private static void ValidateAgentTargetBinding(ApplicationReleaseCheckResult check,
        ApplicationReleaseEnvelope release)
    {
        if (check.StudentAgentTarget is not { } target) return;
        var manifest = target.Manifest;
        if (!string.Equals(manifest.SetupVersion, release.Manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(manifest.InstallerSha256, release.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("签名 Agent 目标清单未绑定当前 StudentSetup 版本与摘要。");
        if (release.Manifest.StudentAgentVersion is { } manifestAgentVersion &&
            !string.Equals(manifestAgentVersion, manifest.StudentAgentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("安装包 Agent 目标与独立签名目标清单不一致。");
    }
}
