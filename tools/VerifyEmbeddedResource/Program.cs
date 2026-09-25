using System.Reflection;
using System.Security.Cryptography;

if (args.Length != 4 || !long.TryParse(args[1], out var expectedSize) ||
    !System.Text.RegularExpressions.Regex.IsMatch(args[2], "^[0-9A-Fa-f]{64}$"))
{
    Console.Error.WriteLine("Usage: VerifyEmbeddedResource <assembly> <size> <sha256> <resource-name>");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var assembly = Assembly.LoadFile(assemblyPath);
using var resource = assembly.GetManifestResourceStream(args[3]);
if (resource is null)
{
    Console.Error.WriteLine($"Published assembly is missing embedded resource: {args[3]}");
    return 1;
}

var actualHash = Convert.ToHexString(SHA256.HashData(resource));
if (resource.Length != expectedSize || !string.Equals(actualHash, args[2], StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"Embedded resource verification failed: size {resource.Length}, SHA-256 {actualHash}.");
    return 1;
}

Console.WriteLine(
    $"PASS embedded resource {args[3]}: {resource.Length} bytes, SHA-256 {actualHash}");
return 0;
