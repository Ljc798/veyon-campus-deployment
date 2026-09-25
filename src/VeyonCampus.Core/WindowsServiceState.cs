using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>Parses the numeric service state returned by Windows sc.exe.</summary>
public static class WindowsServiceState
{
    public const int Stopped = 1;
    public const int StartPending = 2;
    public const int StopPending = 3;
    public const int Running = 4;
    public const int ContinuePending = 5;
    public const int PausePending = 6;
    public const int Paused = 7;

    public static int? Parse(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var match = Regex.Match(output, @"(?:STATE|状态)\s*:\s*(?<state>[1-7])\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["state"].Value, out var state) ? state : null;
    }

    public static string Describe(int? state) => state switch
    {
        Stopped => "已停止",
        StartPending => "正在启动",
        StopPending => "正在停止",
        Running => "正在运行",
        ContinuePending => "正在继续",
        PausePending => "正在暂停",
        Paused => "已暂停",
        _ => "未知"
    };
}
