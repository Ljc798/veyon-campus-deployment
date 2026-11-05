using System.Security.Cryptography;
using VeyonCampus.Checks;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

/// <summary>
/// P3-03 fault-injection scenarios: service kill, power-loss recovery, file-occupancy,
/// dual-teacher concurrency, recoveryRequired boundary, and the cross-version safety
/// matrix. Drives the pure decision surfaces in MaintenanceFaultInjection.cs against
/// scripted facts (FIELD-PENDING for real Windows service/SCM behavior).
/// </summary>
internal static class MaintenanceFaultInjectionChecks
{
    private static readonly MaintenanceDeviceKey TestDevice = new("campus-demo",
        "5f2b3c9d8e7a6f1b0c2d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b");

    private static MaintenanceDeviceIdentity TestIdentity() =>
        new("campus-demo", TestDevice.PinnedIdentityFingerprint, "192.168.1.50:39175");

    private static MaintenanceRun BuildRun(Guid operationId, MaintenanceRunState state)
    {
        var now = DateTimeOffset.UtcNow;
        // Start() creates the Discovered run; drive it to `state` through the state machine
        // so the journal invariants hold. For terminal-ish inspection states used by the
        // concurrency check, a non-terminal Discovered is sufficient.
        var run = MaintenanceRun.Start(operationId, TestIdentity(), now);
        if (state == MaintenanceRunState.Discovered)
            return run;
        // A real active run must have a valid transition path; use IdentityVerified which
        // is a legitimate non-terminal state reachable from Discovered.
        return run.VerifyIdentity(
            new MaintenanceEvidence("signed-identity", TestDevice.PinnedIdentityFingerprint, null,
                Guid.NewGuid(), now), "设备身份已签名核对", now);
    }

    public static void Run()
    {
        Check("服务强杀后重启：中断的更新事务必须进入 RecoveryRequired，绝不宣告 Completed", () =>
        {
            var interrupted = new MaintenanceFaultObservation([], HasInterruptedServiceUpdate: true, false);
            // Restart read the persisted transaction but the helper is gone: unresolved state.
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(interrupted, "needsReview"),
                MaintenanceRunState.RecoveryRequired, "中断且无法确认结果的事务");
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(interrupted, "notFound"),
                MaintenanceRunState.RecoveryRequired, "重启后找不到事务证据");
            // Only a read-back-confirmed terminal outcome may surface as completed.
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(interrupted, "committed"),
                MaintenanceRunState.Completed, "读回确认已提交");
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(interrupted, "rolledBack"),
                MaintenanceRunState.Completed, "读回确认已回滚");
        });
        Check("文件占用/拒绝访问：观察面如实标记，不得进入有副作用阶段", () =>
        {
            var denied = new MaintenanceFaultObservation([], false, RejectedAccess: true);
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(denied, "committed"),
                MaintenanceRunState.ManualRequired, "访问被拒必须转现场处理");
            Expect(!MaintenanceFaultRecovery.MayStartSideEffectingStage(
                    MaintenanceRunState.PlanReady, "needsReview"),
                "读回未确认时禁止继续有副作用操作");
        });
        Check("断电恢复：无中断事务且无活动运行的设备恢复为 Completed 视图", () =>
        {
            var clean = new MaintenanceFaultObservation([], false, false);
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(clean, null),
                MaintenanceRunState.Completed, "干净重启");
            var active = new MaintenanceFaultObservation([Guid.NewGuid()], false, false);
            Expect(MaintenanceFaultRecovery.ClassifyRecovery(active, null),
                MaintenanceRunState.RecoveryRequired, "仍有活动运行的设备");
        });
        Check("双教师端并发：第二请求必须被拒，第一请求继续", () =>
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var activeRun = BuildRun(first, MaintenanceRunState.IdentityVerified);
            var journal = new MaintenanceJournal(DateTimeOffset.UtcNow, [activeRun]);
            Expect(journal.Runs.Count == 1 && journal.Runs[0].IsTerminal == false,
                "journal 含一个未终止运行");
            // A second teacher's different operation id on the same device must be refused.
            Expect(MaintenanceFaultRecovery.RefuseSecondConcurrentOperation(journal,
                    TestDevice, second),
                "第二教师并发维护必须被拒");
            // The first teacher resuming its own operation is not a conflict.
            Expect(!MaintenanceFaultRecovery.RefuseSecondConcurrentOperation(journal,
                    TestDevice, first),
                "原操作恢复不被误判为并发冲突");
        });
        Check("recoveryRequired 边界：PendingVerification 不得进入有副作用阶段", () =>
        {
            foreach (var state in new[]
                     {
                         MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                         MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed
                     })
                Expect(!MaintenanceFaultRecovery.MayStartSideEffectingStage(state, "committed"),
                    $"{state} 不得启动有副作用阶段");
        });
        Check("跨版本安全矩阵：信任根、签名、未知目标版本三者各自独立拒绝", () =>
        {
            Expect(MaintenanceFaultRecovery.NegotiateCrossVersionUpdate(new(
                       "1.1.44", "1.1.45", SignatureVerifies: true, TrustRootRetained: false)),
                "refused:trust-root-lost", "丢失信任根必须拒绝");
            Expect(MaintenanceFaultRecovery.NegotiateCrossVersionUpdate(new(
                       "1.1.44", "1.1.45", SignatureVerifies: false, TrustRootRetained: true)),
                "refused:signature", "签名不匹配必须拒绝");
            Expect(MaintenanceFaultRecovery.NegotiateCrossVersionUpdate(new(
                       "1.1.44", "not-a-version", SignatureVerifies: true, TrustRootRetained: true)),
                "refused:unknown-target-version", "未知目标版本必须拒绝");
            Expect(MaintenanceFaultRecovery.NegotiateCrossVersionUpdate(new(
                       "1.1.44", "1.1.45", SignatureVerifies: true, TrustRootRetained: true)),
                "accepted", "信任根保留且版本可比时接受");
        });
        Check("跨版本安全矩阵：旧协议最低兼容下签名信封可独立验签", () =>
        {
            var key = RSA.Create(2048);
            var trustRootFingerprint = StudentAgentResponseCryptography.GetFingerprint(
                key.ExportSubjectPublicKeyInfoPem());
            var (envelope, publicPem) = MaintenanceSignedCommandEnvelope.Issue(
                protocolVersion: 1, payload: "{\"command\":\"getHealth\"}", key, trustRootFingerprint);
            var trusted = RSA.Create();
            trusted.ImportFromPem(publicPem);
            Expect(envelope.Verifies(trusted), "旧协议最低版本命令对保留信任根验签通过");
            var other = RSA.Create(2048);
            Expect(!envelope.Verifies(other), "非信任根验签必须失败");
        });
    }

    private static void Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(name, exception);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'.");
    }
}
