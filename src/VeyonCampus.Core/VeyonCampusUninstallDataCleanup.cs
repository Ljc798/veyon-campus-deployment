using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>Removes only the current user's Veyon Campus data when an app role is uninstalled.</summary>
internal static class VeyonCampusUninstallDataCleanup
{
    private const string ProductDirectoryName = "VeyonCampus";

    internal static void RemoveAllCurrentUserData(string localApplicationData)
    {
        var productRoot = GetProductRoot(localApplicationData);
        VerifyProductRoot(productRoot);
        if (Directory.Exists(productRoot)) DeleteTree(productRoot);
        RemoveTemporaryArtifacts();
    }

    [SupportedOSPlatform("windows")]
    internal static void RemoveAllUserProductData(string currentLocalApplicationData)
    {
        var localRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLocalRoot(localRoots, currentLocalApplicationData);

        using var profiles = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", writable: false)
            ?? throw new IOException("无法读取 Windows 用户配置目录清单；没有清理用户配置。");
        foreach (var sid in profiles.GetSubKeyNames())
        {
            using var profile = profiles.OpenSubKey(sid, writable: false);
            var imagePath = profile?.GetValue("ProfileImagePath", null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(imagePath)) continue;
            imagePath = Environment.ExpandEnvironmentVariables(imagePath.Trim());
            if (imagePath.Contains('%') || !Path.IsPathFullyQualified(imagePath))
                throw new IOException($"用户配置目录无法安全确认（{sid}）；没有继续清理。");

            var profileRoot = Path.GetFullPath(imagePath);
            if (string.Equals(profileRoot, Path.GetPathRoot(profileRoot), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"用户配置目录无效（{sid}）；没有继续清理。");
            AddLocalRoot(localRoots, Path.Combine(profileRoot, "AppData", "Local"));
        }

        foreach (var localRoot in localRoots)
        {
            var productRoot = GetProductRoot(localRoot);
            VerifyProductRoot(productRoot);
            if (Directory.Exists(productRoot)) DeleteTree(productRoot);
        }

        RemoveTemporaryArtifacts();
    }

    internal static void RemoveAllMachineProductData(string commonApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationData);
        var commonRoot = Path.GetFullPath(commonApplicationData);
        if (!Path.IsPathFullyQualified(commonRoot) || Path.GetPathRoot(commonRoot) == commonRoot)
            throw new IOException("系统应用数据目录无法安全确认。");
        var productRoot = Path.GetFullPath(Path.Combine(commonRoot, ProductDirectoryName));
        VerifyProductRoot(productRoot);
        if (Directory.Exists(productRoot)) DeleteTree(productRoot);
    }

