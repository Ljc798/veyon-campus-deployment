using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record VeyonNetworkLocation(string Name, IReadOnlyList<string> Targets)
{
    public string DisplayName => $"{Name}（{Targets.Count} 台）";
}

public sealed record VeyonNetworkComputer(string ComputerName, string Host, string? StudentName = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(StudentName)
        ? ComputerName
        : StudentName.Trim();
}

public sealed record VeyonNetworkObject(string Type, string Name, string Host, string Mac, string Location);
public sealed record VeyonLocationImportResult(string LocationName, int ComputerCount, int NamedStudentCount,
    int AddedComputerCount, int SkippedComputerCount, bool LocationCreated);
public sealed record VeyonNetworkImportPreview(string LocationName, bool LocationExists,
    IReadOnlyList<VeyonNetworkComputer> ComputersToAdd, IReadOnlyList<string> SkippedComputers,
    IReadOnlyList<string> Conflicts);

/// <summary>Reads the local Veyon built-in network object directory without changing it.</summary>
public static class VeyonNetworkObjectDirectory
{
    // Veyon's 4.11.2 format importer is a placeholder-to-regex parser, not an RFC CSV decoder.
    // Unit Separator cannot occur in accepted fields (control characters are rejected), so
    // semicolons, quotes and Chinese text remain unambiguous without CSV quote escaping.
    private const char FieldSeparator = '\u001f';
    private const string ExportFormat = "%name%\u001f%host%\u001f%location%";
    private const string FullExportFormat = "%type%\u001f%name%\u001f%host%\u001f%mac%\u001f%location%";
    private const string ImportFormat = "%name%\u001f%host%\u001f%mac%";

    /// <summary>Formats one safe custom-delimited computer row for Veyon's placeholder importer.</summary>
    public static string FormatComputerImportRecord(VeyonNetworkComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ValidateDirectoryName(computer.DisplayName, "电脑显示名");
        _ = VeyonHostAddress.NormalizeOverride(computer.Host);
        return string.Join(FieldSeparator.ToString(), computer.DisplayName, computer.Host, "");
    }

    /// <summary>Parses a row written with the custom delimiter used by Veyon's export format.</summary>
    public static IReadOnlyList<string> ParseExportRecord(string line, int expectedFieldCount = 3) =>
        Array.AsReadOnly(ParseExportRow(line, expectedFieldCount));

    public static IReadOnlyList<VeyonNetworkLocation> ReadLocations()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("读取 Veyon 机房列表仅支持 Windows 教师端。");

        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath()
                      ?? throw new InvalidOperationException("找不到 Veyon CLI；请先安装教师端 Veyon。");
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-targets-{Guid.NewGuid():N}.csv");

