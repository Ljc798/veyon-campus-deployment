using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.App;

/// <summary>Runs the fixed screenshot-only helper in the local Ubuntu ARM64 container.</summary>
internal static class MacVeyonScreenshotCapture
{
    private const string ContainerName = "veyon-campus-helper-arm64";
    private const string ImageTag = "veyon-campus-helper:4.11.4";
    private const string HelperScript = "/usr/local/lib/veyon-helper/capture.py";
    private const int MaximumPngBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan HelperBuildTimeout = TimeSpan.FromMinutes(45);
    private static readonly SemaphoreSlim KeyInjectionGate = new(1, 1);
    private static readonly SemaphoreSlim HelperReadinessGate = new(1, 1);
    private sealed record VerifiedHelper(string CampusId, string ContainerGeneration);
    private static VerifiedHelper? _verifiedHelper;
    private sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr);

    public static async Task<string> GetHelperStatusAsync(string campusId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) return "Veyon ARM64 helper 只用于 Mac 教师端。";
        var dockerPath = ResolveDockerCliPath();
        if (dockerPath is null) return "找不到 Docker CLI；请安装 Docker Desktop。";

        var daemon = await RunDockerAsync(dockerPath, cancellationToken, "info", "--format", "{{.ServerVersion}}")
            .ConfigureAwait(false);
        if (daemon.ExitCode != 0) return "Docker Desktop 尚未启动；启动后可准备本机 Veyon helper。";

        var image = await RunDockerAsync(dockerPath, cancellationToken, "image", "inspect", ImageTag)
            .ConfigureAwait(false);
        if (image.ExitCode != 0) return "Ubuntu ARM64 Veyon helper 尚未构建；点击“准备 Veyon helper”安装。";

        var container = await RunDockerAsync(dockerPath, cancellationToken, "inspect", "--format",
            "{{.State.Running}}", ContainerName).ConfigureAwait(false);
        if (container.ExitCode != 0 || !string.Equals(container.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return "Veyon helper 镜像已就绪，容器尚未运行；点击“准备 Veyon helper”启动。";

        var securityIssue = await GetContainerSecurityIssueAsync(dockerPath, cancellationToken)
            .ConfigureAwait(false);
        if (securityIssue is not null) return securityIssue;

        var version = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
            "/usr/local/bin/veyon-cli", "--version").ConfigureAwait(false);
        if (version.ExitCode != 0 || !version.Stdout.Contains("4.11.4", StringComparison.Ordinal))
            return "Veyon helper 未通过固定版本检查；请重新准备 helper。";

        var authenticationMethod = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
            "/usr/local/bin/veyon-cli", "config", "get", "Authentication/Method").ConfigureAwait(false);
        if (authenticationMethod.ExitCode != 0 || authenticationMethod.Stdout.Trim() != "1")
            return "Veyon helper 未启用密钥文件认证；请点击“准备 Veyon helper”重新配置。";

        var keys = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
            "/usr/local/bin/veyon-cli", "authkeys", "list").ConfigureAwait(false);
        if (keys.ExitCode != 0) return "Veyon helper 运行中，但无法读取 Veyon 密钥状态。";
        var entries = VeyonTeacherKeyProvisioner.ParseListingEntries(keys.Stdout);
        if (entries is null) return "Veyon helper 返回了无法识别的密钥清单；请先核对密钥配置。";

        string keyId;
        try { keyId = VeyonAuthKeyId.ForCampus(campusId); }
        catch (ArgumentException) { return "请先完成校区设置，再检查 Veyon helper 密钥。"; }
        try
        {
            await RestoreVeyonKeyToHelperAsync(campusId, cancellationToken).ConfigureAwait(false);
            var refreshed = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
                "/usr/local/bin/veyon-cli", "authkeys", "list").ConfigureAwait(false);
            if (refreshed.ExitCode == 0)
                entries = VeyonTeacherKeyProvisioner.ParseListingEntries(refreshed.Stdout) ?? entries;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
                                           InvalidOperationException or PlatformNotSupportedException)
        {
            return "无法从 Mac 钥匙串恢复 Veyon 私钥到 helper 临时目录：" + exception.Message;
        }
        if (!entries.Contains(keyId + "/private", StringComparer.OrdinalIgnoreCase))
            return "Veyon helper 已运行，但缺少当前校区教师私钥。请先安全导入与学生配置包公钥对应的私钥。";

        return "Veyon helper 本机组件已就绪；校园网连通性和真实学生机截图仍待现场确认。";
    }

    public static async Task<string> PrepareHelperAsync(string campusId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Veyon ARM64 helper 只用于 Mac 教师端。");
        var dockerPath = ResolveDockerCliPath() ??
            throw new InvalidOperationException("找不到 Docker CLI；请先安装 Docker Desktop。");
        var daemon = await RunDockerAsync(dockerPath, cancellationToken, "info", "--format", "{{.ServerVersion}}")
            .ConfigureAwait(false);
        if (daemon.ExitCode != 0)
            throw new InvalidOperationException("Docker Desktop 尚未启动；请启动 Docker Desktop 后重试。");

        var scripts = ResolveHelperScriptDirectory() ??
            throw new InvalidOperationException("此应用包缺少 scripts/veyon-helper；请重新生成 Mac 教师端应用包。");
        var image = await RunDockerAsync(dockerPath, cancellationToken, "image", "inspect", ImageTag)
            .ConfigureAwait(false);
        if (image.ExitCode != 0)
        {
            var build = await RunHelperScriptAsync(scripts, "build.sh", dockerPath, HelperBuildTimeout,
                cancellationToken).ConfigureAwait(false);
            if (build.ExitCode != 0)
                throw new InvalidOperationException($"Veyon helper 镜像构建失败（退出码 {build.ExitCode}）：" +
                    DescribeScriptFailure(build, "请检查网络和 Docker 磁盘空间后重试。"));
        }

        var start = await RunHelperScriptAsync(scripts, "start.sh", dockerPath, CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
            throw new InvalidOperationException($"Veyon helper 启动失败（退出码 {start.ExitCode}）：" +
                DescribeScriptFailure(start, "请检查 Docker Desktop 状态后重试。"));
        await RestoreVeyonKeyToHelperAsync(campusId, cancellationToken).ConfigureAwait(false);
        return await GetHelperStatusAsync(campusId, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> RecoverExistingHelperAsync(string campusId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) return "Veyon ARM64 helper 只用于 Mac 教师端。";
        var dockerPath = ResolveDockerCliPath();
        if (dockerPath is null) return "找不到 Docker CLI；请安装 Docker Desktop。";
        var daemon = await RunDockerAsync(dockerPath, cancellationToken, "info", "--format", "{{.ServerVersion}}")
            .ConfigureAwait(false);
        if (daemon.ExitCode != 0)
            return "Docker Desktop 尚未启动；启动后可准备本机 Veyon helper。";
        var image = await RunDockerAsync(dockerPath, cancellationToken, "image", "inspect", ImageTag)
            .ConfigureAwait(false);
        if (image.ExitCode != 0)
            return await GetHelperStatusAsync(campusId, cancellationToken).ConfigureAwait(false);

        var scripts = ResolveHelperScriptDirectory();
        if (scripts is null) return "此应用包缺少 Veyon helper 启动脚本；请重新生成 Mac 教师端应用包。";
        var start = await RunHelperScriptAsync(scripts, "start.sh", dockerPath, CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
            return "无法重新应用 Veyon helper 的密钥认证设置：" +
                   DescribeScriptFailure(start, "请在设备健康页重新准备 helper。");
        await RestoreVeyonKeyToHelperAsync(campusId, cancellationToken).ConfigureAwait(false);
        return await GetHelperStatusAsync(campusId, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> RestoreVeyonKeyToHelperAsync(string campusId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() || !VeyonAuthenticationKeyStore.HasPrivateKey(campusId)) return false;
        var dockerPath = ResolveDockerCliPath();
        if (dockerPath is null) return false;
        await KeyInjectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? privateKeyBytes = null;
        string? helperImportPath = null;
        try
        {
            var state = await RunDockerAsync(dockerPath, cancellationToken, "inspect", "--format",
                "{{.State.Running}}", ContainerName).ConfigureAwait(false);
            if (state.ExitCode != 0 || !string.Equals(state.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return false;

            var securityIssue = await GetContainerSecurityIssueAsync(dockerPath, cancellationToken)
                .ConfigureAwait(false);
            if (securityIssue is not null) throw new InvalidOperationException(securityIssue);

            var keyId = VeyonAuthKeyId.ForCampus(campusId);
            var listing = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
                "/usr/local/bin/veyon-cli", "authkeys", "list").ConfigureAwait(false);
            if (listing.ExitCode != 0)
                throw new InvalidOperationException("无法读取 helper 中的 Veyon 密钥清单；没有注入私钥。");
            var entries = VeyonTeacherKeyProvisioner.ParseListingEntries(listing.Stdout);
            if (entries is null)
                throw new InvalidDataException("helper 返回了无法识别的 Veyon 密钥清单；没有注入私钥。");
            if (entries.Contains(keyId + "/private", StringComparer.OrdinalIgnoreCase)) return true;

            privateKeyBytes = VeyonAuthenticationKeyStore.ExportPrivateKeyPemBytes(campusId);
            helperImportPath = "/run/veyon-helper/import/" + Guid.NewGuid().ToString("N") + ".pem";
            var writer = new ProcessStartInfo
            {
                FileName = dockerPath,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         "exec", "-i", ContainerName, "/usr/bin/python3", "-c",
                         "import os,sys,shutil; p=sys.argv[1]; os.makedirs(os.path.dirname(p),mode=0o700,exist_ok=True); fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600); f=os.fdopen(fd,'wb'); shutil.copyfileobj(sys.stdin.buffer,f); f.flush(); f.close()",
                         helperImportPath
                     })
                writer.ArgumentList.Add(argument);
            var written = await RunProcessAsync(writer, CommandTimeout, cancellationToken, privateKeyBytes)
                .ConfigureAwait(false);
            if (written.ExitCode != 0)
                throw new IOException("无法将 Veyon 私钥写入 helper 的内存临时目录。");

            var imported = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
                "/usr/local/bin/veyon-cli", "authkeys", "import", keyId + "/private", helperImportPath)
                .ConfigureAwait(false);
            if (imported.ExitCode != 0)
                throw new IOException("Veyon helper 未能导入钥匙串私钥；临时 PEM 随后会删除。");

            var readBack = await RunDockerAsync(dockerPath, cancellationToken, "exec", ContainerName,
                "/usr/local/bin/veyon-cli", "authkeys", "list").ConfigureAwait(false);
            var readBackEntries = readBack.ExitCode == 0
                ? VeyonTeacherKeyProvisioner.ParseListingEntries(readBack.Stdout)
                : null;
            if (readBackEntries is null || !readBackEntries.Contains(keyId + "/private", StringComparer.OrdinalIgnoreCase))
                throw new IOException("Veyon helper 导入后没有读回当前校区私钥。");
            return true;
        }
        finally
        {
            if (privateKeyBytes is not null) CryptographicOperations.ZeroMemory(privateKeyBytes);
            if (helperImportPath is not null)
            {
                try
                {
                    _ = await RunDockerAsync(dockerPath, CancellationToken.None, "exec", ContainerName,
                        "/bin/rm", "-f", helperImportPath).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                                   OperationCanceledException or System.ComponentModel.Win32Exception) { }
            }
            KeyInjectionGate.Release();
        }
    }

    public static async Task<byte[]> CaptureAsync(string target, CancellationToken cancellationToken)
        => await CaptureAsync(string.Empty, target, cancellationToken).ConfigureAwait(false);

    public static async Task<byte[]> CaptureAsync(string campusId, string target,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Ubuntu ARM64 Veyon helper 只用于 Mac 教师端。");

        var normalizedTarget = VeyonHostAddress.NormalizeOverride(target);
        if (normalizedTarget.Length == 0)
            throw new InvalidDataException("屏幕预览目标无效。");

        var dockerPath = ResolveDockerCliPath();
        if (dockerPath is null)
            throw new InvalidOperationException("找不到 Docker CLI；请安装并启动 Docker Desktop，再启动 Veyon helper。");

        var containerGeneration = await EnsureContainerRunningAsync(dockerPath, cancellationToken)
            .ConfigureAwait(false);
        var status = containerGeneration is null
            ? await GetHelperStatusAsync(campusId, cancellationToken).ConfigureAwait(false)
            : await EnsureHelperReadyAsync(campusId, containerGeneration, cancellationToken).ConfigureAwait(false);
        if (!status.StartsWith("Veyon helper 本机组件已就绪", StringComparison.Ordinal))
            throw new InvalidOperationException(status);
        var authKeyName = VeyonAuthKeyId.ForCampus(campusId);

        var startInfo = new ProcessStartInfo
        {
            FileName = dockerPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("VEYON_AUTH_KEY_NAME=" + authKeyName);
        startInfo.ArgumentList.Add(ContainerName);
        startInfo.ArgumentList.Add("/usr/bin/python3");
        startInfo.ArgumentList.Add(HelperScript);
        startInfo.ArgumentList.Add(normalizedTarget);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动本机 Veyon helper 请求。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动 Docker CLI；请确认 Docker Desktop 已安装。", exception);
        }

        var stdoutTask = ReadBoundedBytesAsync(process.StandardOutput.BaseStream, MaximumPngBytes,
            CancellationToken.None);
        var stderrTask = ReadBoundedTextAsync(process.StandardError.BaseStream, 4096,
            CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new TimeoutException("等待 Veyon helper 返回屏幕画面超时；已停止本次采集。");
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var png = await stdoutTask.ConfigureAwait(false);
        var detail = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            if (detail.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("is not running", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Veyon helper 尚未启动；请先运行 scripts/veyon-helper/start.sh。");
            if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) &&
                detail.Contains(HelperScript, StringComparison.Ordinal))
                throw new InvalidOperationException("Veyon helper 缺少截图脚本；请运行 scripts/veyon-helper/start.sh 更新 helper。");
            throw new InvalidOperationException(Truncate(detail, 600, "Veyon helper 未能取得学生屏幕画面。"));
        }

        if (png.Length == 0)
            throw new InvalidDataException("Veyon helper 没有返回屏幕画面。");
        return ClassroomScreenPreviewCapture.VeyonCliScreenshotCapture.EncodePngToMasterJpeg(png);
    }

    private static async Task<ProcessOutput> RunDockerAsync(string dockerPath, CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return await RunProcessAsync(startInfo, CommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> EnsureContainerRunningAsync(string dockerPath,
        CancellationToken cancellationToken)
    {
        var container = await RunDockerAsync(dockerPath, cancellationToken, "inspect", "--format",
            "{{.State.Running}}|{{.Id}}|{{.State.StartedAt}}", ContainerName).ConfigureAwait(false);
        if (container.ExitCode == 0 && TryReadRunningContainerGeneration(container.Stdout, out var generation))
        {
            var securityIssue = await GetContainerSecurityIssueAsync(dockerPath, cancellationToken)
                .ConfigureAwait(false);
            if (securityIssue is not null) throw new InvalidOperationException(securityIssue);
            return generation;
        }

        var image = await RunDockerAsync(dockerPath, cancellationToken, "image", "inspect", ImageTag)
            .ConfigureAwait(false);
        if (image.ExitCode != 0) return null;

        var scripts = ResolveHelperScriptDirectory() ??
            throw new InvalidOperationException("此应用包缺少 Veyon helper 启动脚本；请重新生成 Mac 教师端应用包。");
        var start = await RunHelperScriptAsync(scripts, "start.sh", dockerPath, CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
            throw new InvalidOperationException("Veyon helper 容器未能自动启动；请在设备健康页重新准备 helper。");

        var started = await RunDockerAsync(dockerPath, cancellationToken, "inspect", "--format",
            "{{.State.Running}}|{{.Id}}|{{.State.StartedAt}}", ContainerName).ConfigureAwait(false);
        if (started.ExitCode != 0 || !TryReadRunningContainerGeneration(started.Stdout, out generation))
            throw new InvalidOperationException("Veyon helper 启动后仍未处于运行状态；请在设备健康页重新准备 helper。");
        var startedSecurityIssue = await GetContainerSecurityIssueAsync(dockerPath, cancellationToken)
            .ConfigureAwait(false);
        if (startedSecurityIssue is not null) throw new InvalidOperationException(startedSecurityIssue);
        return generation;
    }

    private static async Task<string?> GetContainerSecurityIssueAsync(string dockerPath,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(dockerPath, cancellationToken, "inspect", "--format",
            "{{.HostConfig.Privileged}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.PublishAllPorts}}|" +
            "{{json .HostConfig.PortBindings}}|{{json .HostConfig.Tmpfs}}|" +
            "{{json .HostConfig.Binds}}|{{json .HostConfig.Mounts}}|" +
            "{{json .HostConfig.VolumesFrom}}|{{json .Mounts}}", ContainerName)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            return "无法核验 Veyon helper 的容器隔离配置；为保护教师机，已停止截图。";

        var fields = result.Stdout.Trim().Split('|', 9);
        if (fields.Length != 9)
            return "Veyon helper 返回的容器隔离配置无法识别；已停止截图。";
        if (!string.Equals(fields[0], "false", StringComparison.OrdinalIgnoreCase))
            return "Veyon helper 容器启用了特权模式；已停止截图，请重新准备 helper。";
        if (!string.Equals(fields[1], "bridge", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fields[2], "false", StringComparison.OrdinalIgnoreCase) ||
            !HasNoPublishedPorts(fields[3]))
            return "Veyon helper 容器网络配置不符合要求，或发布了端口；已停止截图，请重新准备 helper。";
        if (!HasNoHostMounts(fields[5], fields[6], fields[7], fields[8]))
            return "Veyon helper 容器映射了宿主机目录或卷；为保护教师机密钥，已停止截图，请重新准备 helper。";
        if (!HasExpectedHelperTmpfs(fields[4]))
            return "Veyon helper 的密钥目录未使用预期的私有 tmpfs；已停止截图，请重新准备 helper。";
        return null;
    }

    private static bool HasNoHostMounts(params string[] jsonValues)
    {
        foreach (var json in jsonValues)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Null) continue;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 0) return false;
            }
            catch (JsonException) { return false; }
        }
        return true;
    }

    private static bool HasNoPublishedPorts(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Null ||
                   root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any();
        }
        catch (JsonException) { return false; }
    }

    private static bool HasExpectedHelperTmpfs(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("/run/veyon-helper", out var options) ||
                options.ValueKind != JsonValueKind.String)
                return false;

            var actual = (options.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return actual.SetEquals(["rw", "nosuid", "nodev", "noexec", "size=96m"]);
        }
        catch (JsonException) { return false; }
    }

    private static async Task<string> EnsureHelperReadyAsync(string campusId, string containerGeneration,
        CancellationToken cancellationToken)
    {
        // The helper key lives in container tmpfs. Verify helper/key state once per container lifetime;
        // the per-frame path only inspects the container generation before running the screenshot.
        await HelperReadinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verifiedHelper is { } verified && verified.CampusId == campusId &&
                verified.ContainerGeneration == containerGeneration)
                return "Veyon helper 本机组件已就绪。";

            var status = await GetHelperStatusAsync(campusId, cancellationToken).ConfigureAwait(false);
            if (status.StartsWith("Veyon helper 本机组件已就绪", StringComparison.Ordinal))
                _verifiedHelper = new VerifiedHelper(campusId, containerGeneration);
            else
                _verifiedHelper = null;
            return status;
        }
        finally
        {
            HelperReadinessGate.Release();
        }
    }

    private static bool TryReadRunningContainerGeneration(string output, out string generation)
    {
        generation = "";
        var fields = output.Trim().Split('|', 3);
        if (fields.Length != 3 || !string.Equals(fields[0], "true", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(fields[1]) || string.IsNullOrWhiteSpace(fields[2]))
            return false;
        // StartedAt changes when Docker restarts the tmpfs-backed container, whose ID stays the same.
        generation = fields[1] + "|" + fields[2];
        return true;
    }

    private static async Task<ProcessOutput> RunHelperScriptAsync(string scripts, string scriptName,
        string dockerPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(scripts, scriptName);
        if (!File.Exists(scriptPath))
            throw new InvalidOperationException($"Mac 应用包缺少 {scriptName}；请重新生成应用包。");
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = scripts,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["PATH"] = Path.GetDirectoryName(dockerPath) + Path.PathSeparator +
                                         (Environment.GetEnvironmentVariable("PATH") ?? "");
        startInfo.Environment["VEYON_HELPER_IMAGE"] = ImageTag;
        startInfo.Environment["VEYON_HELPER_CONTAINER"] = ContainerName;
        return await RunProcessAsync(startInfo, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessOutput> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout,
        CancellationToken cancellationToken, byte[]? stdinBytes = null)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动本机 helper 管理命令。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动本机 helper 管理命令。", exception);
        }

        var stdoutTask = ReadBoundedTextAsync(process.StandardOutput.BaseStream, 16 * 1024, CancellationToken.None);
        var stderrTask = ReadBoundedTextAsync(process.StandardError.BaseStream, 16 * 1024, CancellationToken.None);
        try
        {
            if (stdinBytes is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdinBytes, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveTextReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveTextReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessOutput(process.ExitCode, stdout, stderr);
    }

    private static string? ResolveHelperScriptDirectory()
    {
        var candidates = new List<string>();
        var overridePath = Environment.GetEnvironmentVariable("VEYONCAMPUS_HELPER_SCRIPT_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath)) candidates.Add(overridePath);
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "veyon-helper")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "veyon-helper"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "scripts", "veyon-helper"));
        return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "start.sh")) &&
                                                       File.Exists(Path.Combine(candidate, "build.sh")));
    }

    private static string? ResolveDockerCliPath()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "docker"))
            .Concat([
                "/opt/homebrew/bin/docker",
                "/usr/local/bin/docker",
                "/Applications/Docker.app/Contents/Resources/bin/docker"
            ]);
        return candidates.FirstOrDefault(path => File.Exists(path) && !Directory.Exists(path));
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("Veyon helper 返回的 PNG 超过大小限制。");
            output.Write(buffer, 0, count);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024));
        var buffer = new byte[1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maximumBytes - (int)output.Length;
            if (remaining > 0) output.Write(buffer, 0, Math.Min(remaining, count));
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task ObserveReadersAsync(Task<byte[]> stdoutTask, Task<string> stderrTask)
    {
        try { await stdoutTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           ObjectDisposedException or OperationCanceledException) { }
        try { await stderrTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or
                                           OperationCanceledException) { }
    }

    private static async Task ObserveTextReadersAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try { await stdoutTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or
                                           OperationCanceledException) { }
        try { await stderrTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or
                                           OperationCanceledException) { }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
    }

    private static string Truncate(string value, int maximumLength, string fallback)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[^maximumLength..];
    }

    private static string DescribeScriptFailure(ProcessOutput result, string fallback)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        return Truncate(detail, 600, fallback);
    }
}
