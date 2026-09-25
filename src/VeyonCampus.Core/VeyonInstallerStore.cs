using System.Reflection;

namespace VeyonCampus.Core;

public sealed record InstallerResourceProgress(long BytesReceived, long TotalBytes);

public sealed record InstallerStoreResult(
    string InstallerPath,
    bool ExtractedFromApp,
    InstallerTrustResult Trust);

/// <summary>
/// Extracts the one Veyon release asset embedded in this app. The file becomes
/// visible in the local cache only after the pinned size, SHA-256, and (on
/// Windows) Authenticode checks pass. No network access is used.
/// </summary>
public sealed class VeyonInstallerStore
{
    private const int BufferSize = 64 * 1024;
    private const string EmbeddedResourceName = "VeyonCampus.Core.VeyonInstaller.exe";
    private readonly string _cacheDirectory;

    public VeyonInstallerStore(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "installers")
            : Path.GetFullPath(cacheDirectory);
    }

    public async Task<InstallerStoreResult> EnsureAvailableAsync(
        IProgress<InstallerResourceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory))
            throw new InvalidOperationException("无法确定当前用户的本地应用数据目录，不能缓存 Veyon 安装程序。");

        Directory.CreateDirectory(_cacheDirectory);
        var cachedPath = Path.Combine(_cacheDirectory, VeyonInstallerTrust.FileName);
        var cachedTrust = VeyonInstallerTrust.Check(cachedPath);
        if (cachedTrust.IsAllowed && (!OperatingSystem.IsWindows() || cachedTrust.AuthenticodeVerified))
            return new(cachedPath, false, cachedTrust);

        var stagingDirectory = Path.Combine(_cacheDirectory, ".embedded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedPath = Path.Combine(stagingDirectory, VeyonInstallerTrust.FileName);
        try
        {
            await using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
                             ?? throw new InvalidDataException("App 内未找到内嵌的 Veyon 安装器资源。请重新构建完整应用。"))
            await using (var destination = new FileStream(stagedPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > VeyonInstallerTrust.FileSize)
                        throw new InvalidDataException("App 内嵌安装器超过固定大小；已停止并丢弃临时文件。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(received, VeyonInstallerTrust.FileSize));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (received != VeyonInstallerTrust.FileSize)
                    throw new InvalidDataException(
                        $"App 内嵌安装器不完整：期望 {VeyonInstallerTrust.FileSize} 字节，实际读取 {received} 字节。");
            }

            var trust = VeyonInstallerTrust.Check(stagedPath);
            if (!trust.IsAllowed)
                throw new InvalidDataException("App 内嵌文件未通过固定版本信任校验；没有保存到缓存。" + trust.Detail);
            if (OperatingSystem.IsWindows() && !trust.AuthenticodeVerified)
                throw new InvalidDataException("App 内嵌文件未通过 Windows Authenticode 校验；没有保存到缓存。" + trust.Detail);

            File.Move(stagedPath, cachedPath, overwrite: true);
            return new(cachedPath, true, trust);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
