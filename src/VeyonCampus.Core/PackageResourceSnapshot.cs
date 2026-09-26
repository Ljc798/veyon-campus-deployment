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
    public string? WebsitePolicyPublicKeyPath { get; }
    public string? WebsitePolicyPublicKeySha256 { get; }

    private bool _disposed;
    private readonly FileStream _readLease;
    private readonly FileStream? _policyReadLease;

    private PackageResourceSnapshot(string workingDirectory, string publicKeyPath, string publicKeySha256,
        string? websitePolicyPublicKeyPath, string? websitePolicyPublicKeySha256, FileStream readLease,
        FileStream? policyReadLease)
    {
        WorkingDirectory = workingDirectory;
        PublicKeyPath = publicKeyPath;
        PublicKeySha256 = publicKeySha256;
        WebsitePolicyPublicKeyPath = websitePolicyPublicKeyPath;
        WebsitePolicyPublicKeySha256 = websitePolicyPublicKeySha256;
        _readLease = readLease;
        _policyReadLease = policyReadLease;
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

            // Allow CLI readers, but deny writes/deletes on Windows until disposal.
            var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(digest, package.PublicKeySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("公钥快照摘要与部署包记录不一致；没有使用该副本。");
                string? policyTarget = null;
                string? policyDigest = null;
                FileStream? policyStream = null;
                try
                {
                    if (package.WebsitePolicyPublicKeyPath is not null)
                    {
                        var sourcePolicy = new FileInfo(package.WebsitePolicyPublicKeyPath);
                        if (!sourcePolicy.Exists || sourcePolicy.LinkTarget is not null ||
                            (sourcePolicy.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("网站策略公钥文件不存在或不是普通文件。");
                        policyTarget = Path.Combine(directory, "website-policy-public-key.pem");
                        File.Copy(sourcePolicy.FullName, policyTarget, overwrite: false);
                        policyStream = new FileStream(policyTarget, FileMode.Open, FileAccess.Read, FileShare.Read);
                        policyDigest = Convert.ToHexString(SHA256.HashData(policyStream));
                        if (!string.Equals(policyDigest, package.WebsitePolicyPublicKeySha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("网站策略公钥快照摘要与部署包记录不一致；没有使用该副本。");
                    }
                    return new PackageResourceSnapshot(directory, target, digest, policyTarget, policyDigest, stream, policyStream);
                }
                catch
                {
                    policyStream?.Dispose();
                    throw;
                }
            }
            catch { stream.Dispose(); throw; }
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stream = File.OpenRead(PublicKeyPath);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(digest, PublicKeySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("执行期间的公钥副本被修改；停止后续步骤。");
        if (WebsitePolicyPublicKeyPath is not null)
        {
            using var policyStream = File.OpenRead(WebsitePolicyPublicKeyPath);
            var policyDigest = Convert.ToHexString(SHA256.HashData(policyStream));
            if (!string.Equals(policyDigest, WebsitePolicyPublicKeySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("执行期间的网站策略公钥副本被修改；停止后续步骤。");
        }
    }

    /// <summary>The actual CLI import consumes this pinned copy, never the source path.</summary>
    public ProcessOutcome ImportPublicKey(string cliPath, PackageContext package, IProcessLauncher launcher)
    {
        VerifyUnchanged();
        if (!string.Equals(PublicKeySha256, package.PublicKeySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("公钥快照不属于当前计划。");
        return launcher.Run(cliPath,
            ["authkeys", "import", VeyonAuthKeyId.PublicKeyForCampus(package.Campus), PublicKeyPath],
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(WindowsVeyonAdapter.CliTimeoutSeconds));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readLease.Dispose();
        _policyReadLease?.Dispose();
        try
        {
            if (Directory.Exists(WorkingDirectory))
                Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
