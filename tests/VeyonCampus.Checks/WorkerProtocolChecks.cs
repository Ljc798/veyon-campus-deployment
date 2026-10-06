using System.Buffers.Binary;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class WorkerProtocolChecks
{
    public static void Run()
    {
        var caller = new WorkerCallerIdentity(412, "S-1-5-21-11-22-33-1001", 3,
            Path.Combine(TestPath.CanonicalTempRoot(), "VeyonCampus.StudentSetup.exe"), "0.4.45",
            VeyonCampusRole.StudentSetup, DateTimeOffset.UtcNow.UtcTicks);
        var secret = Encoding.UTF8.GetBytes("safe-password-123");
        var request = new PrivilegedWorkerRequest(PrivilegedWorkerProtocol.CurrentVersion,
            Guid.NewGuid(), caller, PrivilegedWorkerOperation.CreateStudentAccount,
            AccountName: "Student01", SecretUtf8: secret);
        var payload = PrivilegedWorkerProtocol.SerializeRequest(request);
        try
        {
            var decoded = PrivilegedWorkerProtocol.DeserializeRequest(payload);
            Expect(decoded.RequestId == request.RequestId && decoded.Caller == caller &&
                   decoded.Operation == PrivilegedWorkerOperation.CreateStudentAccount &&
                   decoded.SecretUtf8 is not null && decoded.SecretUtf8.SequenceEqual(secret));

            using var stream = new MemoryStream();
            PrivilegedWorkerProtocol.WriteFrameAsync(stream, payload).GetAwaiter().GetResult();
            stream.Position = 0;
            var framed = PrivilegedWorkerProtocol.ReadFrameAsync(stream).GetAwaiter().GetResult();
            Expect(framed.SequenceEqual(payload));
            Array.Clear(framed);

            var json = Encoding.UTF8.GetString(payload);
            Reject(() => PrivilegedWorkerProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(
                json.Replace("\"ProtocolVersion\":1", "\"ProtocolVersion\":1,\"Unknown\":true", StringComparison.Ordinal))));
            Reject(() => PrivilegedWorkerProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(
                json.Replace("\"ProtocolVersion\":1", "\"ProtocolVersion\":1,\"ProtocolVersion\":1", StringComparison.Ordinal))));
            Reject(() => PrivilegedWorkerProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(
                json.Replace("\"CreateStudentAccount\"", "99", StringComparison.Ordinal))));
            Reject(() => PrivilegedWorkerProtocol.DeserializeRequest(new byte[PrivilegedWorkerProtocol.MaximumFrameBytes + 1]));

            var longPassword = Encoding.UTF8.GetBytes(new string('a', 128));
            Reject(() => PrivilegedWorkerProtocol.SerializeRequest(request with { SecretUtf8 = longPassword }));
            Array.Clear(longPassword);
            var controlledPassword = Encoding.UTF8.GetBytes("bad\npassword");
            Reject(() => PrivilegedWorkerProtocol.SerializeRequest(request with { SecretUtf8 = controlledPassword }));
            Array.Clear(controlledPassword);
            Reject(() => PrivilegedWorkerProtocol.SerializeRequest(request with
            {
                Caller = caller with { UserSid = "S-1-5-18" }
            }));
            Reject(() => PrivilegedWorkerProtocol.SerializeRequest(request with
            {
                Operation = PrivilegedWorkerOperation.RenameComputer,
                ComputerName = "PC-01",
                SecretUtf8 = secret
            }));

            var response = new PrivilegedWorkerResponse(PrivilegedWorkerProtocol.CurrentVersion,
                request.RequestId, new StepResult("student-account", ExecutionPlan.Cancelled,
                    "管理员权限确认已取消。"));
            Expect(PrivilegedWorkerProtocol.DeserializeResponse(
                PrivilegedWorkerProtocol.SerializeResponse(response)).Result.Status == ExecutionPlan.Cancelled);
            Expect(PrivilegedWorkerDispatcher.GetStepId(PrivilegedWorkerOperation.AddVeyonRoom) == "room-directory");

            var oversizedHeader = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader, PrivilegedWorkerProtocol.MaximumFrameBytes + 1);
            using var oversizedStream = new MemoryStream(oversizedHeader);
            Reject(() => PrivilegedWorkerProtocol.ReadFrameAsync(oversizedStream).GetAwaiter().GetResult());
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }

        static void Expect(bool condition)
        {
            if (!condition) throw new Exception("Worker protocol assertion failed.");
        }

        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is InvalidDataException or IOException) { return; }
            throw new Exception("Worker protocol accepted an invalid request.");
        }
    }
}
