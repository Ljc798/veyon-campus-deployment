using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class StudentSetupUpdateViewModel : INotifyPropertyChanged
{
    private readonly ApplicationReleaseClient? _releaseClient;
    private readonly string? _releaseClientError;
    private ApplicationReleaseEnvelope? _latestRelease;
    private ApplicationReleaseEnvelope? _stagedRelease;
    private string? _stagedInstallerPath;
    private bool _updateAvailable;
    private bool _latestHasRequiredPolicyCapabilities;
    private bool _stagedHasRequiredPolicyCapabilities;
    private bool _isBusy;
    private string _status;
    private string _offlineStatus = "请选择离线更新文件。";

    public StudentSetupUpdateViewModel()
    {
        CurrentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
        try { _releaseClient = new ApplicationReleaseClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            _releaseClientError = "在线更新暂不可用。";
        }
        _status = _releaseClientError ?? (OperatingSystem.IsWindows()
            ? "尚未检查更新。"
            : "请在 Windows 电脑上使用更新功能。");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentVersion { get; }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; Changed(); } }
    public string OfflineStatus { get => _offlineStatus; private set { if (_offlineStatus == value) return; _offlineStatus = value; Changed(); } }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            Changed();
            NotifyActions();
        }
    }

    public bool CanCheckLatest => OperatingSystem.IsWindows() && _releaseClient is not null && !IsBusy;
    public bool CanDownloadAndInstall => OperatingSystem.IsWindows() && _releaseClient is not null && !IsBusy &&
        _updateAvailable && _latestRelease is not null && _latestHasRequiredPolicyCapabilities;
    public bool CanExportOfflineUpdate => CanDownloadAndInstall;
    public bool CanVerifyOfflineUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsBusy;
    public bool CanInstallOfflineUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsBusy &&
        _stagedRelease is not null && _stagedInstallerPath is not null &&
        _stagedHasRequiredPolicyCapabilities &&
        ApplicationReleaseClient.CompareVersions(_stagedRelease.Manifest.Version, CurrentVersion) > 0;

    public async Task CheckLatestAsync()
    {
        var releaseClient = _releaseClient;
        if (!CanCheckLatest || releaseClient is null) return;
        IsBusy = true;
        _latestRelease = null;
        _updateAvailable = false;
        _latestHasRequiredPolicyCapabilities = false;
        NotifyActions();
        Status = "正在检查更新……";
        try
        {
            var result = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.StudentSetup, CurrentVersion);
            _latestRelease = result.Release;
            _updateAvailable = result.IsNewer;
            _latestHasRequiredPolicyCapabilities = result.Release is not null &&
                HasRequiredPolicyCapabilities(result.Release.Manifest);
            Status = result.Release is null
                ? "目前没有可用的更新。"
                : result.IsNewer
                    ? _latestHasRequiredPolicyCapabilities
                        ? $"发现新版本 {result.Release.Manifest.Version}。"
                        : $"发现新版本 {result.Release.Manifest.Version}，暂不兼容当前版本。"
                    : "当前已是最新版本。";
        }
        catch (Exception exception)
        {
            _latestRelease = null;
            _updateAvailable = false;
            _latestHasRequiredPolicyCapabilities = false;
            Status = "检查更新失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyActions();
        }
    }

    public async Task<bool> DownloadAndInstallAsync()
    {
        var releaseClient = _releaseClient;
        var latestRelease = _latestRelease;
        if (!CanDownloadAndInstall || releaseClient is null || latestRelease is null) return false;
        IsBusy = true;
        NotifyActions();
        Status = "正在下载更新……";
        try
        {
            var updateDirectory = GetUpdateDirectory();
            var installerPath = await releaseClient.DownloadAsync(latestRelease,
                ApplicationReleaseRole.StudentSetup, updateDirectory);
            var verified = ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath,
                ApplicationReleaseRole.StudentSetup, releaseClient.ApiBaseAddress,
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            if (ApplicationReleaseClient.CompareVersions(verified.Manifest.Version, CurrentVersion) <= 0)
                throw new InvalidDataException("下载的 StudentSetup 版本不高于当前版本，已停止安装。");
            if (!HasRequiredPolicyCapabilities(verified.Manifest))
                throw new InvalidDataException("StudentSetup 候选版本未声明应用与系统策略兼容能力，已停止安装。");
            if (verified != latestRelease)
                throw new InvalidDataException("下载后的签名清单与刚才查询的版本不一致，已停止安装。");

            ApplicationReleaseUpdateHandoff.Start(installerPath, ApplicationReleaseRole.StudentSetup, CurrentVersion);
            Status = "更新已启动，应用将关闭并自动重启。";
            return true;
        }
        catch (Exception exception)
        {
            Status = "下载或启动更新失败：" + exception.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyActions();
        }
    }

    public async Task ExportOfflineUpdateAsync(string destinationDirectory)
    {
        var releaseClient = _releaseClient;
        var latestRelease = _latestRelease;
        if (!CanDownloadAndInstall || releaseClient is null || latestRelease is null) return;
        IsBusy = true;
        NotifyActions();
        Status = "正在准备离线更新包……";
        try
        {
            var updateDirectory = GetUpdateDirectory();
            var sourceInstallerPath = await releaseClient.DownloadAsync(latestRelease,
                ApplicationReleaseRole.StudentSetup, updateDirectory);
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var exportedPath = await Task.Run(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(
                sourceInstallerPath, destinationDirectory, ApplicationReleaseRole.StudentSetup,
                releaseClient.ApiBaseAddress, publicKeyPem));
            var exportedRelease = ApplicationReleaseClient.ReadVerifiedStagedRelease(exportedPath,
                ApplicationReleaseRole.StudentSetup, releaseClient.ApiBaseAddress, publicKeyPem);
            if (exportedRelease != latestRelease)
                throw new InvalidDataException("导出的离线发布清单与刚才查询的版本不一致。");
            Status = $"离线更新包已保存到所选文件夹（版本 {exportedRelease.Manifest.Version}）。";
        }
        catch (Exception exception)
        {
            Status = "离线更新包导出失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyActions();
        }
    }

    public async Task VerifyOfflineUpdateAsync(string installerPath)
    {
        var releaseClient = _releaseClient;
        if (!CanVerifyOfflineUpdate || releaseClient is null) return;
        IsBusy = true;
        _stagedRelease = null;
        _stagedInstallerPath = null;
        _stagedHasRequiredPolicyCapabilities = false;
        NotifyActions();
        OfflineStatus = "正在检查离线更新文件……";
        try
        {
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var updateDirectory = GetUpdateDirectory();
            var stagedPath = await Task.Run(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(
                installerPath, updateDirectory, ApplicationReleaseRole.StudentSetup,
                releaseClient.ApiBaseAddress, publicKeyPem));
            var verified = ApplicationReleaseClient.ReadVerifiedStagedRelease(stagedPath,
                ApplicationReleaseRole.StudentSetup, releaseClient.ApiBaseAddress, publicKeyPem);
            _stagedInstallerPath = stagedPath;
            _stagedRelease = verified;
            _stagedHasRequiredPolicyCapabilities = HasRequiredPolicyCapabilities(verified.Manifest);
            OfflineStatus = ApplicationReleaseClient.CompareVersions(verified.Manifest.Version, CurrentVersion) > 0 &&
                            _stagedHasRequiredPolicyCapabilities
                ? $"离线更新 {verified.Manifest.Version} 已准备好，可以安装并重启。"
                : ApplicationReleaseClient.CompareVersions(verified.Manifest.Version, CurrentVersion) > 0
                    ? "此更新与当前版本不兼容，无法安装。"
                    : $"所选文件版本不高于当前版本 {CurrentVersion}，无法安装。";
        }
        catch (Exception exception)
        {
            _stagedRelease = null;
            _stagedInstallerPath = null;
            _stagedHasRequiredPolicyCapabilities = false;
            OfflineStatus = "离线安装器验证失败，未启动安装：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyActions();
        }
    }

    public bool InstallOfflineUpdate()
    {
        if (!CanInstallOfflineUpdate || _releaseClient is null || _stagedInstallerPath is null ||
            _stagedRelease is null) return false;
        try
        {
            var verified = ApplicationReleaseClient.ReadVerifiedStagedRelease(_stagedInstallerPath,
                ApplicationReleaseRole.StudentSetup, _releaseClient.ApiBaseAddress,
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            if (verified != _stagedRelease ||
                ApplicationReleaseClient.CompareVersions(verified.Manifest.Version, CurrentVersion) <= 0 ||
                !HasRequiredPolicyCapabilities(verified.Manifest))
                throw new InvalidDataException("暂存文件发生变化，或不再是高于当前版本的 StudentSetup 安装器。");
            ApplicationReleaseUpdateHandoff.Start(_stagedInstallerPath,
                ApplicationReleaseRole.StudentSetup, CurrentVersion);
            OfflineStatus = $"已再次验签并启动 StudentSetup {verified.Manifest.Version} 更新；应用将关闭，安装失败时会尝试恢复旧版本。";
            return true;
        }
        catch (Exception exception)
        {
            _stagedRelease = null;
            _stagedInstallerPath = null;
            _stagedHasRequiredPolicyCapabilities = false;
            OfflineStatus = "离线更新未启动；暂存文件复核失败：" + exception.Message;
            NotifyActions();
            return false;
        }
    }

    public void ReportOfflineUpdateError(string message) => OfflineStatus = message;

    private static bool HasRequiredPolicyCapabilities(ApplicationReleaseManifest manifest) =>
        ApplicationReleaseCompatibility.SupportsApplicationPolicy(manifest) &&
        ApplicationReleaseCompatibility.SupportsStudentSystemPolicy(manifest);

    private static string GetUpdateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeyonCampus", "Updates");

    private void NotifyActions()
    {
        Changed(nameof(CanCheckLatest));
        Changed(nameof(CanDownloadAndInstall));
        Changed(nameof(CanExportOfflineUpdate));
        Changed(nameof(CanVerifyOfflineUpdate));
        Changed(nameof(CanInstallOfflineUpdate));
    }

    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
