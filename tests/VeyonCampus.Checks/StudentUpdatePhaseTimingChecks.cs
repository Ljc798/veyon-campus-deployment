using System.Security.Cryptography;
using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class StudentUpdatePhaseTimingChecks
{
    public static void Run()
    {
        CheckSignedV3CarriesRetryCount();
        CheckV1AndV2RemainWireCompatible();
        CheckTeacherAndAggregateReportDisplayRetries();
        Console.WriteLine("PASS signed update timing v3 retry count and legacy compatibility fixtures");
    }

    private static void CheckSignedV3CarriesRetryCount()
    {
        using var identity = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var request = CreateRequest(commandId, now);
        var phase = CreateLanPhase(now, retryCount: 2);
        var snapshot = new StudentUpdatePhaseTimingSnapshot(1, commandId, now, [phase],
            StudentUpdateEnvironmentCategories.Unknown);
        var response = StudentUpdatePhaseTimingCryptography.CreateV3(snapshot, request);
        var envelope = StudentAgentResponseCryptography.Sign(response, identity);
        var verified = StudentAgentResponseCryptography.Verify<StudentUpdatePhaseTimingResponseV3>(envelope,
            identity.ExportSubjectPublicKeyInfoPem());

        StudentUpdatePhaseTimingCryptography.ValidateV3(verified.Payload, request, request.CampusId, now);
        Expect(verified.MatchesPinnedKey && verified.Payload.Phases.Single().RetryCount == 2);
        Expect(JsonSerializer.Serialize(response).Contains("\"retryCount\":2", StringComparison.Ordinal));

        ExpectInvalid(() => StudentUpdatePhaseTimingCryptography.ValidateV3(
            response with { Phases = [phase with { RetryCount =
                StudentApplicationUpdateArtifactTransfer.MaximumRetryCount + 1 }] },
            request, request.CampusId, now));
        ExpectInvalid(() => StudentUpdatePhaseTimingCryptography.ValidateV3(
            response with { Phases = [phase with { Phase = StudentUpdatePhase.AgentStage }] },
            request, request.CampusId, now));
    }

    private static void CheckV1AndV2RemainWireCompatible()
    {
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var request = CreateRequest(commandId, now);
        var snapshot = new StudentUpdatePhaseTimingSnapshot(1, commandId, now,
            [CreateLanPhase(now, retryCount: 1)], StudentUpdateEnvironmentCategories.Unknown);

        var v1 = StudentUpdatePhaseTimingCryptography.Create(snapshot, request);
        StudentUpdatePhaseTimingCryptography.Validate(v1, request, request.CampusId, now);
        var v1Json = JsonSerializer.Serialize(v1);
        Expect(v1.Phases.Single().RetryCount is null && !v1Json.Contains("retryCount", StringComparison.Ordinal));

        var v2 = StudentUpdatePhaseTimingCryptography.CreateV2(snapshot, request);
        StudentUpdatePhaseTimingCryptography.ValidateV2(v2, request, request.CampusId, now);
        var v2Json = JsonSerializer.Serialize(v2);
        Expect(v2.Phases.Single().RetryCount is null && !v2Json.Contains("retryCount", StringComparison.Ordinal));
    }

    private static void CheckTeacherAndAggregateReportDisplayRetries()
    {
        var now = DateTimeOffset.UtcNow;
        var phase = CreateLanPhase(now, retryCount: 1);
        var row = new StudentApplicationUpdateTargetProgressItem("fixture student", "Fixture student");
        row.SetPhaseTimings([phase]);
        Expect(row.PhaseTimings.Single().Duration.Contains("重试 1 次", StringComparison.Ordinal));

        var sample = new StudentUpdatePerformanceSample("succeeded", true, "1.0.0", "1.0.0",
            StudentUpdateEnvironmentCategories.Unknown, [phase]);
        var report = StudentUpdatePerformanceReportBuilder.Create(Guid.NewGuid(), now, now.AddSeconds(1),
            1000, "1.0.1", 1, 0, 0, 0, 0, false,
            new StudentUpdatePerformancePreparation(null, null, false, null, null, null), [sample]);
        var lan = report.Groups.Single().Phases.Single(item => item.Phase == StudentUpdatePhase.LanTransfer);
        Expect(report.SchemaVersion == 2 && lan.RetryCount is
               { SampleCount: 1, TotalRetries: 1, P50Retries: 1, P95Retries: 1, MaxRetries: 1 });
        var json = System.Text.Encoding.UTF8.GetString(StudentUpdatePerformanceReportBuilder.Serialize(report));
        Expect(json.Contains("\"retryCount\"", StringComparison.Ordinal) &&
               !json.Contains("fixture student", StringComparison.Ordinal));
    }

    private static WebsitePolicyStatusRequest CreateRequest(Guid commandId, DateTimeOffset now) =>
        new(4, WebsitePolicyStatusCryptography.RequestPurposeV4, "timing-fixture", Guid.NewGuid(), now,
            commandId);

    private static StudentUpdatePhaseTiming CreateLanPhase(DateTimeOffset now, int retryCount) =>
        new(StudentUpdatePhase.LanTransfer, now.AddMilliseconds(-500), now, 500, 4096, "succeeded",
            RetryCount: retryCount);

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update timing retry count was accepted.");
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student update phase timing fixture failed.");
    }
}
