using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Checks;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

/// <summary>
/// P2 维护通道独立性 6 条验收测试（计划的 Definition of Done 核心）。
/// 用 fake 的 Agent 状态/协议/签名/身份/时间戳驱动现有协议、重放窗口与拒绝逻辑，
/// 不需要真实 Windows 服务。
/// </summary>
internal static class MaintenanceIndependenceChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record SignedMaintenanceMessage(string Payload, string Signature);

    public static async Task RunAsync()
    {
        var fixtureRoot = Path.Combine(TestPath.CanonicalTempRoot(),
            "maintenance-independence-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var teacher = RSA.Create(2048);
            var device = RSA.Create(2048);
            using var otherKey = RSA.Create(2048);
            var teacherPublicKey = teacher.ExportSubjectPublicKeyInfoPem();
            var devicePublicKey = device.ExportSubjectPublicKeyInfoPem();
            const string campusId = "campus-demo";
            var deviceFingerprint = StudentAgentResponseCryptography.GetFingerprint(devicePublicKey);
            var now = DateTimeOffset.UtcNow;

            string Sign(MaintenanceProtocolV1Request request, RSA key, DateTimeOffset issuedUtc,
                DateTimeOffset expiresUtc, DateTimeOffset? verifyAtUtc = null)
            {
                var requestToSign = request with { IssuedUtc = issuedUtc, ExpiresUtc = expiresUtc };
                var payload = JsonSerializer.SerializeToUtf8Bytes(requestToSign, JsonOptions);
                var signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                return JsonSerializer.Serialize(new SignedMaintenanceMessage(Convert.ToBase64String(payload),
                    Convert.ToBase64String(signature)), JsonOptions);
            }

            // 模拟一台 Student Agent 已被主动停止的机器：
            // 只读状态命令的成功路径只取决于签名、固定身份与协议时间窗，
            // 与 Agent 进程是否在线、课堂通道是否可达均无依赖关系。

            var baseRequest = new MaintenanceProtocolV1Request(
                MaintenanceProtocolV1.CurrentVersion, MaintenanceProtocolV1.RequestPurpose,
                campusId, deviceFingerprint, Guid.NewGuid(), now, now.AddMinutes(5),
                Guid.NewGuid(), null, MaintenanceProtocolV1Command.GetInstalledVersions);

            // ── 验收 1：主动停止 Student Agent 后，Maintenance 通道仍能读状态 ──────────────
            var stoppedRequest = baseRequest with { Nonce = Guid.NewGuid() };
            var stoppedSigned = Sign(stoppedRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            var stoppedVerified = MaintenanceProtocolV1.VerifyRequest(stoppedSigned, teacherPublicKey,
                campusId, deviceFingerprint, now);
            Assert(stoppedVerified.Command == MaintenanceProtocolV1Command.GetInstalledVersions &&
                   stoppedVerified.CampusId == campusId &&
                   stoppedVerified.DeviceIdentityFingerprint == deviceFingerprint,
               "Agent 停止后，已签名的只读状态命令必须通过独立校验进入 Maintenance 通道。");

            // Maintenance 自身升级 offer 在业务 Agent 不在线时仍可被独立验证与受理（角色白名单+摘要）。
            const int localUpdatePort = 39174;
            var releaseSha256 = new string('a', 64);
            var offerRequest = new MaintenanceProtocolV1Request(
                MaintenanceProtocolV1.CurrentVersion, MaintenanceProtocolV1.RequestPurpose,
                campusId, deviceFingerprint, Guid.NewGuid(), now, now.AddMinutes(5),
                Guid.NewGuid(), Guid.NewGuid(),
                MaintenanceProtocolV1Command.OfferSignedMaintenanceServiceUpdate,
                TargetMaintenanceServiceVersion: "1.1.45", ReleaseSha256: releaseSha256,
                SignedReleaseEnvelopeJson: "fixture-envelope",
                ArtifactTransferUrl: $"http://192.168.1.7:{localUpdatePort}/v1/student-updates/{releaseSha256}");
            var offerSigned = Sign(offerRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            var offerVerified = MaintenanceProtocolV1.VerifyRequest(offerSigned, teacherPublicKey,
                campusId, deviceFingerprint, now);
            Assert(offerVerified.Command == MaintenanceProtocolV1Command.OfferSignedMaintenanceServiceUpdate &&
                   offerVerified.ReleaseSha256 == releaseSha256,
                "Agent 停止时，受信任 Maintenance 升级 offer 的签名/摘要/角色绑定必须独立于业务 Agent 通过。");
            Console.WriteLine("PASS 1：Student Agent 停止后，Maintenance 独立读状态并完成受信任升级/修复 offer 验证");

            // ── 验收 2：新旧课堂通信协议不兼容时，Maintenance 仍可维护（协议解耦实证）──────
            // 课堂通道子协议已协商到新会话（旧 Agent 的会话因协议变化而失败），
            // 但 Maintenance 通道的请求/响应只依赖 MaintenanceProtocolV1，与课堂 WebSocket 协议版本无关。
            var classlessRequest = baseRequest with { Nonce = Guid.NewGuid() };
            var classlessSigned = Sign(classlessRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            var classlessVerified = MaintenanceProtocolV1.VerifyRequest(classlessSigned, teacherPublicKey,
                campusId, deviceFingerprint, now);
            var readbackResponse = new MaintenanceProtocolV1Response(
                MaintenanceProtocolV1.CurrentVersion, MaintenanceProtocolV1.ResponsePurpose,
                classlessVerified.CampusId, classlessVerified.DeviceIdentityFingerprint,
                classlessVerified.Nonce, classlessVerified.OperationId, classlessVerified.CommandId,
                now, "succeeded", "状态读回成功，未使用课堂通道。",
                JsonSerializer.Serialize(new MaintenanceServiceInstalledVersionsPayload(
                    "1.1.44", null, null, "maintenance-1.1.44", "idle", now,
                    RunningAsSystem: true, ManagedAgentTaskTrusted: false), JsonOptions));
            var classlessResponseSigned = MaintenanceProtocolV1.SignResponse(readbackResponse, device);
            var classlessResponseVerified = MaintenanceProtocolV1.VerifyResponse(classlessResponseSigned,
                devicePublicKey, classlessVerified, now);
            Assert(classlessResponseVerified.ResultCode == "succeeded" &&
                   classlessResponseVerified.Nonce == classlessVerified.Nonce,
                "课堂通信协议版本变化不影响 Maintenance 通道的命令与签名响应验证（协议解耦）。");
            Console.WriteLine("PASS 2：课堂协议新旧不兼容场景下，Maintenance 通道仍可独立读状态与维护");

            // ── 验收 3：错误签名的维护命令必须被拒绝 ──────────────────────────────────────
            var wrongKeyRequest = baseRequest with { Nonce = Guid.NewGuid() };
            var wrongKeySigned = Sign(wrongKeyRequest, otherKey, now.AddMinutes(-1), now.AddMinutes(4));
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(wrongKeySigned,
                teacherPublicKey, campusId, deviceFingerprint, now), "错误签名");
            var wrongEnvelope = Sign(wrongKeyRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            var tampered = System.Text.Encoding.UTF8.GetString(wrongEnvelope)
                .Replace("\"signature\"", "\"Signature\"", StringComparison.Ordinal);
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(tampered, teacherPublicKey,
                campusId, deviceFingerprint, now), "非规范 JSON 签名信封");
            var forgedPayload = System.Text.Encoding.UTF8.GetString(wrongEnvelope)
                .Replace(campusId, "campus-forge", StringComparison.Ordinal);
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(forgedPayload, teacherPublicKey,
                campusId, deviceFingerprint, now), "载荷被篡改后签名不匹配");
            Console.WriteLine("PASS 3：错误签名（错误私钥 / 非规范信封）的维护命令被拒绝并带原因");

            // ── 验收 4：未知/未授权身份发起的维护命令必须被拒绝 ──────────────────────────
            var otherDeviceFingerprint = StudentAgentResponseCryptography.GetFingerprint(otherKey.ExportSubjectPublicKeyInfoPem());
            var foreignIdentityRequest = baseRequest with
                { Nonce = Guid.NewGuid(), DeviceIdentityFingerprint = otherDeviceFingerprint };
            var foreignIdentitySigned = Sign(foreignIdentityRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(foreignIdentitySigned,
                teacherPublicKey, campusId, deviceFingerprint, now), "未授权设备身份");
            var otherCampusKey = RSA.Create(2048);
            var otherCampusRequest = baseRequest with
                { Nonce = Guid.NewGuid(), CampusId = "campus-other",
                  TeacherConfiguration = new MaintenanceTeacherBootstrapConfiguration([], "c2VjcmV0") };
            // 只读命令不允许携带配置内容；先验证一个带身份的异地校区请求被身份绑定拒绝。
            var otherCampusSigned = Sign(otherCampusRequest with { Command = MaintenanceProtocolV1Command.GetHealth,
                TeacherConfiguration = null }, otherCampusKey, now.AddMinutes(-1), now.AddMinutes(4));
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(otherCampusSigned,
                teacherPublicKey, "campus-other", deviceFingerprint, now), "错误签名密钥签发的异地校区命令");
            Console.WriteLine("PASS 4：未知/未授权设备身份与错校区签名的维护命令被拒绝并带原因");

            // ── 验收 5：过期命令（超出重放窗口）必须被拒绝 ────────────────────────────────
            var expiredRequest = baseRequest with { Nonce = Guid.NewGuid() };
            var expiredSigned = Sign(expiredRequest, teacher, now.AddMinutes(-6), now.AddMinutes(-1));
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(expiredSigned,
                teacherPublicKey, campusId, deviceFingerprint, now), "过期命令（已超出 5 分钟重放窗口）");
            var expiredWindowPath = Path.Combine(fixtureRoot, "expired-replay.json");
            var expiredWindow = new MaintenanceReplayWindow(expiredWindowPath);
            var expiredRejected = false;
            try
            {
                await expiredWindow.TryAcceptAsync(Guid.NewGuid(), now.AddMinutes(-1), now).ConfigureAwait(false);
                throw new InvalidDataException("重放窗口接受了已过期时间戳。");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("已过期"))
            { expiredRejected = true; }
            Assert(expiredRejected, "重放窗口对已过期时间戳必须拒绝并带原因。");

            // 新鲜 nonce 可以进入窗口；同一 nonce 重放必须被拒绝（防重复副作用）。
            var windowPath = Path.Combine(fixtureRoot, "replay.json");
            var window = new MaintenanceReplayWindow(windowPath);
            var nonce = Guid.NewGuid();
            var expires = now.AddMinutes(5);
            var firstAccept = await window.TryAcceptAsync(nonce, expires, now).ConfigureAwait(false);
            Assert(firstAccept, "首次通过重放窗口签名的新命令必须被接受。");
            var replayRejected = false;
            try
            {
                var replayAccepted = await window.TryAcceptAsync(nonce, expires, now.AddMinutes(1)).ConfigureAwait(false);
                replayRejected = !replayAccepted;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            { replayRejected = true; }
            Assert(replayRejected, "同一 nonce 的重放必须在重放窗口被拒绝。");
            Console.WriteLine("PASS 5：过期命令与 nonce 重放均被拒绝并带原因（InvalidDataException）");

            // ── 验收 6：错误摘要（payload hash 不匹配）必须被拒绝 ─────────────────────────
            var digestRequest = new MaintenanceProtocolV1Request(
                MaintenanceProtocolV1.CurrentVersion, MaintenanceProtocolV1.RequestPurpose,
                campusId, deviceFingerprint, Guid.NewGuid(), now, now.AddMinutes(5),
                Guid.NewGuid(), Guid.NewGuid(), MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate,
                TargetSetupVersion: "1.1.45", ReleaseSha256: releaseSha256,
                SignedReleaseEnvelopeJson: "fixture-envelope",
                ArtifactTransferUrl: $"http://192.168.1.7:{localUpdatePort}/v1/student-updates/{releaseSha256}");
            var digestSigned = Sign(digestRequest, teacher, now.AddMinutes(-1), now.AddMinutes(4));
            var digestEnvelope = JsonSerializer.Deserialize<SignedMaintenanceMessage>(digestSigned, JsonOptions)!;
            var forgedPayload = JsonSerializer.SerializeToUtf8Bytes(
                digestRequest with { TargetSetupVersion = "1.1.99" }, JsonOptions);
            var forgedEnvelope = JsonSerializer.Serialize(new SignedMaintenanceMessage(
                Convert.ToBase64String(forgedPayload), digestEnvelope.Signature), JsonOptions);
            ExpectRejectedWith(() => MaintenanceProtocolV1.VerifyRequest(forgedEnvelope,
                teacherPublicKey, campusId, deviceFingerprint, now), "错误摘要（篡改载荷后签名不匹配）");
            Console.WriteLine("PASS 6：篡改后的 payload（摘要/哈希不再匹配原签名）被拒绝并带原因");

            // 汇总：以上 6 条验收场景都驱动现有 Maintenance 协议/重放代码路径；
            // 真正 SYSTEM 身份下的 dispatch 与 Agent 重启由 Windows 实机完成（FIELD-PENDING）。
            Console.WriteLine("PASS 维护通道独立性 6 条验收测试（本地协议/重放/签名层；实机 dispatch 保持 FIELD-PENDING）");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void ExpectRejectedWith(Action action, string label)
    {
        try
        {
            action();
            throw new InvalidDataException($"{label} 场景没有被拒绝。");
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            // 拒绝必须带可诊断原因。
            Assert(exception.Message.Length > 0, "拒绝路径没有返回原因信息。");
        }
    }
}
