using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

internal static class StudentApplicationUpdateChecks
{
    public static void Run()
    {
        CheckSignedCommand();
        CheckSignedAgentResponses();
        CheckSignedCommandAcknowledgements();
        CheckAgentIdentityTrustStore();
        CheckPushFailureIsolation();
        CheckUpdateHandoffRollback();
        CheckReplayStore();
    }

    private static void CheckUpdateHandoffRollback()
    {
        var method = typeof(StudentApplicationUpdateHandoff).GetMethod("ApplyAgentUpdateWithRollback",
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Student update rollback sequence was not found.");
        int Run(Action waitForPreviousExit, Action startUpdated, Func<bool> waitForUpdatedHealth,
            Action restorePrevious, Func<bool> waitForPreviousHealth, Action<Exception> reportFailure) =>
            (int)method.Invoke(null,
                [waitForPreviousExit, startUpdated, waitForUpdatedHealth, restorePrevious, waitForPreviousHealth,
                    reportFailure])!;

        var successfulSteps = new List<string>();
        var successResult = Run(
            () => successfulSteps.Add("wait-old-exit"),
            () => successfulSteps.Add("start-new"),
            () => { successfulSteps.Add("check-new-health"); return true; },
            () => throw new InvalidOperationException("Unexpected rollback."),
            () => throw new InvalidOperationException("Unexpected old-agent health check."),
            _ => throw new InvalidOperationException("Unexpected failure report."));
        Expect(successResult == 0 && successfulSteps.SequenceEqual(
            ["wait-old-exit", "start-new", "check-new-health"]));

        var rollbackSteps = new List<string>();
        Exception? reportedFailure = null;
        var rollbackResult = Run(
            () => rollbackSteps.Add("wait-old-exit"),
            () => rollbackSteps.Add("start-new"),
            () => { rollbackSteps.Add("check-new-health"); return false; },
            () => rollbackSteps.Add("restore-old"),
            () => { rollbackSteps.Add("check-old-health"); return true; },
            exception => reportedFailure = exception);
        Expect(rollbackResult == 1 && rollbackSteps.SequenceEqual(
            ["wait-old-exit", "start-new", "check-new-health", "restore-old", "check-old-health"]));
        Expect(reportedFailure is InvalidOperationException { InnerException: TimeoutException } &&
               reportedFailure.Message.Contains("旧版本已恢复", StringComparison.Ordinal));

        var updateFailure = new InvalidOperationException("updated agent failed");
        var restoreFailure = new IOException("restore failed");
        Exception? aggregatedFailure = null;
        var failedRollbackResult = Run(
            () => { },
            () => { },
            () => throw updateFailure,
            () => throw restoreFailure,
            () => false,
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
                (target, _) => Task.FromException<(Guid CommandId, string SignedCommand)>(
                    target.StartsWith("unavailable-a", StringComparison.Ordinal) ||
                    target.StartsWith("unavailable-unpinned", StringComparison.Ordinal)
                        ? new InvalidDataException("invalid local route")
                        : new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)),
                identityTrustStore: trustStore).GetAwaiter().GetResult();

            Expect(results.Count == 3 && results.All(result => !result.Succeeded && result.NeedsReview));
            Expect(results[0].Detail.Contains("invalid local route", StringComparison.Ordinal));
            Expect(results[1].Detail.Contains("目标地址", StringComparison.Ordinal));
            Expect(results[2].Detail.Contains("invalid local route", StringComparison.Ordinal));
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
