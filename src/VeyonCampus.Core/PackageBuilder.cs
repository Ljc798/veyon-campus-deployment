using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// Teacher-side slice of P7-08: generates a self-contained student
/// deployment folder (campus.json, RSA public key, installer resource,
/// manifest.json) from a 4.11.2 installer and a campus name.
/// Never writes a teacher private key or admin.txt into the package.
/// </summary>
public static class PackageBuilder
{
    public static string Build(string outputDirectory, string campus, string computerPrefix,
        string installerSourcePath, int keyBits = 2048)
    {
        if (string.IsNullOrWhiteSpace(campus) || campus.Length > 100 || campus.Any(char.IsControl))
            throw new InvalidDataException("校区名称必须为 1–100 个有效字符。");
        MachineNaming.CreateRange(computerPrefix, "1", "150");
        if (!File.Exists(installerSourcePath))
            throw new FileNotFoundException("安装程序文件不存在。", installerSourcePath);

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        // 1. RSA key pair: public key into the package, private key stays local.
        using var rsa = RSA.Create(keyBits);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var keyFileName = campus + "-public.pem";
        var privatePath = Path.Combine(root, campus + "-private-local-only.pem");
        var publicPath = Path.Combine(root, keyFileName);
        File.WriteAllText(publicPath, publicPem);
        File.WriteAllText(privatePath, privatePem);

        // 2. campus.json with BOM, matching the legacy teacher script format.
        var campusJson = JsonSerializer.Serialize(new
        {
            campus, computerPrefix, keyFile = keyFileName
        });
        var bom = new UTF8Encoding(true);
        var jsonBytes = bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(campusJson)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "campus.json"), jsonBytes);

        // 3. copy the installer into the package.
        var installerDestName = Path.GetFileName(installerSourcePath);
        var installerDest = Path.Combine(root, installerDestName);
        File.Copy(installerSourcePath, installerDest, overwrite: true);

        // 4. manifest.json (schema 1) binding resources to their digests.
        long Size(string p) => new FileInfo(p).Length;
        string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
        var manifest = new
        {
            schemaVersion = 1,
            packageId = Guid.NewGuid().ToString(),
            targetOs = "windows",
            architecture = "x64",
            campus,
            computerPrefix,
            publicKey = new { path = keyFileName, size = Size(publicPath), sha256 = Hash(publicPath) },
            installer = new { path = installerDestName, size = Size(installerDest), sha256 = Hash(installerDest) }
        };
        File.WriteAllText(Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        // 5. README so the folder is self-explaining.
        File.WriteAllText(Path.Combine(root, "README.md"),
            $"# 校区部署包：{campus}\n\n" +
            "由教师端 App 生成。内含 4.11.2 安装程序、校区公钥与 manifest.json。\n" +
            "不包含教师私钥或 admin.txt；学生端 App 读取本文件夹后执行部署。\n");
        return root;
    }
}
