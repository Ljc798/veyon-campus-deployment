using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class StudentApplicationUpdateChecks
{
    public static void RunRecoveryStateFixtures()
    {
        CheckAtomicTransactionTerminalStates();
        CheckSupersededRecoveryFacts();
    }

    public static void Run()
    {
        CheckSignedCommand();
        CheckSignedAgentResponses();
        CheckCommandBoundStatusRequeryAndProgress();
        CheckDownloadCapabilityResponse();
        CheckAgentUpdateCompatibilityMatrix();
        CheckStudentMaintenancePlanner();
        CheckInlineIdentityReviewActions();
        CheckUpdateHelperVersionBinding();
        CheckAgentVersionRebaseGuard();
        CheckSetupAndAgentVersionIndependence();
        CheckLegacyV140ConfigFingerprintMigration();
        CheckSignedCommandAcknowledgements();
        CheckAgentIdentityTrustStore();
        CheckPushFailureIsolation();
        CheckAtomicTransactionTerminalStates();
        CheckAtomicJournalRejectsStaleRecords();
        CheckStagingDirectoryCleanupGate();
        CheckUpdateHandoffRollback();
        CheckReplayStore();
        StudentApplicationUpdateArtifactTransferChecks.Run();
    }

    public static void RunCompatibilityFixtures()
    {
        CheckCommandBoundStatusRequeryAndProgress();
        CheckDownloadCapabilityResponse();
        CheckAgentUpdateCompatibilityMatrix();
        CheckStudentMaintenancePlanner();
        CheckInlineIdentityReviewActions();
        CheckUpdateHelperVersionBinding();
        CheckAgentVersionRebaseGuard();
        CheckSetupAndAgentVersionIndependence();
        CheckLegacyV140ConfigFingerprintMigration();
        CheckAtomicTransactionTerminalStates();
        CheckAtomicJournalRejectsStaleRecords();
        CheckStagingDirectoryCleanupGate();
        CheckUpdateHandoffRollback();
    }

    private static void CheckAtomicTransactionTerminalStates()
    {
        var transactionType = typeof(StudentApplicationUpdateProcessor).Assembly.GetType(
                                 "VeyonCampus.Core.StudentUpdateAtomicTransaction", throwOnError: true)
                             ?? throw new InvalidOperationException("Student atomic transaction type was not found.");
        var transitionMethod = transactionType.GetMethod("IsTransitionAllowed",
                                 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                             ?? throw new InvalidOperationException("Student atomic transaction transition table was not found.");

        bool IsAllowed(StudentUpdateTransactionState current, StudentUpdateTransactionState next) =>
            (bool)(transitionMethod.Invoke(null, [current, next])
                   ?? throw new InvalidOperationException("Student atomic transaction transition result was empty."));

        foreach (var terminal in new[] { StudentUpdateTransactionState.Committed, StudentUpdateTransactionState.RolledBack,
                     StudentUpdateTransactionState.Superseded })
        foreach (var next in Enum.GetValues<StudentUpdateTransactionState>())
            Expect(!IsAllowed(terminal, next));

        Expect(IsAllowed(StudentUpdateTransactionState.Prepared, StudentUpdateTransactionState.RecoveryRequired));
        Expect(IsAllowed(StudentUpdateTransactionState.RollbackPending, StudentUpdateTransactionState.RecoveryRequired));
        Expect(IsAllowed(StudentUpdateTransactionState.RecoveryRequired, StudentUpdateTransactionState.RollbackPending));
        Expect(IsAllowed(StudentUpdateTransactionState.RecoveryRequired, StudentUpdateTransactionState.Superseded));
        Expect(!IsAllowed(StudentUpdateTransactionState.RecoveryRequired, StudentUpdateTransactionState.Committed));
    }

    private static void CheckSupersededRecoveryFacts()
    {
        var transactionType = typeof(StudentApplicationUpdateProcessor).Assembly.GetType(
                                 "VeyonCampus.Core.StudentUpdateAtomicTransaction", throwOnError: true)
                             ?? throw new InvalidOperationException("Student atomic transaction type was not found.");
        var method = transactionType.GetMethod("HasSupersedingInstallationFacts",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Superseded transaction version fence was not found.");
        bool IsEligible(StudentUpdateTransactionRecord record, string setupVersion, string agentVersion,
            bool previousSetupPresent) =>
            (bool)(method.Invoke(null,
                [record, setupVersion, agentVersion, previousSetupPresent])
                   ?? throw new InvalidOperationException("Superseded transaction version fence returned no result."));

        var now = DateTimeOffset.UtcNow;
        var record = new StudentUpdateTransactionRecord(1, Guid.NewGuid(), new string('a', 24),
            "1.1.41", "1.1.42", "0.4.55", "1.1.42",
            "VeyonCampus-Student-Setup-1.1.42-win-x64.exe", new string('b', 64), new string('c', 64),
            now, now, StudentUpdateTransactionState.RecoveryRequired);
        Expect(IsEligible(record, "1.1.45", "0.4.56", false));
        Expect(!IsEligible(record, "1.1.42", "0.4.56", false));
        Expect(!IsEligible(record, "1.1.45", "0.4.54", false));
        Expect(!IsEligible(record, "1.1.45", "0.4.56", true));
        Expect(!IsEligible(record with { State = StudentUpdateTransactionState.RollbackPending },
            "1.1.45", "0.4.56", false));
    }

    private static void CheckStagingDirectoryCleanupGate()
    {
        var transactionType = typeof(StudentApplicationUpdateProcessor).Assembly.GetType(
                                 "VeyonCampus.Core.StudentUpdateAtomicTransaction", throwOnError: true)
                             ?? throw new InvalidOperationException("Student atomic transaction type was not found.");
        var method = transactionType.GetMethod("CanCleanupUnclaimedStagingDirectory",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Student update staging cleanup gate was not found.");
        bool CanCleanup(Guid commandId, Guid? journalCommandId,
            StudentUpdateTransactionState? journalState) =>
            (bool)(method.Invoke(null, [commandId, journalCommandId, journalState])
                   ?? throw new InvalidOperationException("Student staging cleanup gate returned no result."));

        var commandId = Guid.NewGuid();
        Expect(CanCleanup(commandId, null, null));
        Expect(CanCleanup(commandId, Guid.NewGuid(), StudentUpdateTransactionState.Committed));
        Expect(CanCleanup(commandId, Guid.NewGuid(), StudentUpdateTransactionState.RolledBack));
        Expect(CanCleanup(commandId, Guid.NewGuid(), StudentUpdateTransactionState.Superseded));
        Expect(!CanCleanup(commandId, commandId, StudentUpdateTransactionState.Committed));
        Expect(!CanCleanup(commandId, commandId, StudentUpdateTransactionState.SetupInstallationPending));
        Expect(!CanCleanup(commandId, commandId, StudentUpdateTransactionState.RecoveryRequired));
        Expect(!CanCleanup(commandId, Guid.NewGuid(), StudentUpdateTransactionState.SetupInstallationPending));
        Expect(!CanCleanup(commandId, Guid.NewGuid(), null));
    }

    private static void CheckAtomicJournalRejectsStaleRecords()
    {
        var transactionType = typeof(StudentApplicationUpdateProcessor).Assembly.GetType(
                                 "VeyonCampus.Core.StudentUpdateAtomicTransaction", throwOnError: true)
                             ?? throw new InvalidOperationException("Student atomic transaction type was not found.");
        var method = transactionType.GetMethod("IsCurrentRecord",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Student journal compare guard was not found.");
        bool Matches(StudentUpdateTransactionRecord expected, StudentUpdateTransactionRecord? persisted) =>
            (bool)(method.Invoke(null, [expected, persisted])
                   ?? throw new InvalidOperationException("Student journal compare guard returned no result."));

        var now = DateTimeOffset.UtcNow;
        var record = new StudentUpdateTransactionRecord(1, Guid.NewGuid(), new string('a', 24),
            "1.0.0", "1.1.0", "1.0.0", "1.1.0", "VeyonCampus-Student-Setup-1.1.0-win-x64.exe",
            new string('b', 64), new string('c', 64), now, now, StudentUpdateTransactionState.SetupInstalled);
        Expect(Matches(record, record));
        Expect(!Matches(record, null));
        Expect(!Matches(record, record with
        {
            State = StudentUpdateTransactionState.Committed,
            UpdatedUtc = now.AddTicks(1)
        }));
        Expect(!Matches(record, record with
        {
            HandoffHelperProcessId = 123,
            HandoffHelperStartUtcTicks = 456,
            UpdatedUtc = now.AddTicks(1)
        }));
    }

    private static void CheckUpdateHandoffRollback()
    {
        var method = typeof(StudentApplicationUpdateHandoff).GetMethod("ApplyAgentUpdateWithRollback",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Student update rollback sequence was not found.");
        int Run(Action waitForPreviousExit, Action startUpdated, Action candidateStarted,
            Func<bool> waitForUpdatedHealth,
            Action commitUpdate, Action<Exception> restorePrevious, Action<Exception> reportFailure) =>
            (int)method.Invoke(null,
                [waitForPreviousExit, startUpdated, candidateStarted, waitForUpdatedHealth, commitUpdate,
                    restorePrevious, reportFailure])!;

        var successfulSteps = new List<string>();
        var successResult = Run(
            () => successfulSteps.Add("wait-old-exit"),
            () => successfulSteps.Add("start-new"),
            () => successfulSteps.Add("candidate-started"),
            () => { successfulSteps.Add("check-new-health"); return true; },
            () => successfulSteps.Add("commit"),
            _ => throw new InvalidOperationException("Unexpected rollback."),
            _ => throw new InvalidOperationException("Unexpected failure report."));
        Expect(successResult == 0 && successfulSteps.SequenceEqual(
            ["wait-old-exit", "start-new", "candidate-started", "check-new-health", "commit"]));

        var rollbackSteps = new List<string>();
        Exception? reportedFailure = null;
        var rollbackResult = Run(
            () => rollbackSteps.Add("wait-old-exit"),
            () => rollbackSteps.Add("start-new"),
            () => rollbackSteps.Add("candidate-started"),
            () => { rollbackSteps.Add("check-new-health"); return false; },
            () => rollbackSteps.Add("commit"),
            _ => rollbackSteps.Add("restore-old"),
            exception => reportedFailure = exception);
        Expect(rollbackResult == 1 && rollbackSteps.SequenceEqual(
            ["wait-old-exit", "start-new", "candidate-started", "check-new-health", "restore-old"]));
        Expect(reportedFailure is InvalidOperationException { InnerException: TimeoutException } &&
               reportedFailure.Message.Contains("旧 Agent 已恢复", StringComparison.Ordinal));

        var updateFailure = new InvalidOperationException("updated agent failed");
        var restoreFailure = new IOException("restore failed");
        Exception? aggregatedFailure = null;
        var failedRollbackResult = Run(
            () => { },
            () => { },
            () => { },
            () => throw updateFailure,
            () => throw new InvalidOperationException("Commit must not run after failed health."),
            _ => throw restoreFailure,
            exception => aggregatedFailure = exception);
        Expect(failedRollbackResult == 1 && aggregatedFailure is AggregateException aggregate &&
               aggregate.InnerExceptions.Count == 2 && ReferenceEquals(aggregate.InnerExceptions[0], updateFailure) &&
               ReferenceEquals(aggregate.InnerExceptions[1], restoreFailure));
    }

    private static void CheckSignedCommand()
    {
        using var campusKey = RSA.Create(2048);
        using var developerKey = RSA.Create(2048);
        var apiBase = new Uri("https://release-fixture.invalid/");
        var now = DateTimeOffset.UtcNow;
        var releaseId = Guid.NewGuid();
        var artifactHash = new string('A', 64);
        var manifest = new ApplicationReleaseManifest(1, "VeyonCampus.StudentSetup", "StudentSetup",
            "2.0.0", "win-x64", "VeyonCampus-Student-Setup-2.0.0-win-x64.exe", 512,
            artifactHash, new Uri(apiBase, $"v1/releases/{releaseId:D}/artifact").AbsoluteUri);
        var developerSignature = developerKey.SignData(ApplicationReleaseClient.CreateCanonicalPayload(manifest),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var release = new ApplicationReleaseEnvelope(manifest, "RSA-PSS-SHA256",
            Convert.ToBase64String(developerSignature), now);
        var command = new StudentApplicationUpdateCommand(1, "ExampleCampus", Guid.NewGuid(), now,
            now.AddMinutes(8), release,
            "http://192.168.4.10:39175/v1/student-updates/" + new string('b', 64));
        var apiPublicPem = developerKey.ExportSubjectPublicKeyInfoPem();
        var signed = StudentApplicationUpdateCryptography.Sign(command, campusKey, apiBase, apiPublicPem);
        var verified = StudentApplicationUpdateCryptography.Verify(signed, campusKey.ExportSubjectPublicKeyInfoPem(),
            "ExampleCampus", apiBase, apiPublicPem, now);
        Expect(verified.CommandId == command.CommandId && verified.Release.Manifest.Version == "2.0.0");

        Reject(() => StudentApplicationUpdateCryptography.Verify(signed, campusKey.ExportSubjectPublicKeyInfoPem(),
            "DifferentCampus", apiBase, apiPublicPem, now));
        Reject(() => StudentApplicationUpdateCryptography.Verify(signed, campusKey.ExportSubjectPublicKeyInfoPem(),
            "ExampleCampus", apiBase, apiPublicPem, now.AddMinutes(9)));
        Reject(() => StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(
            "https://192.168.4.10:39175/v1/student-updates/" + new string('b', 64)));
        Reject(() => StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(
            "http://203.0.113.10:39175/v1/student-updates/" + new string('b', 64)));

        var signedEnvelope = JsonSerializer.Deserialize<SignedStudentApplicationUpdate>(signed)!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(signedEnvelope.Payload))
            .Replace("192.168.4.10", "192.168.4.11", StringComparison.Ordinal);
        var modified = JsonSerializer.Serialize(signedEnvelope with
        {
            Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
        });
        Reject(() => StudentApplicationUpdateCryptography.Verify(modified,
            campusKey.ExportSubjectPublicKeyInfoPem(), "ExampleCampus", apiBase, apiPublicPem, now));
    }

    private static void CheckReplayStore()
    {
        var temporaryDirectory = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-update-replay-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(temporaryDirectory, "replay.json");
        var commandId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            Expect(StudentApplicationUpdateReplayStore.TryConsume(statePath, commandId, now.AddMinutes(5), now));
            Expect(!StudentApplicationUpdateReplayStore.TryConsume(statePath, commandId, now.AddMinutes(5), now));
            Expect(StudentApplicationUpdateReplayStore.TryConsume(statePath, commandId, now.AddMinutes(5), now.AddMinutes(6)));

            var targetDirectory = Path.Combine(temporaryDirectory, "link-target");
            var linkedDirectory = Path.Combine(temporaryDirectory, "linked-directory");
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllBytes(Path.Combine(targetDirectory, "replay.json"), new byte[64 * 1024 + 1]);
            Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
            try
            {
                try
                {
                    StudentApplicationUpdateReplayStore.TryConsume(Path.Combine(linkedDirectory, "replay.json"),
                        Guid.NewGuid(), now.AddMinutes(5), now);
                }
                catch (InvalidDataException exception) when (exception.Message.Contains("重解析点", StringComparison.Ordinal))
                {
                    return;
                }
                throw new InvalidOperationException("Replay store followed a linked parent before rejecting it.");
            }
            finally
            {
                Directory.Delete(linkedDirectory);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void CheckSignedAgentResponses()
    {
        using var agentKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var response = new StudentApplicationUpdateResponse(1,
            StudentApplicationUpdateResponseCryptography.Purpose, "ExampleCampus", Guid.NewGuid(), now,
            "2.1.0", "2.1.0", false, "StudentSetup 已读回。");
        var signed = StudentAgentResponseCryptography.Sign(response, agentKey);
        var publicKey = agentKey.ExportSubjectPublicKeyInfoPem();
        var verified = StudentAgentResponseCryptography.Verify<StudentApplicationUpdateResponse>(signed, publicKey);
        Expect(verified.Payload == response && verified.MatchesPinnedKey &&
               verified.Fingerprint == StudentAgentResponseCryptography.GetFingerprint(publicKey));
        StudentApplicationUpdateResponseCryptography.Validate(response, "ExampleCampus", response.CommandId,
            "2.1.0", restartPending: false, now);

        var mismatchedPin = StudentAgentResponseCryptography.Verify<StudentApplicationUpdateResponse>(signed,
            wrongKey.ExportSubjectPublicKeyInfoPem());
        Expect(!mismatchedPin.MatchesPinnedKey && mismatchedPin.Payload == response);
        Reject(() => StudentApplicationUpdateResponseCryptography.Validate(response, "ExampleCampus", Guid.NewGuid(),
            "2.1.0", restartPending: false, now));
        Reject(() => StudentApplicationUpdateResponseCryptography.Validate(response, "OtherCampus", response.CommandId,
            "2.1.0", restartPending: false, now));
        Reject(() => StudentApplicationUpdateResponseCryptography.Validate(response, "ExampleCampus", response.CommandId,
            "2.2.0", restartPending: false, now));

        var envelope = JsonSerializer.Deserialize<SignedStudentAgentResponse>(signed)!;
        var body = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload))
            .Replace("2.1.0", "9.9.9", StringComparison.Ordinal);
        var tampered = JsonSerializer.Serialize(envelope with
        {
            Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(body))
        });
        Reject(() => StudentAgentResponseCryptography.Verify<StudentApplicationUpdateResponse>(tampered, publicKey));
    }

    private static void CheckCommandBoundStatusRequeryAndProgress()
    {
        using var campusKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            "ExampleCampus", Guid.NewGuid(), now, commandId);
        var signed = WebsitePolicyStatusCryptography.SignRequest(request, campusKey);
        var verified = WebsitePolicyStatusCryptography.VerifyRequest(signed,
            campusKey.ExportSubjectPublicKeyInfoPem(), "ExampleCampus", now);
        Expect(verified.UpdateCommandId == commandId);
        Reject(() => WebsitePolicyStatusCryptography.VerifyRequest(signed,
            campusKey.ExportSubjectPublicKeyInfoPem(), "OtherCampus", now));

        var row = new StudentApplicationUpdateTargetProgressItem("PC-01", "学生 01");
        row.SetTransportProgress("已发送签名更新命令，正在连接学生 Agent", commandId);
        row.SetTransportProgress("学生端更新处理中；已等待 5 秒", commandId);
        Expect(row.CommandId == commandId && row.StatusText.Contains("正在读取签名阶段", StringComparison.Ordinal));
        row.SetResult(false, needsReview: true, detail: "更新请求超时；需要重新查询。");
        Expect(row.CanRequery);
        row.BeginRequery();
        Expect(!row.CanRequery && row.StatusText == "正在重新核对终态");
        row.SetResult(false, needsReview: true, detail: "Agent 暂时不可用；请稍后重试。");
        Expect(row.CanRequery);
        row.SetPendingVerification("等待长连接恢复");
        Expect(row.StatusText == "待验证" && row.CanRequery);
    }

    private static void CheckDownloadCapabilityResponse()
    {
        using var agentKey = RSA.Create(2048);
        var publicPem = agentKey.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid();
        var response = new StudentApplicationUpdateDownloadCapabilityResponse(
            StudentApplicationUpdateDownloadCapabilityCryptography.SchemaVersion,
            StudentApplicationUpdateDownloadCapabilityCryptography.Purpose, "ExampleCampus", nonce, now,
            StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion);
        var signed = StudentAgentResponseCryptography.Sign(response, agentKey);
        var verified = StudentAgentResponseCryptography
            .Verify<StudentApplicationUpdateDownloadCapabilityResponse>(signed, publicPem);
        Expect(verified.MatchesPinnedKey);
        StudentApplicationUpdateDownloadCapabilityCryptography.Validate(verified.Payload, "ExampleCampus", nonce,
            now);
        Reject(() => StudentApplicationUpdateDownloadCapabilityCryptography.Validate(verified.Payload,
            "ExampleCampus", Guid.NewGuid(), now));
        Reject(() => StudentApplicationUpdateDownloadCapabilityCryptography.Validate(
            verified.Payload with { DownloadRequestProtocolVersion = 99 }, "ExampleCampus", nonce, now));

        var fullyReported = response with
        {
            StudentUpdateProtocolVersion = 1,
            StudentSetupVersion = "1.2.3",
            AgentRuntimeVersion = "0.4.56",
            AgentProductVersion = "0.4.56",
            CoreAssemblyVersion = "0.4.53",
            DeveloperReleasePublicKeyFingerprint = new string('A', 64),
            TeacherChannelConfigurationReady = true,
            TeacherChannelConfigurationFingerprint = new string('B', 64),
            ConfigurationFingerprint = new string('C', 64)
        };
        StudentApplicationUpdateDownloadCapabilityCryptography.Validate(fullyReported,
            "ExampleCampus", nonce, now);
        Expect(fullyReported.StudentSetupVersion != fullyReported.AgentRuntimeVersion &&
               fullyReported.AgentRuntimeVersion != fullyReported.CoreAssemblyVersion);
        Reject(() => StudentApplicationUpdateDownloadCapabilityCryptography.Validate(
            fullyReported with { TeacherChannelConfigurationFingerprint = null },
            "ExampleCampus", nonce, now));
    }

    private static void CheckAgentUpdateCompatibilityMatrix()
    {
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.50", null, null, "stable") ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.51", null, null, "stable") ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.53", 1, null, "stable") ==
               StudentAgentUpdateTransportMode.LegacySignedHttp);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.55", 1, null, "stable") ==
               StudentAgentUpdateTransportMode.LegacySignedHttp);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.55", null, null, "stable") ==
               StudentAgentUpdateTransportMode.LegacySignedHttp);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.55", null, 1, "stable") ==
               StudentAgentUpdateTransportMode.SignedDownloadV1);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", null, 1, "stable") ==
               StudentAgentUpdateTransportMode.SignedDownloadV1);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.55", null, null, "local-test") ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.55", 2, null, "stable") ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", 1, 1, "stable") ==
               StudentAgentUpdateTransportMode.SignedDownloadV1);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.57", 1, 1, "stable",
                   new string('A', 64), new string('B', 64)) == StudentAgentUpdateTransportMode.Unsupported);
        var officialKeyFingerprint = new string('A', 64);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", 1, 1, "stable",
                   officialKeyFingerprint, officialKeyFingerprint, capabilityReportsPinnedKey: true) ==
               StudentAgentUpdateTransportMode.SignedDownloadV1);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", 1, 1, "stable",
                   null, officialKeyFingerprint, capabilityReportsPinnedKey: true) ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", 1, null, "stable",
                   null, officialKeyFingerprint, capabilityReportsPinnedKey: true) ==
               StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.56", 1, 99, "stable") ==
               StudentAgentUpdateTransportMode.Unsupported);

        var testFingerprint = new string('D', 64);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.57", 1, null, "local-test",
                   expectedLocalTestKeyFingerprint: testFingerprint) == StudentAgentUpdateTransportMode.Unsupported);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.1.0", 1, 1, "local-test",
                   localTestKeyFingerprint: testFingerprint, expectedLocalTestKeyFingerprint: testFingerprint) ==
               StudentAgentUpdateTransportMode.SignedDownloadV1);
        Expect(StudentAgentUpdateCompatibility.SelectTransport("0.4.57", 1, 1, "local-test",
                   localTestKeyFingerprint: new string('E', 64), expectedLocalTestKeyFingerprint: testFingerprint) ==
               StudentAgentUpdateTransportMode.Unsupported);
        var missingTestCapability = StudentAgentUpdateCompatibility.DescribeUnsupported("0.4.56", 1,
            capability: null, releaseChannel: "local-test",
            expectedLocalTestKeyFingerprint: testFingerprint);
        Expect(missingTestCapability.Contains("未返回签名的本机测试下载能力回执", StringComparison.Ordinal));
        var mismatchedTestCapability = new StudentApplicationUpdateDownloadCapabilityResponse(
            StudentApplicationUpdateDownloadCapabilityCryptography.SchemaVersion,
            StudentApplicationUpdateDownloadCapabilityCryptography.Purpose, "ExampleCampus", Guid.NewGuid(),
            DateTimeOffset.UtcNow, StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion,
            StudentUpdateProtocolVersion: 1,
            LocalTestReleasePublicKeyFingerprint: new string('E', 64));
        var mismatchedTestKeyReason = StudentAgentUpdateCompatibility.DescribeUnsupported("0.4.57", 1,
            mismatchedTestCapability, "local-test", expectedLocalTestKeyFingerprint: testFingerprint);
        Expect(mismatchedTestKeyReason.Contains(new string('E', 12), StringComparison.Ordinal) &&
               mismatchedTestKeyReason.Contains(new string('D', 12), StringComparison.Ordinal) &&
               mismatchedTestKeyReason.Contains("同一测试公钥", StringComparison.Ordinal));

        Expect(StudentAgentChannelHttpMessageHandler.ShouldUseDirectFallback(true,
            allowLegacyDirectFallback: false, allowDirectFallbackWhenChannelOffline: true));
        Expect(!StudentAgentChannelHttpMessageHandler.ShouldUseDirectFallback(true,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: false));
        Expect(StudentAgentChannelHttpMessageHandler.ShouldUseDirectFallback(false,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: false));
    }

    private static void CheckStudentMaintenancePlanner()
    {
        static StudentMaintenancePlanningInput Input(string? setup = "1.1.56", string? agent = "0.4.56",
            string targetSetup = "1.1.57", string? targetAgent = "0.4.57", int? protocol = 1,
            string? transaction = null, bool? configMatches = null, bool connected = false,
            bool identityTrusted = true) => new(identityTrusted, setup, agent, targetSetup, targetAgent,
                protocol, transaction, configMatches, connected);

        var oldAgent = StudentMaintenancePlanner.Create(Input());
        Expect(oldAgent.Kind == StudentMaintenancePlanKind.UpdateThenConfiguration &&
               oldAgent.UpdateTransport == StudentMaintenanceTransport.TrustedLegacyHttp &&
               oldAgent.NeedsSoftwareUpdate && oldAgent.NeedsConfigurationSync);

        var connectedNewAgent = StudentMaintenancePlanner.Create(Input(connected: true));
        Expect(connectedNewAgent.UpdateTransport == StudentMaintenanceTransport.AuthenticatedAgentChannel);

        var configOnly = StudentMaintenancePlanner.Create(Input(setup: "1.1.57", agent: "0.4.57",
            configMatches: false));
        Expect(configOnly.Kind == StudentMaintenancePlanKind.ConfigurationOnly &&
               !configOnly.NeedsSoftwareUpdate && configOnly.NeedsConfigurationSync);

        var noOp = StudentMaintenancePlanner.Create(Input(setup: "1.1.57", agent: "0.4.57",
            configMatches: true));
        Expect(noOp.Kind == StudentMaintenancePlanKind.NoOp && !noOp.NeedsConfigurationSync);

        var supportedHistoricalAgent = StudentMaintenancePlanner.Create(Input(protocol: null));
        Expect(supportedHistoricalAgent.Kind == StudentMaintenancePlanKind.UpdateThenConfiguration &&
               supportedHistoricalAgent.UpdateTransport == StudentMaintenanceTransport.TrustedLegacyHttp);

        var unsupportedOldAgent = StudentMaintenancePlanner.Create(Input(agent: "0.4.50", protocol: null));
        Expect(unsupportedOldAgent.Kind == StudentMaintenancePlanKind.ManualRequired &&
               !unsupportedOldAgent.NeedsSoftwareUpdate);

        var unknownStableTarget = StudentMaintenancePlanner.Create(Input(setup: "1.1.57", agent: "0.4.56",
            targetAgent: null, protocol: 1));
        Expect(unknownStableTarget.Kind == StudentMaintenancePlanKind.ManualRequired);

        var pendingTransaction = StudentMaintenancePlanner.Create(Input(transaction: "inProgress"));
        Expect(pendingTransaction.Kind == StudentMaintenancePlanKind.RecoveryRequired &&
               !pendingTransaction.NeedsSoftwareUpdate && !pendingTransaction.NeedsConfigurationSync);

        var identityChanged = StudentMaintenancePlanner.Create(Input(identityTrusted: false));
        Expect(identityChanged.Kind == StudentMaintenancePlanKind.ManualRequired &&
               !identityChanged.NeedsConfigurationSync);

        var independentVersions = StudentMaintenancePlanner.Create(Input(setup: "1.1.58", agent: "0.4.56",
            targetSetup: "1.1.57", targetAgent: "0.4.57"));
        Expect(independentVersions.Kind == StudentMaintenancePlanKind.ManualRequired);

        var agentOnlyUpdate = StudentMaintenancePlanner.Create(Input(setup: "1.1.57", agent: "0.4.56",
            targetSetup: "1.1.57", targetAgent: "0.4.57"));
        Expect(agentOnlyUpdate.Kind == StudentMaintenancePlanKind.UpdateThenConfiguration &&
               agentOnlyUpdate.NeedsSoftwareUpdate);
    }

    private static void CheckInlineIdentityReviewActions()
    {
        var updateRow = new StudentApplicationUpdateTargetProgressItem("10.0.0.7", "student");
        updateRow.SetResult(false, true, "Agent 身份需先核对；指纹与教师端信任记录不一致。");
        Expect(updateRow.CanReviewIdentity);
        updateRow.BeginIdentityReview();
        Expect(!updateRow.CanReviewIdentity);
        updateRow.CompleteIdentityReview();
        Expect(!updateRow.CanReviewIdentity && updateRow.HasIdentityReviewStatus);

        var configRow = new StudentConfigurationSyncTargetProgressItem("10.0.0.7", "student");
        configRow.SetResult(false, true, "学生 Agent 身份尚未核对；配置未发送。");
        Expect(configRow.CanReviewIdentity);
        configRow.CompleteIdentityReview();
        Expect(!configRow.CanReviewIdentity && configRow.HasIdentityReviewStatus);
    }

    private static void CheckUpdateHelperVersionBinding()
    {
        var helperPath = StudentApplicationUpdateHandoff.GetUpdateHelperPath(
            @"C:\Program Files", "1.2.3");
        Expect(helperPath.EndsWith(Path.Combine("Updater", "Student", "1.2.3",
            "VeyonCampus.UpdateHelper.exe"), StringComparison.OrdinalIgnoreCase));
    }

    private static void CheckAgentVersionRebaseGuard()
    {
        var method = typeof(StudentUpdateAtomicTransaction).GetMethod("CanRebaseTargetAgentVersion",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Agent target version rebase guard was not found.");
        bool CanRebase(StudentUpdateTransactionRecord record, string expected, string actual) =>
            (bool)(method.Invoke(null, [record, expected, actual])
                   ?? throw new InvalidOperationException("Agent target version rebase guard returned no result."));

        var now = DateTimeOffset.UtcNow;
        var record = new StudentUpdateTransactionRecord(1, Guid.NewGuid(), new string('a', 24),
            "1.1.0", "1.2.0", "0.4.56", "0.4.56",
            "VeyonCampus-Student-Setup-1.2.0-win-x64.exe", new string('b', 64), new string('c', 64),
            now, now, StudentUpdateTransactionState.SetupInstalled);
        Expect(CanRebase(record, "0.4.56", "0.4.57"));
        Expect(!CanRebase(record, "0.4.55", "0.4.57"));
        Expect(!CanRebase(record, "0.4.56", "0.4.55"));
        Expect(!CanRebase(record with { State = StudentUpdateTransactionState.AgentStaged },
            "0.4.56", "0.4.57"));

        var legacyMethod = typeof(StudentUpdateAtomicTransaction).GetMethod(
                               "CanRebaseLegacySetupVersionAgentAlias",
                               System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                           ?? throw new InvalidOperationException("Legacy Setup/Agent alias migration guard was not found.");
        bool CanMigrateLegacyAlias(StudentUpdateTransactionRecord legacy, string alias, string actual) =>
            (bool)(legacyMethod.Invoke(null, [legacy, alias, actual])
                   ?? throw new InvalidOperationException("Legacy Setup/Agent alias guard returned no result."));
        var legacyRecord = record with
        {
            TargetSetupVersion = "1.2.0",
            TargetAgentVersion = "1.2.0",
            State = StudentUpdateTransactionState.ActivationPending
        };
        Expect(CanMigrateLegacyAlias(legacyRecord, "1.2.0", "0.4.57"));
        Expect(CanMigrateLegacyAlias(legacyRecord, "1.2.0", "0.4.56"));
        Expect(!CanMigrateLegacyAlias(legacyRecord, "1.2.0", "0.4.55"));
        Expect(!CanMigrateLegacyAlias(legacyRecord with { State = StudentUpdateTransactionState.SetupInstalled },
            "1.2.0", "0.4.57"));
    }

    private static void CheckLegacyV140ConfigFingerprintMigration()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new WebsitePolicyAgentConfig("ExampleCampus", "campus-public-key",
            DeploymentId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            ApplicationVersion: "1.1.40", ApplicationPolicyPublicKeyPem: "application-public-key",
            StudentSystemPolicyPublicKeyPem: "system-public-key");
        var legacyFingerprint = WebsitePolicyAgent.LegacyV140ConfigFingerprint(config);
        var record = new StudentUpdateTransactionRecord(1, Guid.NewGuid(), new string('a', 24),
            "1.1.40", "1.1.57", "0.4.55", "0.4.57",
            "VeyonCampus-Student-Setup-1.1.57-win-x64.exe", legacyFingerprint, new string('b', 64),
            now, now, StudentUpdateTransactionState.ActivationPending);

        Expect(StudentUpdateAtomicTransaction.MatchesTrustedV140ConfigurationFingerprint(record, config));
        Expect(!StudentUpdateAtomicTransaction.MatchesTrustedV140ConfigurationFingerprint(
            record with { ConfigurationFingerprint = new string('c', 64) }, config));
        Expect(!StudentUpdateAtomicTransaction.MatchesTrustedV140ConfigurationFingerprint(record,
            config with { PublicKeyPem = "different-campus-key" }));
        Expect(!StudentUpdateAtomicTransaction.MatchesTrustedV140ConfigurationFingerprint(record,
            config with { TeacherChannelEndpoints = ["https://teacher.example.invalid/"] }));
        Expect(!StudentUpdateAtomicTransaction.MatchesTrustedV140ConfigurationFingerprint(record with
        {
            PreviousSetupVersion = "1.1.41"
        }, config));
    }

    private static void CheckSetupAndAgentVersionIndependence()
    {
        var matcher = typeof(StudentApplicationUpdateTransport).GetMethod("MatchesCommittedVersions",
                          System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                      ?? throw new InvalidOperationException("Setup/Agent committed-version matcher was not found.");
        var now = DateTimeOffset.UtcNow;
        var source = new StudentApplicationUpdateSourceVersions("1.1.9", "0.4.56", 1,
            "config-fingerprint", TargetAgentVersion: "0.4.57");
        var status = new StudentAgentStatusResponse(1, "fixture", "ExampleCampus", Guid.NewGuid(), now,
            "0.4.57", "config-fingerprint", null!, null,
            StudentSetupVersion: "1.2.3",
            UpdateTransaction: new StudentUpdateTransactionStatus(Guid.NewGuid(), "committed",
                PreviousSetupVersion: "1.1.9", TargetSetupVersion: "1.2.3",
                PreviousAgentVersion: "0.4.56", TargetAgentVersion: "0.4.57"),
            Health: new StudentAgentHealthSnapshot(true, true, true, StudentVeyonServiceState.NotInstalled,
                "committed", now));
        bool Matches(string expectedSetupVersion) => (bool)(matcher.Invoke(null,
            [status, source, expectedSetupVersion]) ?? false);

        Expect(Matches("1.2.3") && !Matches("0.4.57"));
    }

    private static void CheckAgentIdentityTrustStore()
    {
        using var firstKey = RSA.Create(2048);
        using var replacementKey = RSA.Create(2048);
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-agent-trust-" + Guid.NewGuid().ToString("N"));
        var trustStore = new StudentAgentIdentityTrustStore(Path.Combine(root, "agents.json"));
        try
        {
            var firstPem = firstKey.ExportSubjectPublicKeyInfoPem();
            var first = new StudentAgentIdentityTrustCandidate("PC-01", "ExampleCampus", firstPem,
                StudentAgentResponseCryptography.GetFingerprint(firstPem));
            var pin = trustStore.Pin(first);
            Expect(pin.Fingerprint == first.Fingerprint && trustStore.FindTrustedPublicKey("ExampleCampus", "pc-01") == firstPem);

            var replacementPem = replacementKey.ExportSubjectPublicKeyInfoPem();
            var replacement = new StudentAgentIdentityTrustCandidate("PC-01", "ExampleCampus", replacementPem,
                StudentAgentResponseCryptography.GetFingerprint(replacementPem), first.Fingerprint);
            Reject(() => trustStore.Pin(replacement));
            var rotated = trustStore.Pin(replacement, replaceChangedKey: true);
            Expect(rotated.Fingerprint == replacement.Fingerprint &&
                   trustStore.FindTrustedPublicKey("ExampleCampus", "PC-01") == replacementPem);
            Expect(trustStore.Remove("ExampleCampus", "PC-01", replacement.Fingerprint));
            Expect(trustStore.FindTrustedPublicKey("ExampleCampus", "PC-01") is null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckSignedCommandAcknowledgements()
    {
        using var agentKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var campus = "ExampleCampus";
        var requestBody = "{\"policy\":\"signed request\"}";
        var nonce = Guid.NewGuid();
        var signed = StudentAgentCommandAcknowledgementCryptography.Sign(campus, nonce, requestBody,
            200, "policy accepted", agentKey, now);
        var verified = StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024);
        Expect(verified.MatchesPinnedKey && verified.Payload.Body == "policy accepted" &&
               verified.Payload.RequestSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody))));

        var wrongPin = StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody, 200, wrongKey.ExportSubjectPublicKeyInfoPem(), now, 1024);
        Expect(!wrongPin.MatchesPinnedKey);
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, Guid.NewGuid(),
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024));
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody + " ", 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024));
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, "OtherCampus", nonce,
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024));
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody, 201, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024));
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now.AddMinutes(3), 1024));
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(signed, campus, nonce,
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 4));

        var envelope = JsonSerializer.Deserialize<SignedStudentAgentResponse>(signed)!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload))
            .Replace("policy accepted", "policy rejected", StringComparison.Ordinal);
        var tampered = JsonSerializer.Serialize(envelope with
        {
            Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
        });
        Reject(() => StudentAgentCommandAcknowledgementCryptography.Verify(tampered, campus, nonce,
            requestBody, 200, agentKey.ExportSubjectPublicKeyInfoPem(), now, 1024));
    }

    private static void CheckPushFailureIsolation()
    {
        using var campusKey = RSA.Create(2048);
        using var agentKey = RSA.Create(2048);
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-agent-trust-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var publicPem = agentKey.ExportSubjectPublicKeyInfoPem();
            var trustStore = new StudentAgentIdentityTrustStore(Path.Combine(root, "agents.json"));
            foreach (var target in new[] { "unavailable-a.invalid", "unavailable-b.invalid" })
                trustStore.Pin(new StudentAgentIdentityTrustCandidate(target, "ExampleCampus", publicPem,
                    StudentAgentResponseCryptography.GetFingerprint(publicPem)));
            var results = StudentApplicationUpdateTransport.PushAsync(
                ["unavailable-a.invalid", "unavailable-b.invalid", "unavailable-unpinned.invalid"],
                "ExampleCampus", "2.0.0", campusKey,
                (target, _, _, _) => Task.FromException<(Guid CommandId, string SignedCommand)>(
                    target.StartsWith("unavailable-a", StringComparison.Ordinal)
                        ? new InvalidDataException("invalid local route")
                        : new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)),
                identityTrustStore: trustStore,
                sourceVersions: new Dictionary<string, StudentApplicationUpdateSourceVersions>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["unavailable-a.invalid"] = new("1.0.0", "1.0.0",
                        WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion),
                    ["unavailable-b.invalid"] = new("1.0.0", "1.0.0",
                        WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion)
                }).GetAwaiter().GetResult();

            Expect(results.Count == 3 && results.All(result => !result.Succeeded && result.NeedsReview));
            Expect(results[0].Target == "unavailable-a.invalid" &&
                   results[1].Target == "unavailable-b.invalid");
            Expect(results[2].Detail.Contains("尚未固定可信 Agent 身份", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student application update check failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid Student application update input was accepted.");
    }
}
