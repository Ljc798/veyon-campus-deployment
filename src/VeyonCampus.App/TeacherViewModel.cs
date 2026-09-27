using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class TeacherViewModel : INotifyPropertyChanged
{
    private readonly VeyonInstallerStore _installerStore;
    private readonly ITaskLease _lease;
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private bool _isExecuting;
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _campusId = "", _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _installerStatus = "Veyon 安装器已内嵌在 App 中；无需联网下载。", _teacherInstallResult = "", _teacherInstallIssue = "";
    private int _websiteModeIndex = 1, _websiteDurationIndex = 1;

    public TeacherViewModel(VeyonInstallerStore? installerStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
        LoadLatestWebsitePolicyHistory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanFillFailedWebsiteTargets)); } }
    public string PageTitle => "教师控制台";
    public string PageDescription => "管理教师端 Veyon、生成学生校区资料，并签名推送课堂网站策略。";
    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? $"教师控制台 {AppVersion} · 签名私钥只在当前教师 Windows 用户证书库中使用。"
        : $"教师控制台 {AppVersion} · 当前为界面预览环境；教师部署和策略签名只支持 Windows。";

    public bool CanInstallTeacherVeyon => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanGenerateStudentPackage => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanPushWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && IsWebsitePolicyInputValid();
    public bool CanDisableWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanFillFailedWebsiteTargets => !IsExecuting && _lastFailedWebsiteTargets.Count > 0;
    public string TeacherInstallPlanText =>
        $"目标计算机：{Environment.MachineName}\n操作：从 App 内嵌资源校验并安装官方 Veyon {VeyonInstallerTrust.Version} x64 教师组件（含 Master）。安装可能要求重启；检测到本机已有 Veyon 时会停止并提示不要重复安装。";
    public string TeacherInstallSafetyText =>
        "安装会添加 Veyon 系统服务并修改系统配置。开始前请暂时退出 360 等杀毒软件；安装完成后立即重新开启防护。";

    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomCount { get => _roomCount; set { _roomCount = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string CampusId { get => _campusId; set { _campusId = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); } }
    public string RoomOutputDir { get => _roomOutputDir; set { _roomOutputDir = value ?? ""; Changed(); } }
    public string WebsiteTargets { get => _websiteTargets; set { _websiteTargets = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); } }
    public string WebsiteDomains { get => _websiteDomains; set { _websiteDomains = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public int WebsiteModeIndex { get => _websiteModeIndex; set { _websiteModeIndex = Math.Clamp(value, 0, 2); Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public int WebsiteDurationIndex { get => _websiteDurationIndex; set { _websiteDurationIndex = Math.Clamp(value, 0, 4); Changed(); } }
    public string WebsitePolicyResult { get => _websitePolicyResult; private set { _websitePolicyResult = value; Changed(); Changed(nameof(HasWebsitePolicyResult)); } }
    public bool HasWebsitePolicyResult => WebsitePolicyResult.Length > 0;
    public string WebsitePolicyError { get => _websitePolicyError; private set { _websitePolicyError = value; Changed(); Changed(nameof(HasWebsitePolicyError)); } }
    public bool HasWebsitePolicyError => WebsitePolicyError.Length > 0;
    public string WebsitePolicyHistoryText { get => _websitePolicyHistoryText; private set { _websitePolicyHistoryText = value; Changed(); Changed(nameof(HasWebsitePolicyHistory)); } }
    public bool HasWebsitePolicyHistory => WebsitePolicyHistoryText.Length > 0;
    public string InstallerStatus { get => _installerStatus; private set { _installerStatus = value; Changed(); } }
    public string TeacherInstallResult { get => _teacherInstallResult; private set { _teacherInstallResult = value; Changed(); Changed(nameof(HasTeacherInstallResult)); } }
    public bool HasTeacherInstallResult => TeacherInstallResult.Length > 0;
    public string TeacherInstallIssue { get => _teacherInstallIssue; private set { _teacherInstallIssue = value; Changed(); Changed(nameof(HasTeacherInstallIssue)); } }
    public bool HasTeacherInstallIssue => TeacherInstallIssue.Length > 0;
    public string PackageOutput { get => _packageOutput; private set { _packageOutput = value; Changed(); Changed(nameof(HasPackageOutput)); } }
    public string PackageOutputError { get => _packageOutputError; private set { _packageOutputError = value; Changed(); Changed(nameof(HasPackageOutputError)); } }
    public bool HasPackageOutput => PackageOutput.Length > 0;
    public bool HasPackageOutputError => PackageOutputError.Length > 0;
    public IReadOnlyList<string> RoomNames { get => _roomNames; private set { _roomNames = value; Changed(); Changed(nameof(HasRoomPreview)); Changed(nameof(RoomSummary)); } }
    public bool HasRoomPreview => RoomNames.Count > 0;
    public string RoomError { get => _roomError; private set { _roomError = value; Changed(); Changed(nameof(HasRoomError)); } }
    public bool HasRoomError => RoomError.Length > 0;
    public string RoomSummary => HasRoomPreview ? $"共 {RoomNames.Count} 台，首台 {RoomNames[0]}，末台 {RoomNames[^1]}" : "尚未生成清单";

    public void GenerateRoomPreview()
    {
        RoomNames = Array.Empty<string>();
        RoomError = "";
        try { RoomNames = MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount); }
        catch (InvalidDataException exception) { RoomError = exception.Message; }
    }

    public void CloseRoomPreview() => ClearRoomPreview();

    public void FillWebsiteTargetsFromRoom()
    {
        try
        {
            var names = RoomNames.Count > 0 ? RoomNames : MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount);
            WebsiteTargets = string.Join(Environment.NewLine, names);
            RoomError = "";
        }
        catch (InvalidDataException exception) { RoomError = exception.Message; }
    }

    public async Task PushWebsitePolicyAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        WebsitePolicyResult = "";
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("网站策略推送仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var mode = WebsiteModeIndex switch
            {
                0 => WebsitePolicyMode.Disabled,
                1 => WebsitePolicyMode.Blocklist,
                2 => WebsitePolicyMode.Allowlist,
                _ => throw new InvalidDataException("网站策略模式无效。")
            };
            var domains = mode == WebsitePolicyMode.Disabled
                ? Array.Empty<string>()
                : WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var issuedUtc = DateTimeOffset.UtcNow;
            TimeSpan? duration = mode == WebsitePolicyMode.Disabled ? null : WebsiteDurationIndex switch
            {
                0 => TimeSpan.FromMinutes(45),
                1 => TimeSpan.FromHours(1),
                2 => TimeSpan.FromMinutes(90),
                3 => TimeSpan.FromHours(2),
                4 => null,
                _ => throw new InvalidDataException("网站限制时长无效。")
            };
            DateTimeOffset? expiresUtc = duration is { } lifetime ? issuedUtc + lifetime : null;
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = WebsitePolicyCompiler.Create(campus, revision, mode, domains, issuedUtc, expiresUtc);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var signedPolicy = WebsitePolicyCryptography.Sign(policy, signingKey.PrivateKey);
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 不自动到期";
            var heading = $"策略版本 {revision} · {mode switch { WebsitePolicyMode.Disabled => "已停用", WebsitePolicyMode.Blocklist => "黑名单", _ => "白名单" }}{expirySummary} · 代理确认 {succeeded}/{results.Count} 台 · 需核对 {needsReview} · 失败 {failed}";
            WebsitePolicyResult = heading + Environment.NewLine + string.Join(Environment.NewLine,
                results.Select(result => $"{result.Target}：{(result.Succeeded ? "代理已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
            var history = new WebsitePolicyPushHistoryEntry(DateTimeOffset.UtcNow, campus, revision, mode, expiresUtc, results);
            UpdateWebsitePolicyHistory(history);
            var errorMessages = new List<string>();
            try { WebsitePolicyPushHistoryStore.Append(history); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              ArgumentException or System.Text.Json.JsonException)
            {
                errorMessages.Add("本次推送结果已显示，但本机历史记录未保存：" + exception.Message);
            }
            if (succeeded != results.Count)
                errorMessages.Add(string.Join(" ", new[]
                {
                    needsReview > 0 ? "部分学生机没有返回代理确认；这些目标状态不明。" : "",
                    failed > 0 ? "部分学生机明确拒绝或未应用策略。" : "",
                    "可检查逐台结果并重新推送；重试会生成新的策略版本。"
                }.Where(text => text.Length > 0)));
            if (errorMessages.Count > 0) WebsitePolicyError = string.Join(" ", errorMessages);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "网站策略未推送：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public Task DisableWebsitePolicyAsync()
    {
        WebsiteModeIndex = 0;
        return PushWebsitePolicyAsync();
    }

    public void FillFailedWebsiteTargets()
    {
        if (_lastFailedWebsiteTargets.Count == 0) return;
        WebsiteTargets = string.Join(Environment.NewLine, _lastFailedWebsiteTargets);
        WebsitePolicyHistoryText += Environment.NewLine + "已填入失败或需核对的设备；请核对当前网站规则和时长，再发起新版本推送。";
    }

    private void LoadLatestWebsitePolicyHistory()
    {
        try
        {
            var latest = WebsitePolicyPushHistoryStore.ReadLatest();
            if (latest is not null) UpdateWebsitePolicyHistory(latest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          ArgumentException or System.Text.Json.JsonException)
        {
            WebsitePolicyHistoryText = "无法读取本机上次推送记录；当前仍可编辑新策略。";
        }
    }

    private void UpdateWebsitePolicyHistory(WebsitePolicyPushHistoryEntry entry)
    {
        _lastFailedWebsiteTargets = Array.AsReadOnly(entry.Results.Where(result => !result.Succeeded)
            .Select(result => result.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var succeeded = entry.Results.Count(result => result.Succeeded);
        var needsReview = entry.Results.Count(result => !result.Succeeded && result.NeedsReview);
        var failed = entry.Results.Count(result => !result.Succeeded && !result.NeedsReview);
        WebsitePolicyHistoryText = $"最近一次推送：{entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 校区 {entry.CampusId} · 版本 {entry.Revision} · 代理确认 {succeeded}/{entry.Results.Count} · 需核对 {needsReview} · 失败 {failed}。本机仅保存设备与结果，不保存域名清单或签名内容；最多保留 {WebsitePolicyPushHistoryStore.MaximumRuns} 次。";
        Changed(nameof(CanFillFailedWebsiteTargets));
    }

    private bool IsWebsitePolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            if (WebsiteModeIndex != 0)
                WebsitePolicyCompiler.NormalizeDomains(WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return WebsiteModeIndex == 0 || WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(domain => !string.IsNullOrWhiteSpace(domain));
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
    }

    private bool AreWebsitePolicyTargetsValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
    }

    private static readonly System.Text.RegularExpressions.Regex CampusIdPattern =
        new("^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public async Task GenerateStudentPackageAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        PackageOutput = "";
        PackageOutputError = "";
        string? temporaryPublicKey = null;
        var teacherKeyCreated = false;
        var campus = CampusId.Trim();
        if (campus.Length == 0 || !CampusIdPattern.IsMatch(campus))
        {
            PackageOutputError = "校区 ID 只能包含中英文、数字、连字符或下划线。";
            EndExclusiveTask();
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                PackageOutputError = "校区公钥由 Veyon 受控密钥目录管理；请在已安装 Veyon 的 Windows 教师端生成配置包。";
                return;
            }
            var platform = await Task.Run(PlatformFacts.Collect);
            if (platform.IsElevated != true)
            {
                PackageOutputError = "生成学生包需要管理员权限来访问 Veyon 密钥目录；请以管理员身份重新启动 App。";
                return;
            }
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(installed.VersionDetail))
            {
                PackageOutputError = $"请先安装并确认 Veyon {VeyonInstallerTrust.Version}，再生成学生校区配置包。\n{installed.AsText()}";
                return;
            }
            MachineNaming.CreateRange(RoomPrefix, "1", "150");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-学生配置包-" + campus)
                : Path.GetFullPath(RoomOutputDir);
            if (Directory.Exists(outDir) || File.Exists(outDir))
            {
                PackageOutputError = "输出目录已存在，为防止覆盖现有密钥，请改路径或先查清原目录内容。";
                return;
            }
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
            InstallerStatus = "正在检查 Veyon 密钥库并仅导出校区配置所需公钥……";
            var keyResult = await Task.Run(() => new VeyonTeacherKeyProvisioner().ExportPublicKey(campus, publicKeyExportPath));
            teacherKeyCreated = keyResult.Created;
            if (!keyResult.Step.Ok)
            {
                PackageOutputError = keyResult.Step.Detail +
                    (teacherKeyCreated ? "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。" : "");
                return;
            }
            using var websiteSigningKey = await Task.Run(() => WebsitePolicySigningKeyStore.GetOrCreate(campus));
            var built = await Task.Run(() => PackageBuilder.Build(outDir, campus, RoomPrefix,
                publicKeyExportPath, websiteSigningKey.PublicKeyPem));
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResult.Step.Detail}\n教师签名私钥保留在当前 Windows 用户证书库；学生配置仅包含校区公钥。请把本配置包与单独下载的学生部署工具配套，通过受信渠道交给学生管理员。";
        }
        catch (Exception exception)
        {
            PackageOutputError = "生成失败：" + exception.Message;
            if (teacherKeyCreated)
                PackageOutputError += "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。";
        }
        finally
        {
            if (temporaryPublicKey is not null)
            {
                try { if (File.Exists(temporaryPublicKey)) File.Delete(temporaryPublicKey); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            EndExclusiveTask();
        }
    }

    public async Task InstallTeacherVeyonAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        TeacherInstallResult = "";
        TeacherInstallIssue = "";
        if (!OperatingSystem.IsWindows())
        {
            TeacherInstallIssue = "教师端 Veyon 安装仅支持 Windows。";
            EndExclusiveTask();
            return;
        }
        InstallerStatus = "正在检查教师端安装状态与权限……";
        try
        {
            var adapter = new WindowsVeyonAdapter();
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status == "installed")
            {
                TeacherInstallIssue = $"检测到本机已安装 Veyon，请勿重复安装。\n{installed.AsText()}";
                return;
            }
            if (installed.Status != VeyonFacts.NotInstalled)
            {
                TeacherInstallIssue = $"无法确认本机 Veyon 安装状态；为避免覆盖未知安装，已停止。\n{installed.AsText()}";
                return;
            }
            var platform = await Task.Run(PlatformFacts.Collect);
            if (platform.IsElevated != true)
            {
                TeacherInstallIssue = "尚未安装：请退出 App，再右键应用选择“以管理员身份运行”，然后重新点击此按钮。";
                return;
            }
            InstallerStatus = "正在从 App 内嵌资源提取并校验 Veyon 安装程序……";
            var acquired = await AcquireInstallerWithProgressAsync();
            if (!acquired.Trust.IsAllowed || !acquired.Trust.AuthenticodeVerified)
            {
                TeacherInstallIssue = "安装器没有通过 Windows SHA-256 与 Authenticode 校验；没有运行安装程序。" + acquired.Trust.Detail;
                return;
            }
            InstallerStatus = "安装器已校验，正在安装教师组件（含 Veyon Master）……";
            var install = await Task.Run(() => adapter.InstallVeyonOnly(acquired.InstallerPath, isTeacher: true));
            if (install.RebootRequired || !install.Ok)
            {
                TeacherInstallIssue = install.Detail + (install.RebootRequired ? " 请重启后重新检查。" : "");
                return;
            }
            var verification = await Task.Run(adapter.VerifyTeacherInstall);
            if (verification.Ok)
                TeacherInstallResult = $"{install.Detail}\n安装读回：{verification.Detail}\n\n教师端 Veyon 已安装。认证密钥、日常教师账户权限和机房电脑目录仍需后续配置。";
            else TeacherInstallIssue = $"{install.Detail}\n安装读回需人工核对：{verification.Detail}";
            InstallerStatus = acquired.ExtractedFromApp
                ? $"已从 App 内嵌资源提取并验证 Veyon {VeyonInstallerTrust.Version}；后续使用本机校验缓存。"
                : $"已复用并验证本机缓存中的 App 内嵌 Veyon {VeyonInstallerTrust.Version}。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + exception.Message; }
        finally { EndExclusiveTask(); }
    }

    private Task<InstallerStoreResult> AcquireInstallerWithProgressAsync()
    {
        var progress = new Progress<InstallerResourceProgress>(value =>
        {
            var percent = value.TotalBytes > 0 ? Math.Clamp(value.BytesReceived * 100 / value.TotalBytes, 0, 100) : 0;
            InstallerStatus = $"正在释放 App 内嵌 Veyon {VeyonInstallerTrust.Version}：{percent}%（{value.BytesReceived / 1024 / 1024} / {value.TotalBytes / 1024 / 1024} MB）";
        });
        return _installerStore.EnsureAvailableAsync(progress);
    }

    private void ClearRoomPreview()
    {
        RoomNames = Array.Empty<string>();
        RoomError = "";
    }

    private bool TryBeginExclusiveTask()
    {
        if (!_lease.TryAcquire(out var denial))
        {
            WebsitePolicyError = denial;
            PackageOutputError = denial;
            TeacherInstallIssue = denial;
            return false;
        }
        IsExecuting = true;
        return true;
    }

    private void EndExclusiveTask()
    {
        _lease.Dispose();
        IsExecuting = false;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
