using System.Text;

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
public sealed record VeyonLocationImportResult(string LocationName, int ComputerCount, int NamedStudentCount);

/// <summary>Reads the local Veyon built-in network object directory without changing it.</summary>
public static class VeyonNetworkObjectDirectory
{
    private const string ExportFormat = "\"%name%\";\"%host%\";\"%location%\"";
    private const string FullExportFormat = "\"%type%\";\"%name%\";\"%host%\";\"%mac%\";\"%location%\"";
    private const string ImportFormat = "%name%;%host%;%mac%";

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

    /// <summary>Creates a new built-in location and imports its computers without replacing existing entries.</summary>
    public static VeyonLocationImportResult AddLocation(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("写入 Veyon 机房目录仅支持 Windows 教师端。");
        ValidateDirectoryName(locationName, "地点名称");
        if (computers is null || computers.Count is < 1 or > 150)
            throw new InvalidDataException("一个地点需要 1–150 台电脑。");
        foreach (var computer in computers)
        {
            ValidateDirectoryName(computer.ComputerName, "电脑编号");
            ValidateDirectoryName(computer.Host, "电脑主机名");
            ValidateDirectoryName(computer.DisplayName, "电脑显示名");
            if (!string.IsNullOrWhiteSpace(computer.StudentName))
                ValidateDirectoryName(computer.StudentName.Trim(), "学生姓名");
        }
        if (computers.Select(computer => computer.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != computers.Count)
            throw new InvalidDataException("电脑主机名有重复项；没有写入 Veyon 目录。");

        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath()
                      ?? throw new InvalidOperationException("找不到 Veyon CLI；请先安装教师端 Veyon。");
        var workingDirectory = Path.GetDirectoryName(cliPath)!;
        var current = ReadDirectoryObjects(cliPath, workingDirectory);
        if (current.Any(entry =>
                (entry.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                 entry.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)) ||
                entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Veyon 中已存在地点“{locationName}”；为避免合并或覆盖，请先在 Configurator 核对并选择其他名称。");
        var existingHosts = current.Where(entry => entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Host.Length > 0 ? entry.Host : entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = computers.Select(computer => computer.Host)
            .Where(existingHosts.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (conflicts.Length > 0)
            throw new InvalidDataException("这些主机名已存在于 Veyon 目录，未添加任何内容：" + string.Join("、", conflicts));

        var importPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-room-{Guid.NewGuid():N}.csv");
        try
        {
            var csv = string.Join(Environment.NewLine, computers.Select(computer =>
                $"{computer.DisplayName};{computer.Host};"));
            File.WriteAllText(importPath, csv + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var runner = new ProcessRunner();
            runner.Run(cliPath, ["networkobjects", "add", "location", locationName],
                workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException($"创建 Veyon 地点失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");

            try
            {
                runner.Run(cliPath,
                    ["networkobjects", "import", importPath, "location", locationName, "format", ImportFormat],
                    workingDirectory, TimeSpan.FromSeconds(90), outputLimitChars: 16384);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"地点“{locationName}”已创建，但电脑清单导入状态未确认；请先在 Veyon Configurator 核对，不要直接重试：{exception.Message}", exception);
            }
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException(
                    $"地点“{locationName}”已创建，但电脑清单导入失败；请在 Veyon Configurator 核对该地点后再重试（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");

            IReadOnlyList<VeyonNetworkObject> after;
            try { after = ReadDirectoryObjects(cliPath, workingDirectory); }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"地点“{locationName}”和电脑清单已提交，但读回核对失败；请先在 Veyon Configurator 核对，不要直接重试：{exception.Message}", exception);
            }
            var missing = computers.Where(computer => !after.Any(entry =>
                    entry.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    entry.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Host.Equals(computer.Host, StringComparison.OrdinalIgnoreCase) &&
                    entry.Name.Equals(computer.DisplayName, StringComparison.Ordinal)))
                .Select(computer => computer.Host).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException(
                    $"地点已创建，但有 {missing.Length} 台电脑未能读回确认：{string.Join("、", missing)}。请在 Veyon Configurator 核对，未确认前不要重复导入。");

            return new VeyonLocationImportResult(locationName, computers.Count,
                computers.Count(computer => !string.IsNullOrWhiteSpace(computer.StudentName)));
        }
        finally
        {
            try { if (File.Exists(importPath)) File.Delete(importPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<VeyonNetworkObject> ReadDirectoryObjects(string cliPath, string workingDirectory)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"veyon-campus-directory-{Guid.NewGuid():N}.csv");
        try
        {
            var runner = new ProcessRunner();
            runner.Run(cliPath, ["networkobjects", "export", temporaryPath, "format", FullExportFormat],
                workingDirectory, TimeSpan.FromSeconds(45), outputLimitChars: 4096);
            if (runner.ExitCode is not 0)
                throw new InvalidOperationException($"Veyon 导出电脑目录失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）：{ProcessDetail(runner)}");
            if (!File.Exists(temporaryPath))
                throw new InvalidDataException("Veyon 没有生成电脑目录导出文件；没有添加地点。");

            var objects = new List<VeyonNetworkObject>();
            foreach (var line in File.ReadLines(temporaryPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = ParseExportRow(line, 5);
                objects.Add(new VeyonNetworkObject(fields[0].Trim(), fields[1].Trim(), fields[2].Trim(),
                    fields[3].Trim(), fields[4].Trim()));
            }
            return objects.AsReadOnly();
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
            value.Any(character => char.IsControl(character) || character is ';' or '"'))
            throw new InvalidDataException($"{label}不能为空、不能有首尾空格或控制字符，也不能包含分号或双引号。");
    }

    private static string[] ParseExportRow(string line, int expectedFieldCount = 3)
    {
        var fields = new List<string>(3);
        var value = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else inQuotes = !inQuotes;
            }
            else if (character == ';' && !inQuotes)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }

        if (inQuotes)
            throw new InvalidDataException("Veyon 电脑目录导出行的引号不完整；没有导入目标。");
        fields.Add(value.ToString());
        if (fields.Count != expectedFieldCount)
            throw new InvalidDataException("Veyon 电脑目录导出格式无法识别；没有更改目录或导入目标。");
        return fields.ToArray();
    }
}