        try
        {
            var runner = new ProcessRunner();
            runner.Run(cliPath,
                ["networkobjects", "export", temporaryPath, "format", ExportFormat],
                Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(45), outputLimitChars: 1024);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException($"Veyon 导出电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）。");
            if (!File.Exists(temporaryPath))
                throw new InvalidDataException("Veyon 没有生成电脑目录导出文件。");

            var byLocation = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(temporaryPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = ParseExportRow(line);
                var computerName = fields[0].Trim();
                var host = fields[1].Trim();
                var location = fields[2].Trim();
                var target = host.Length > 0 ? host : computerName;
                if (target.Length == 0) continue;

                if (location.Length == 0) location = "未分组电脑";
                if (!byLocation.TryGetValue(location, out var targets))
                    byLocation.Add(location, targets = []);
                targets.Add(target);
            }

            var locations = byLocation
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new VeyonNetworkLocation(pair.Key,
                    Array.AsReadOnly(pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())))
                .Where(location => location.Targets.Count > 0)
                .ToArray();
            return Array.AsReadOnly(locations);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads the current Veyon directory and previews location/host conflicts without writing.</summary>
    public static IReadOnlyList<string> ReadImportConflicts(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("检查 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);
        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath()
                      ?? throw new InvalidOperationException("找不到 Veyon CLI；请先安装教师端 Veyon。");
        return BuildImportPreview(locationName, computers,
            ReadDirectoryObjects(cliPath, Path.GetDirectoryName(cliPath)!)).Conflicts;
    }

    /// <summary>Reads current Veyon objects and previews additions, preserved duplicates and hard conflicts.</summary>
    public static VeyonNetworkImportPreview ReadImportPreview(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("检查 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);
        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath()
                      ?? throw new InvalidOperationException("找不到 Veyon CLI；请先安装教师端 Veyon。");
        return BuildImportPreview(locationName, computers,
            ReadDirectoryObjects(cliPath, Path.GetDirectoryName(cliPath)!));
    }

    /// <summary>Pure conflict check used by the UI and regression fixtures.</summary>
    public static IReadOnlyList<string> FindImportConflicts(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers, IReadOnlyList<VeyonNetworkObject> existing)
    {
        return BuildImportPreview(locationName, computers, existing).Conflicts;
    }

    /// <summary>Pure import diff used by the UI and regression fixtures.</summary>
    public static VeyonNetworkImportPreview BuildImportPreview(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers, IReadOnlyList<VeyonNetworkObject> existing)
    {
        ValidateImportPlan(locationName, computers);
        ArgumentNullException.ThrowIfNull(existing);
        var conflicts = new List<string>();
        var existingLocations = existing.Where(entry => entry.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            entry.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var locationExists = existingLocations.Length > 0 || existing.Any(entry =>
            entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase));
        if (existingLocations.Length > 1)
            conflicts.Add($"Veyon 中有多个同名地点“{locationName}”，无法安全选择目标。");

        var existingComputers = existing
            .Where(entry => entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
        var existingHosts = existingComputers.Select(entry => entry.Host.Length > 0 ? entry.Host : entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNames = existingComputers.Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<VeyonNetworkComputer>();
        var skipped = new List<string>();
        foreach (var computer in computers)
        {
            if (!plannedNames.Add(computer.ComputerName))
                conflicts.Add($"电脑编号“{computer.ComputerName}”在本次清单中重复。");
            if (!string.Equals(computer.DisplayName, computer.ComputerName, StringComparison.OrdinalIgnoreCase) &&
                !plannedNames.Add(computer.DisplayName))
                conflicts.Add($"显示名称“{computer.DisplayName}”在本次清单中重复。");
            if (!plannedHosts.Add(computer.Host))
                conflicts.Add($"主机名/IP“{computer.Host}”在本次清单中重复。");
            var sameHost = existingHosts.Contains(computer.Host);
            var sameName = existingNames.Contains(computer.DisplayName);
            if (sameHost || sameName)
            {
                var reason = sameHost && sameName ? "主机/IP 与显示名均已存在" :
                    sameHost ? "主机/IP 已存在" : "显示名已存在";
                skipped.Add($"{computer.ComputerName} → {computer.Host}：{reason}，保留已有项。");
            }
            else toAdd.Add(computer);
        }
        return new VeyonNetworkImportPreview(locationName, locationExists,
            toAdd.AsReadOnly(), skipped.AsReadOnly(), conflicts.AsReadOnly());
    }

    /// <summary>Reuses an existing location, preserves duplicate objects and imports only new computers.</summary>
    public static VeyonLocationImportResult AddLocation(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("写入 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateImportPlan(locationName, computers);

        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath()
                      ?? throw new InvalidOperationException("找不到 Veyon CLI；请先安装教师端 Veyon。");
        var workingDirectory = Path.GetDirectoryName(cliPath)!;
        var current = ReadDirectoryObjects(cliPath, workingDirectory);
        var preview = BuildImportPreview(locationName, computers, current);
        if (preview.Conflicts.Count > 0)
            throw new InvalidDataException("添加前冲突检查发现问题，未写入任何内容：" + string.Join("、", preview.Conflicts));
        if (preview.ComputersToAdd.Count == 0)
            return new VeyonLocationImportResult(locationName, computers.Count,
                computers.Count(computer => !string.IsNullOrWhiteSpace(computer.StudentName)),
                AddedComputerCount: 0, SkippedComputerCount: preview.SkippedComputers.Count,
                LocationCreated: false);

        var importPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-room-{Guid.NewGuid():N}.txt");
        var locationCreated = false;
        try
        {
            var importText = string.Join(Environment.NewLine,
                preview.ComputersToAdd.Select(FormatComputerImportRecord));
            File.WriteAllText(importPath, importText + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var runner = new ProcessRunner();
            if (!preview.LocationExists)
            {
                runner.Run(cliPath, ["networkobjects", "add", "location", locationName],
                    workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
                if (runner.ExitCode is not 0)
                    throw new InvalidOperationException($"创建 Veyon 地点失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");
                locationCreated = true;
            }

            try
            {
                runner.Run(cliPath,
                    ["networkobjects", "import", importPath, "location", locationName, "format", ImportFormat],
                    workingDirectory, TimeSpan.FromSeconds(90), outputLimitChars: 16384);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"地点“{locationName}”已{(locationCreated ? "创建" : "复用")}，但电脑清单导入状态未确认；请先在 Veyon Configurator 核对，不要直接重试：{exception.Message}", exception);
            }
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException(
                    $"地点“{locationName}”已{(locationCreated ? "创建" : "复用")}，但电脑清单导入失败；请在 Veyon Configurator 核对该地点后再重试（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");

            IReadOnlyList<VeyonNetworkObject> after;
            try { after = ReadDirectoryObjects(cliPath, workingDirectory); }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"地点“{locationName}”和新增电脑已提交，但读回核对失败；请先在 Veyon Configurator 核对，不要直接重试：{exception.Message}", exception);
            }
            var missing = preview.ComputersToAdd.Where(computer => !after.Any(entry =>
                    entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Host.Equals(computer.Host, StringComparison.OrdinalIgnoreCase) &&
                    entry.Name.Equals(computer.DisplayName, StringComparison.Ordinal)))
                .Select(computer => computer.Host).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException(
                    $"地点操作已提交，但有 {missing.Length} 台新增电脑未能读回确认：{string.Join("、", missing)}。请在 Veyon Configurator 核对，未确认前不要重复导入。");

            return new VeyonLocationImportResult(locationName, computers.Count,
                computers.Count(computer => !string.IsNullOrWhiteSpace(computer.StudentName)),
                preview.ComputersToAdd.Count, preview.SkippedComputers.Count, locationCreated);
        }
        finally
        {
            try { if (File.Exists(importPath)) File.Delete(importPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateImportPlan(string locationName, IReadOnlyList<VeyonNetworkComputer> computers)
    {
        ValidateDirectoryName(locationName, "地点名称");
        if (computers is null || computers.Count is < 1 or > 150)
            throw new InvalidDataException("一个地点需要 1–150 台电脑。");
        foreach (var computer in computers)
        {
            if (computer is null) throw new InvalidDataException("电脑清单不能包含空项目。");
            ValidateDirectoryName(computer.ComputerName, "电脑编号");
            _ = VeyonHostAddress.NormalizeOverride(computer.Host);
            ValidateDirectoryName(computer.DisplayName, "电脑显示名");
            if (!string.IsNullOrWhiteSpace(computer.StudentName))
                ValidateDirectoryName(computer.StudentName.Trim(), "学生姓名");
        }
    }

    private static IReadOnlyList<VeyonNetworkObject> ReadDirectoryObjects(string cliPath, string workingDirectory)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-directory-{Guid.NewGuid():N}.txt");
        try
        {
            var runner = new ProcessRunner();
            // %type% in networkobjects export is translated by Veyon. Read the
            // numeric object types and parent UUIDs from its configuration instead.
            runner.Run(cliPath, ["config", "export", temporaryPath],
                workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException($"Veyon 导出电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");
            if (!File.Exists(temporaryPath))
                throw new InvalidDataException("Veyon 没有生成电脑目录导出文件；没有添加地点。");

            if (new FileInfo(temporaryPath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Veyon 配置超过读取上限；请核对目录。");
            return ParseConfigurationDirectory(File.ReadAllText(temporaryPath, Encoding.UTF8));
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static IReadOnlyList<VeyonNetworkObject> ParseConfigurationDirectory(string json)
    {
        PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Veyon 配置格式无法识别。");
        JsonElement objects = default;
        foreach (var sectionName in new[] { "BuiltinDirectory", "LocalData" })
        {
            if (!root.TryGetProperty(sectionName, out var section)) continue;
            if (section.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Veyon 目录配置格式无法识别。");
            if (!section.TryGetProperty("NetworkObjects", out objects)) continue;
            break;
        }
        if (objects.ValueKind == JsonValueKind.Undefined) return Array.Empty<VeyonNetworkObject>();
        if (objects.ValueKind == JsonValueKind.Object && objects.TryGetProperty("JsonStoreArray", out var array))
            objects = array;
        if (objects.ValueKind != JsonValueKind.Array || objects.GetArrayLength() > 10000)
            throw new InvalidDataException("Veyon 目录对象格式或数量无法识别。");
        var entries = new List<(Guid Id, Guid Parent, int Type, string Name, string Host, string Mac)>();
        var ids = new HashSet<Guid>();
        foreach (var entry in objects.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("Type", out var type) ||
                !type.TryGetInt32(out var numericType) || !entry.TryGetProperty("Uid", out var uid) ||
                uid.ValueKind != JsonValueKind.String || !Guid.TryParse(uid.GetString(), out var id) ||
                id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException("Veyon 目录对象标识或类型无效。");
            string Text(string field) => entry.TryGetProperty(field, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString()! :
                    throw new InvalidDataException("Veyon 目录字段类型无效。") : "";
            var parentText = Text("ParentUid");
            var parent = Guid.Empty;
            if (parentText.Length > 0 && !Guid.TryParse(parentText, out parent))
                throw new InvalidDataException("Veyon 地点关联标识无效。");
            entries.Add((id, parent, numericType, Text("Name"), Text("HostAddress"), Text("MacAddress")));
        }
        var locations = entries.Where(entry => entry.Type == 2).ToDictionary(entry => entry.Id, entry => entry.Name);
        return entries.Where(entry => entry.Type is 2 or 3).Select(entry => new VeyonNetworkObject(
            entry.Type == 2 ? "location" : "computer", entry.Name, entry.Host, entry.Mac,
            locations.GetValueOrDefault(entry.Parent, ""))).ToArray();
    }

    private static string ProcessDetail(ProcessRunner runner)
    {
        var detail = string.Join(" ", new[] { runner.Stderr, runner.Stdout }.Where(value => value.Length > 0));
        return detail.Length > 1000 ? detail[..1000] : detail;
    }

    private static void ValidateDirectoryName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}不能为空，最多 100 个字符，不能有首尾空格或控制字符。");
    }

    private static string[] ParseExportRow(string line, int expectedFieldCount = 3)
    {
        var fields = line.Split(FieldSeparator, StringSplitOptions.None);
        if (fields.Length != expectedFieldCount)
            throw new InvalidDataException("Veyon 电脑目录导出字段数无法识别；没有更改目录或导入目标。");
        return fields;
    }
}
