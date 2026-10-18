using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class EdgePolicyRefreshChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "veyon-edge-refresh-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "queue.json");
            var queue = new StudentAgentEdgePolicyRefreshSignalState(path, protectStorage: false);
            if (queue.Read().RequestId != Guid.Empty) throw new InvalidDataException("新刷新队列不是空状态。");

            queue.Request(10, DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
            var first = queue.Read();
            if (first.RequestId == Guid.Empty || first.Revision != 10)
                throw new InvalidDataException("刷新队列未保存已提交的 Revision。");
            queue.Request(10, DateTimeOffset.UtcNow);
            if (queue.Read() != first)
                throw new InvalidDataException("相同 Revision 触发了重复队列项。");

            queue.Request(11);
            var latest = queue.Read();
            if (latest.RequestId == first.RequestId || latest.Revision != 11)
                throw new InvalidDataException("刷新队列没有合并为最新策略 Revision。");
            var recovered = new StudentAgentEdgePolicyRefreshSignalState(path, protectStorage: false).Read();
            if (recovered != latest)
                throw new InvalidDataException("Agent 重启后没有恢复持久化的刷新任务。");

            try
            {
                queue.Request(10);
                throw new InvalidDataException("刷新队列接受了回退的 Revision。");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("较旧", StringComparison.Ordinal)) { }

            queue.Clear();
            if (new StudentAgentEdgePolicyRefreshSignalState(path, protectStorage: false).Read().RequestId != Guid.Empty)
                throw new InvalidDataException("清除刷新队列后，重启仍读到过期请求。");

            File.WriteAllText(path, "not-json");
            var recoveredCorrupt = new StudentAgentEdgePolicyRefreshSignalState(path, protectStorage: false);
            if (recoveredCorrupt.LoadFailure is null || recoveredCorrupt.Read().RequestId != Guid.Empty)
                throw new InvalidDataException("损坏队列未进入安全空状态供 Agent 恢复。");
            recoveredCorrupt.Request(12);
            if (new StudentAgentEdgePolicyRefreshSignalState(path, protectStorage: false).Read().Revision != 12)
                throw new InvalidDataException("Agent 未能覆盖损坏的队列并恢复服务。");

            if (!EdgePolicyRefreshCompatibility.IsSupportedVersion("155.0.4283.45") ||
                EdgePolicyRefreshCompatibility.IsSupportedVersion("155.0.4283.46") ||
                EdgePolicyRefreshCompatibility.IsSupportedVersion("118.0.2088.76") ||
                EdgePolicyRefreshCompatibility.IsSupportedVersion(null))
                throw new InvalidDataException("Edge 刷新兼容性规则不是精确版本白名单。");

            if (!EdgePolicyRefreshDispatchRules.IsSameInteractiveUserSession(1, "S-1-5-21-A", 1, "s-1-5-21-a") ||
                EdgePolicyRefreshDispatchRules.IsSameInteractiveUserSession(1, "S-1-5-21-A", 2, "S-1-5-21-A") ||
                EdgePolicyRefreshDispatchRules.IsSameInteractiveUserSession(1, "S-1-5-21-A", 1, "S-1-5-21-B") ||
                EdgePolicyRefreshDispatchRules.IsSameInteractiveUserSession(0, "S-1-5-21-A", 0, "S-1-5-21-A"))
                throw new InvalidDataException("Edge 刷新会话隔离规则无效。");

            if (!EdgePolicyRefreshDispatchRules.IsSameIntegrityLevel(0x2000, 0x2000) ||
                EdgePolicyRefreshDispatchRules.IsSameIntegrityLevel(0x2000, 0x3000) ||
                EdgePolicyRefreshDispatchRules.IsSameIntegrityLevel(0, 0))
                throw new InvalidDataException("Edge 刷新完整性级别隔离规则无效。");

            if (!EdgePolicyRefreshDispatchRules.ShouldRetry(0, 3) ||
                !EdgePolicyRefreshDispatchRules.ShouldRetry(2, 3) ||
                EdgePolicyRefreshDispatchRules.ShouldRetry(3, 3) ||
                EdgePolicyRefreshDispatchRules.ShouldRetry(-1, 3))
                throw new InvalidDataException("Edge 刷新重试上限无效。");

            var nowUtc = DateTimeOffset.UtcNow;
            const string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (EdgeBrowserDiagnosticsReader.ClassifyChannel(edgePath) != "stable" ||
                EdgeBrowserDiagnosticsReader.ClassifyChannel(
                    @"C:\Program Files\Microsoft\Edge Beta\Application\msedge.exe") != "beta" ||
                EdgeBrowserDiagnosticsReader.ClassifyChannel(
                    @"C:\Program Files\Microsoft\Edge Dev\Application\msedge.exe") != "dev" ||
                EdgeBrowserDiagnosticsReader.ClassifyChannel(
                    @"C:\Program Files\Microsoft\Edge SxS\Application\msedge.exe") != "canary" ||
                EdgeBrowserDiagnosticsReader.ClassifyChannel(
                    @"C:\Temp\msedge.exe") != "unknown")
                throw new InvalidDataException("Edge 安装通道诊断识别错误。");

            var userSid = "S-1-5-21-100-200-300-1001";
            var requestId = Guid.NewGuid();
            var diagnostics = new EdgeBrowserDiagnosticsSnapshot(1, nowUtc, 42, 41, requestId,
                nowUtc.AddSeconds(-5),
                [new EdgeBrowserInstallationDiagnostic("stable", "x64", "155.0.4283.45",
                    edgePath, true)],
                [new EdgeBrowserProcessDiagnostic(1234, 1, userSid, 0x2000, "stable", "x64",
                    "155.0.4283.45", edgePath, true, nowUtc.AddMinutes(-4),
                    nowUtc.AddHours(-1), false, null)],
                [new EdgeUpdateServiceDiagnostic("edgeupdate", true, "running", "automatic",
                    "1.3.291.9", null)],
                [new EdgeExecutorReportDiagnostic(userSid, 1, requestId, 41,
                    EdgePolicyRefreshStatus.DispatchedUnverified, "155.0.4283.45", 1,
                    nowUtc.AddSeconds(-2), null)],
                Array.Empty<string>());
            EdgeBrowserDiagnosticsValidator.Validate(diagnostics, nowUtc);
            try
            {
                EdgeBrowserDiagnosticsValidator.Validate(diagnostics with
                {
                    RunningProcesses = [diagnostics.RunningProcesses[0] with
                    {
                        ImageFileVersion = "118.0.2088.76"
                    }]
                }, nowUtc);
                throw new InvalidDataException("Edge 118 被错误标记为支持无窗口刷新。");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("诊断条目无效", StringComparison.Ordinal)) { }

            Console.WriteLine("PASS Edge 刷新队列 Revision 去重、持久化恢复及损坏隔离");
            Console.WriteLine("PASS 精确 Edge 版本白名单、用户会话隔离及重试上限");
            Console.WriteLine("PASS Edge 通道、进程、更新服务和会话执行器诊断字段校验");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
