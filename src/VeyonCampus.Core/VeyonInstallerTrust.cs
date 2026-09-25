using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VeyonCampus.Core;

public sealed record InstallerTrustResult(
    bool IsAllowed,
    bool HashMatched,
    bool AuthenticodeVerified,
    string Detail);

/// <summary>Trust policy for the single Veyon installer version tested by this app.</summary>
public static class VeyonInstallerTrust
{
    public const string Version = "4.11.2.0";
    public const string FileName = "veyon-4.11.2.0-win64-setup.exe";
    public const long FileSize = 16_782_856;
    public const string Sha256 = "7EC3F0689F995FE7C79D3F5B106E85DADE089AB24F534E92A168543197F0E428";
    public const string ReleaseAssetUrl = "https://github.com/veyon/veyon/releases/download/v4.11.2/veyon-4.11.2.0-win64-setup.exe";
    public const string Publisher = "Veyon Solutions";
    public const string PublisherCertificateSha256 = "7587C97686BF912155F3C43713B87E7F800890D804AC1EFE479E0744A62C5619";

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool MatchesPinnedArtifact(string fileName, long size, string sha256) =>
        string.Equals(Path.GetFileName(fileName), FileName, StringComparison.OrdinalIgnoreCase) &&
        size == FileSize &&
        string.Equals(sha256, Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the official release digest everywhere. On Windows, also requires
    /// WinVerifyTrust success and the pinned Veyon Solutions signing certificate.
    /// Non-Windows hosts may prepare a package from the exact release asset, but the
    /// target Windows machine must repeat Authenticode verification before execution.
    /// </summary>
    public static InstallerTrustResult Check(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return Reject("找不到 Veyon 4.11.2 安装程序文件。");

        try
        {
            var info = new FileInfo(installerPath);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return Reject("安装程序不能是符号链接或重解析点。");
            if (!string.Equals(info.Name, FileName, StringComparison.OrdinalIgnoreCase))
                return Reject($"安装程序文件名必须为 {FileName}。");
            if (info.Length != FileSize)
                return Reject($"安装程序大小不匹配：期望 {FileSize} 字节，实际 {info.Length} 字节。");

            using var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!MatchesPinnedArtifact(info.Name, info.Length, actualHash))
                return Reject($"安装程序 SHA-256 不匹配官方 Veyon {Version} 发布文件（实际 {actualHash}）。");

            if (!OperatingSystem.IsWindows())
                return new(true, true, false,
                    $"Veyon {Version} 官方发布文件名、大小和 SHA-256 均匹配；当前系统不能验证 Windows Authenticode。Windows 学生端会在运行前再次验证发布者签名。SHA-256：{Sha256}。");

            var signature = VerifyWindowsAuthenticode(installerPath);
            if (!signature.IsTrusted)
                return new(false, true, false, signature.Detail);
            return new(true, true, true,
                $"Veyon {Version} 官方发布文件 SHA-256 匹配，Windows Authenticode 信任验证通过；签名证书为 {Publisher}（SHA-256 指纹 {PublisherCertificateSha256}）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or
                                   ArgumentException or InvalidOperationException or MarshalDirectiveException or
                                   DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
            return Reject($"读取或验证 Veyon 安装程序失败：{ex.Message}");
        }
    }

    private static InstallerTrustResult Reject(string detail) => new(false, false, false, detail);

    [SupportedOSPlatform("windows")]
    private static (bool IsTrusted, string Detail) VerifyWindowsAuthenticode(string installerPath)
    {
        var fileName = Marshal.StringToHGlobalUni(Path.GetFullPath(installerPath));
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = fileName,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = WtdRevokeNone,
            dwUnionChoice = WtdChoiceFile,
            pFile = fileInfoPointer,
            dwStateAction = WtdStateActionVerify,
            // The release SHA-256 and leaf certificate are pinned. Revocation retrieval
            // is disabled so installation remains possible in an offline classroom.
            dwProvFlags = WtdRevocationCheckNone
        };
        var action = GenericVerifyV2;
        var verifyCalled = false;
        int status;
        try
        {
            verifyCalled = true;
            status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            try
            {
                if (verifyCalled)
                {
                    data.dwStateAction = WtdStateActionClose;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                // Preserve the verification result while still releasing unmanaged memory.
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeHGlobal(fileName);
            }
        }

        if (status != 0)
            return (false, $"Windows Authenticode 信任验证失败（WinVerifyTrust=0x{status:X8}）；禁止执行安装器。");

#pragma warning disable SYSLIB0057 // PE Authenticode signer extraction has no X509CertificateLoader equivalent.
        using var embeddedCertificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
#pragma warning restore SYSLIB0057
        var actualCertificateSha256 = Convert.ToHexString(SHA256.HashData(embeddedCertificate.RawData));
        if (!string.Equals(actualCertificateSha256, PublisherCertificateSha256, StringComparison.Ordinal))
            return (false,
                $"安装器签名虽通过 Windows 信任验证，但发布者证书不符合固定基线；预期 SHA-256 {PublisherCertificateSha256}，实际 {actualCertificateSha256}。");
        if (!embeddedCertificate.Subject.Contains(Publisher, StringComparison.OrdinalIgnoreCase))
            return (false, $"签名证书发布者名称不匹配固定基线：{embeddedCertificate.Subject}。");
        return (true, "Authenticode signature verified.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);
}
