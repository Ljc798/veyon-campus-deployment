namespace VeyonCampus.Core;

/// <summary>Maps a selected folder or its manifest/config file to one package root without parsing resources.</summary>
public static class PackageSource
{
    private const int MaximumReferencedFiles = 10;
    private const long MaximumPackageFileBytes = 300L * 1024 * 1024;
    private const long MaximumPackageBytes = MaximumPackageFileBytes + 512 * 1024;

    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("请选择一个部署包文件夹或其中的 manifest.json／campus.json。");

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            PackagePathGuard.EnsureNoReparsePoints(fullPath);
            return fullPath;
        }
        if (!File.Exists(fullPath))
            throw new InvalidDataException("无法访问部署包路径或路径已不存在（可能已移动或权限不足），请检查权限后重新选择。");

        PackagePathGuard.EnsureNoReparsePoints(fullPath);

        var fileName = Path.GetFileName(fullPath);
        if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("campus.json", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(fullPath)!;

        throw new InvalidDataException("请拖入部署包文件夹、manifest.json 或 campus.json；ZIP 暂不支持。");
    }

    public static bool IsCandidate(string? path)
    {
        if (path is null) return false;
        try { Resolve(path); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    /// <summary>
    /// Copies only files referenced by a validated package into an application-owned local directory.
    /// This lets callers safely use a package selected from a network share without relying on that share later.
    /// </summary>
    public static PackageContext CreateLocalSnapshot(string sourcePath, string storageRoot)
    {
        var sourceRoot = Resolve(sourcePath);
        var sourcePackage = PackageContext.Load(sourceRoot);
        var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) sourceFiles.Add(Path.GetFullPath(path));
        }

        var manifestPath = Path.Combine(sourceRoot, "manifest.json");
        var campusPath = Path.Combine(sourceRoot, "campus.json");
        if (File.Exists(manifestPath)) Add(manifestPath);
        if (File.Exists(campusPath)) Add(campusPath);
        Add(sourcePackage.PublicKeyPath);
        Add(sourcePackage.WebsitePolicyPublicKeyPath);
        Add(sourcePackage.ApplicationPolicyPublicKeyPath);
        Add(sourcePackage.StudentSystemPolicyPublicKeyPath);
        Add(sourcePackage.InstallerPath);
        foreach (var payload in sourcePackage.PayloadFiles ?? [])
            Add(Path.Combine(sourceRoot, payload.Path.Replace('/', Path.DirectorySeparatorChar)));

        if (sourceFiles.Count is 0 or > MaximumReferencedFiles)
            throw new InvalidDataException("配置包引用文件数量超出本机导入限制。");

        var root = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(root);
        PackagePathGuard.EnsureNoReparsePoints(root);
        var fingerprint = sourcePackage.PackageFingerprint.ToLowerInvariant();
        var finalRoot = Path.Combine(root, fingerprint);
        if (Directory.Exists(finalRoot))
        {
            try
            {
                PackagePathGuard.EnsureNoReparsePoints(finalRoot);
                var cached = PackageContext.Load(finalRoot);
                if (string.Equals(cached.PackageFingerprint, sourcePackage.PackageFingerprint, StringComparison.Ordinal))
                    return cached;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            { }
            finalRoot += "-" + Guid.NewGuid().ToString("N");
        }

        var stagingRoot = Path.Combine(root, ".import-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            PackagePathGuard.EnsureNoReparsePoints(stagingRoot);
            long totalBytes = 0;
            foreach (var sourceFile in sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                PackagePathGuard.EnsureNoReparsePoints(sourceFile);
                var relative = Path.GetRelativePath(sourceRoot, sourceFile);
                if (Path.IsPathRooted(relative) || relative is "." or ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("配置包引用了包目录以外的文件。");

                var info = new FileInfo(sourceFile);
                var isInstaller = string.Equals(sourceFile, sourcePackage.InstallerPath, StringComparison.OrdinalIgnoreCase);
                var fileLimit = isInstaller ? MaximumPackageFileBytes : 64 * 1024;
                if (!info.Exists || info.Length is < 1 || info.Length > fileLimit ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                    throw new InvalidDataException($"配置包文件为空、过大或不是普通文件：{Path.GetFileName(sourceFile)}");
                totalBytes += info.Length;
                if (totalBytes > MaximumPackageBytes)
                    throw new InvalidDataException("配置包总大小超过本机暂存限制。");

                var targetFile = Path.Combine(stagingRoot, relative);
                var targetDirectory = Path.GetDirectoryName(targetFile)!;
                Directory.CreateDirectory(targetDirectory);
                PackagePathGuard.EnsureNoReparsePoints(targetDirectory);
                using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != info.Length) throw new InvalidDataException("配置包文件在复制前发生变化。");
                using var output = new FileStream(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > info.Length || copied > fileLimit)
                        throw new InvalidDataException("配置包文件在复制时超过大小限制。");
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
                if (copied != info.Length || input.Length != info.Length)
                    throw new InvalidDataException("配置包文件在复制时发生变化。");
            }

            var stagedPackage = PackageContext.Load(stagingRoot);
            if (!string.Equals(stagedPackage.PackageFingerprint, sourcePackage.PackageFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("共享文件夹中的配置包在复制期间发生变化；请检查共享文件后重试。");

            Directory.Move(stagingRoot, finalRoot);
            var localPackage = PackageContext.Load(finalRoot);
            if (!string.Equals(localPackage.PackageFingerprint, sourcePackage.PackageFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("本机暂存后的配置包核对失败；请重新导入。");
            return localPackage;
        }
        catch
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