    internal static void RemoveDefaultVeyonAuthenticationKeys(string commonApplicationData,
        IEnumerable<string>? veyonInstallationRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationData);
        var commonRoot = Path.GetFullPath(commonApplicationData);
        if (!Path.IsPathFullyQualified(commonRoot) || Path.GetPathRoot(commonRoot) == commonRoot)
            throw new IOException("系统应用数据目录无法安全确认。");
        var roots = new[] { Path.Combine(commonRoot, "Veyon") }
            .Concat(veyonInstallationRoots ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var veyonRoot in roots)
        {
            VerifyProductRoot(veyonRoot);
            if (Directory.Exists(veyonRoot)) DeleteOwnedChildDirectory(veyonRoot, "keys");
        }
    }

    internal static void RemoveStudentData(string localApplicationData, bool preserveTeacherData)
    {
        var productRoot = GetProductRoot(localApplicationData);
        VerifyProductRoot(productRoot);
        if (!preserveTeacherData)
        {
            if (Directory.Exists(productRoot)) DeleteTree(productRoot);
            RemoveTemporaryArtifacts();
            return;
        }
        if (!Directory.Exists(productRoot)) return;
        // Student setup shares this per-user root with the teacher console. The
        // deployment packages, run history, and snapshots are teacher data (and
        // may be in use by a still-installed teacher console), so keep the root.
    }

    internal static void RemoveTeacherData(string localApplicationData, bool preserveStudentData)
    {
        var productRoot = GetProductRoot(localApplicationData);
        VerifyProductRoot(productRoot);
        if (!preserveStudentData)
        {
            if (Directory.Exists(productRoot)) DeleteTree(productRoot);
            RemoveTemporaryArtifacts();
            return;
        }
        if (!Directory.Exists(productRoot)) return;

        foreach (var name in new[]
                 {
                     "Teacher", "StudentAgentTrust", "ApplicationPolicy", "WebsitePolicy", "MobileControl",
                     "deployment-packages", "runs", "installers"
                 })
            DeleteOwnedChildDirectory(productRoot, name);

        DeleteOwnedFile(productRoot, "website-observed-address.txt");
        var telemetry = Path.Combine(productRoot, "Telemetry");
        DeleteOwnedFile(telemetry, "teacher-heartbeat.json");
        DeleteOwnedFile(telemetry, "teacher-heartbeat.json.user-disabled");
        DeleteOwnedFile(telemetry, "operations-reporting.json");
        DeleteEmptyDirectory(telemetry);
        DeleteEmptyDirectory(productRoot);
    }

    internal static void RemoveTemporaryArtifacts()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var productTempRoot = Path.Combine(temporaryRoot, ProductDirectoryName);
        if (Directory.Exists(productTempRoot)) DeleteTree(productTempRoot);

        foreach (var entry in Directory.EnumerateFileSystemEntries(temporaryRoot, "VeyonCampus-*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals("VeyonCampus-setup-cleanup.log", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(name,
                    "^VeyonCampus-(package-verify-[0-9a-f]{32}|public-[0-9a-f]{32}\\.pem)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                if (Directory.Exists(entry)) DeleteTree(entry);
                else DeleteOwnedFile(temporaryRoot, name);
            }
        }
    }

    private static string GetProductRoot(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var localRoot = Path.GetFullPath(localApplicationData);
        if (!Path.IsPathFullyQualified(localRoot) || Path.GetPathRoot(localRoot) == localRoot)
            throw new IOException("当前用户的本地应用数据目录无法安全确认。");
        return Path.GetFullPath(Path.Combine(localRoot, ProductDirectoryName));
    }

    private static void AddLocalRoot(HashSet<string> roots, string localApplicationData)
    {
        var localRoot = Path.GetFullPath(localApplicationData);
        if (!Path.IsPathFullyQualified(localRoot) || Path.GetPathRoot(localRoot) == localRoot)
            throw new IOException("用户本地应用数据目录无法安全确认；没有继续清理。");
        roots.Add(localRoot);
    }

    private static void VerifyProductRoot(string productRoot)
    {
        if (!TryGetAttributes(productRoot, out var attributes)) return;
        RejectPathChain(productRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Veyon Campus 本机数据根路径是重解析点；拒绝递归删除。");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("Veyon Campus 本机数据根路径不是目录；停止清理以避免误删。");
    }

    private static void DeleteOwnedChildDirectory(string productRoot, string name)
    {
        var child = Path.GetFullPath(Path.Combine(productRoot, name));
        if (!string.Equals(Path.GetDirectoryName(child), productRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Veyon Campus 数据清理路径超出本机数据目录。");
        if (!TryGetAttributes(child, out var attributes)) return;
        RejectPathChain(child);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Veyon Campus 数据目录是重解析点；拒绝递归删除。");
        if ((attributes & FileAttributes.Directory) != 0) DeleteTree(child);
        else DeleteOwnedFile(productRoot, name);
    }

    private static void DeleteOwnedFile(string parent, string name)
    {
        var path = Path.GetFullPath(Path.Combine(parent, name));
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Veyon Campus 数据清理文件路径超出本机数据目录。");
        if (!TryGetAttributes(path, out var attributes)) return;
        RejectPathChain(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            throw new IOException("Veyon Campus 清理目标不是普通文件；拒绝删除。");
        File.Delete(path);
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        RejectPathChain(root);
        VerifyTreeHasNoReparsePoints(root);
        Directory.Delete(root, recursive: true);
    }

    private static void RejectPathChain(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            RejectReparsePoint(current);
    }

    private static void VerifyTreeHasNoReparsePoints(string root)
    {
        RejectReparsePoint(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            RejectReparsePoint(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                VerifyTreeHasNoReparsePoints(entry);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Veyon Campus 清理目录包含重解析点；拒绝越界删除。");
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void DeleteEmptyDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        RejectReparsePoint(path);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
}
