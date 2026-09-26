using System.Security.Cryptography;

namespace VeyonCampus.Core;

/// <summary>
/// Controlled resource snapshot (AR-03): copies the deployment package's
/// public key into an execution-owned working directory so that the bytes
/// actually consumed by the Veyon CLI are verified bytes. The source package
/// may be replaced between the last digest check and CLI use; only the
/// snapshot, pinned by digest at creation, is handed to execution.
/// </summary>
public interface IResourceSnapshot : IDisposable
{
    /// <summary>Re-verifies the snapshot against its creation-time digest.</summary>
    void VerifyUnchanged();
}

public sealed class PackageResourceSnapshot : IResourceSnapshot
{
    public string WorkingDirectory { get; }
    public string PublicKeyPath { get; }
    public string PublicKeySha256 { get; }

    private bool _disposed;

    private PackageResourceSnapshot(string workingDirectory, string publicKeyPath, string publicKeySha256)
    {
        WorkingDirectory = workingDirectory;
        PublicKeyPath = publicKeyPath;
        PublicKeySha256 = publicKeySha256;
    }

    /// <summary>
    /// Copies and verifies one resource snapshot. Throws when the source
    /// cannot be read or its digest no longer matches the package context.
    /// </summary>
    public static PackageResourceSnapshot Create(string workingRoot, PackageContext package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = Path.Combine(Path.GetFullPath(workingRoot),
            "exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (new FileInfo(package.PublicKeyPath).LinkTarget is not null ||
                (new FileInfo(package.PublicKeyPath).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("公钥文件不能是符号链接或重解析点。");
            var target = Path.Combine(directory, "public-key.pem");
            File.Copy(package.PublicKeyPath, target, overwrite: false);

            using var stream = File.OpenRead(target);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(digest, package.PublicKeySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("公钥快照摘要与部署包记录不一致；没有使用该副本。");

            return new PackageResourceSnapshot(directory, target, digest);
        }
        catch
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Re-verifies the snapshot file against its creation-time digest.</summary>
    public void VerifyUnchanged()
    {
        using var stream = File.OpenRead(PublicKeyPath);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(digest, PublicKeySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("执行期间的公钥副本被修改；停止后续步骤。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(WorkingDirectory))
                Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
