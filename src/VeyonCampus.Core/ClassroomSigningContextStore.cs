using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Keeps the active session's signing campus separate from the versioned session file.</summary>
public sealed class ClassroomSigningContextStore
{
    private const int MaximumFileBytes = 4096;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private sealed record ContextDocument(int SchemaVersion, Guid SessionId, string CampusId);
    private readonly string _path;

    public ClassroomSigningContextStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-signing-context.json");

    public void Save(Guid sessionId, string campusId)
    {
        ValidateSessionId(sessionId);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var document = new ContextDocument(1, sessionId, campusId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("课堂签名校区上下文超过大小限制。");

        lock (Gate)
        {
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidDataException("课堂签名校区上下文路径无效。");
            Directory.CreateDirectory(directory);
            PathLinkSecurity.RejectLinks(directory);
            if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    public string? Read(Guid sessionId)
    {
        ValidateSessionId(sessionId);
        lock (Gate)
        {
            var document = ReadDocument();
            return document?.SessionId == sessionId ? document.CampusId : null;
        }
    }

    public void Clear(Guid sessionId)
    {
        ValidateSessionId(sessionId);
        lock (Gate)
        {
            var document = ReadDocument();
            if (document?.SessionId != sessionId) return;
            PathLinkSecurity.RejectLinks(_path);
            File.Delete(_path);
        }
    }

    private ContextDocument? ReadDocument()
    {
        if (!File.Exists(_path)) return null;
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("课堂签名校区上下文文件大小无效。");

        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var document = JsonSerializer.Deserialize<ContextDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("课堂签名校区上下文为空。");
            if (document.SchemaVersion != 1 || document.SessionId == Guid.Empty)
                throw new InvalidDataException("课堂签名校区上下文版本或 session ID 无效。");
            WebsitePolicySigningKeyStore.ValidateCampusId(document.CampusId);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("课堂签名校区上下文格式无效；原文件未修改。", exception);
        }
    }

    private static void ValidateSessionId(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂签名校区上下文的 session ID 无效。");
    }
}
