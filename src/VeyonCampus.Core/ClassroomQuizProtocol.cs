using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>A purpose-separated teacher-signed question for one classroom session.</summary>
public sealed record ClassroomQuizQuestionMessage(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid SessionId,
    Guid QuizId,
    string Question,
    ImmutableArray<ClassroomQuizChoice> Choices,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// A purpose-separated Agent-signed answer. Target identity is deliberately omitted; the Teacher
/// resolves it from the authenticated short-lived Agent grant.
/// </summary>
public sealed record ClassroomQuizAnswerMessage(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid SessionId,
    Guid QuizId,
    Guid SubmissionId,
    string ChoiceId,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>Strict, size-bounded signatures for teacher quiz questions and Agent answers.</summary>
public static class ClassroomQuizCryptography
{
    public const string QuestionPurpose = "VeyonCampus.ClassroomQuizQuestion.v1";
    public const string AnswerPurpose = "VeyonCampus.ClassroomQuizAnswer.v1";
    public const int MaximumQuestionEnvelopeBytes = 16 * 1024;
    public const int MaximumAnswerEnvelopeBytes = 8 * 1024;
    public static readonly TimeSpan MaximumAnswerLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);

    private sealed record SignedPayload(string Payload, string Signature);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string SignQuestion(ClassroomQuizTeacherSnapshot snapshot, string campusId,
        RSA teacherPrivateKey, DateTimeOffset issuedUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        if (snapshot.SchemaVersion != ClassroomQuizStore.CurrentSchemaVersion ||
            !Enum.IsDefined(snapshot.State))
            throw new InvalidDataException("课堂测验状态无效，无法签名发布。");
        ValidateQuestionMessage(CreateQuestionMessage(snapshot, campusId, issuedUtc),
            campusId, snapshot.SessionId, issuedUtc);
        return SignPayload(CreateQuestionMessage(snapshot, campusId, issuedUtc),
            teacherPrivateKey, MaximumQuestionEnvelopeBytes);
    }

    public static ClassroomQuizQuestionMessage VerifyQuestion(string signedJson, string expectedCampusId,
        Guid expectedSessionId, string pinnedTeacherPublicKeyPem, DateTimeOffset nowUtc)
    {
        var message = VerifyPayload<ClassroomQuizQuestionMessage>(signedJson, pinnedTeacherPublicKeyPem,
            MaximumQuestionEnvelopeBytes);
        ValidateQuestionMessage(message, expectedCampusId, expectedSessionId, nowUtc);
        return message;
    }

    public static string SignAnswer(string campusId, Guid sessionId, Guid quizId, Guid submissionId,
        string choiceId, RSA agentIdentityKey, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        var message = new ClassroomQuizAnswerMessage(1, AnswerPurpose, campusId, sessionId,
            quizId, submissionId, choiceId, issuedUtc, expiresUtc);
        ValidateAnswerMessage(message, campusId, sessionId, quizId, null, issuedUtc);
        return SignPayload(message, agentIdentityKey, MaximumAnswerEnvelopeBytes);
    }

    public static ClassroomQuizAnswerMessage VerifyAnswer(string signedJson, string expectedCampusId,
        Guid expectedSessionId, Guid expectedQuizId, IReadOnlySet<string> allowedChoiceIds,
        string pinnedAgentPublicKeyPem, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(allowedChoiceIds);
        var message = VerifyPayload<ClassroomQuizAnswerMessage>(signedJson, pinnedAgentPublicKeyPem,
            MaximumAnswerEnvelopeBytes);
        ValidateAnswerMessage(message, expectedCampusId, expectedSessionId, expectedQuizId,
            allowedChoiceIds, nowUtc);
        return message;
    }

    private static ClassroomQuizQuestionMessage CreateQuestionMessage(ClassroomQuizTeacherSnapshot snapshot,
        string campusId, DateTimeOffset issuedUtc) =>
        new(1, QuestionPurpose, campusId, snapshot.SessionId, snapshot.QuizId, snapshot.Question,
            snapshot.Choices, issuedUtc, snapshot.ExpiresUtc);

    private static void ValidateQuestionMessage(ClassroomQuizQuestionMessage? message,
        string expectedCampusId, Guid expectedSessionId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        ValidateUtc(nowUtc);
        if (message.SchemaVersion != 1 || message.Purpose != QuestionPurpose ||
            !string.Equals(message.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            message.SessionId == Guid.Empty || message.SessionId != expectedSessionId ||
            message.QuizId == Guid.Empty || message.IssuedUtc.Offset != TimeSpan.Zero ||
            message.ExpiresUtc.Offset != TimeSpan.Zero ||
            message.IssuedUtc > nowUtc.Add(MaximumFutureSkew) || message.IssuedUtc > message.ExpiresUtc ||
            message.ExpiresUtc <= nowUtc || message.ExpiresUtc - message.IssuedUtc > ClassroomQuizStore.MaximumLifetime)
            throw new InvalidDataException("课堂测验题目版本、范围或有效期无效。");

        if (ClassroomQuizStore.ValidateText(message.Question,
                ClassroomQuizStore.MaximumQuestionCharacters, "题目") != message.Question)
            throw new InvalidDataException("课堂测验题目格式无效。");

        if (message.Choices.IsDefault || message.Choices.Length is < ClassroomQuizStore.MinimumChoices or
                > ClassroomQuizStore.MaximumChoices)
            throw new InvalidDataException("课堂测验选项数量无效。");
        var canonicalChoices = ClassroomQuizStore.CreateChoices(message.Choices.Select(choice =>
            choice?.Text ?? throw new InvalidDataException("课堂测验选项为空。")).ToArray());
        if (!canonicalChoices.SequenceEqual(message.Choices))
            throw new InvalidDataException("课堂测验选项 ID、顺序或正文无效。");
    }

    private static void ValidateAnswerMessage(ClassroomQuizAnswerMessage? message,
        string expectedCampusId, Guid expectedSessionId, Guid expectedQuizId,
        IReadOnlySet<string>? allowedChoiceIds, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        ValidateUtc(nowUtc);
        if (message.SchemaVersion != 1 || message.Purpose != AnswerPurpose ||
            !string.Equals(message.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            message.SessionId == Guid.Empty || message.SessionId != expectedSessionId ||
            message.QuizId == Guid.Empty || message.QuizId != expectedQuizId ||
            message.SubmissionId == Guid.Empty || message.IssuedUtc.Offset != TimeSpan.Zero ||
            message.ExpiresUtc.Offset != TimeSpan.Zero ||
            message.IssuedUtc > nowUtc.Add(MaximumFutureSkew) || message.ExpiresUtc <= nowUtc ||
            message.ExpiresUtc <= message.IssuedUtc ||
            message.ExpiresUtc - message.IssuedUtc > MaximumAnswerLifetime ||
            message.ChoiceId is not ("A" or "B" or "C" or "D" or "E") ||
            allowedChoiceIds is not null && !allowedChoiceIds.Contains(message.ChoiceId))
            throw new InvalidDataException("课堂测验答案版本、范围、选项或有效期无效。");
    }

    private static string SignPayload<T>(T payload, RSA signingKey, int maximumBytes)
    {
        if (signingKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("课堂测验签名密钥位长无效。");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length is 0 || payloadBytes.Length > maximumBytes * 3 / 4)
            throw new InvalidDataException("课堂测验正文超过大小限制。");
        var envelope = new SignedPayload(Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signingKey.SignData(payloadBytes, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var signedJson = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(signedJson) > maximumBytes)
            throw new InvalidDataException("课堂测验签名封装超过大小限制。");
        return signedJson;
    }

    private static T VerifyPayload<T>(string signedJson, string pinnedPublicKeyPem, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedPublicKeyPem);
        if (Encoding.UTF8.GetByteCount(signedJson) > maximumBytes)
            throw new InvalidDataException("课堂测验签名封装超过大小限制。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedPayload>(signedJson, JsonOptions)
                           ?? throw new InvalidDataException("课堂测验签名封装为空。");
            var payloadBytes = DecodeCanonicalBase64(envelope.Payload, "课堂测验正文");
            var signature = DecodeCanonicalBase64(envelope.Signature, "课堂测验签名");
            if (payloadBytes.Length is 0 || payloadBytes.Length > maximumBytes * 3 / 4)
                throw new InvalidDataException("课堂测验正文超过大小限制。");
            PolicyJson.RejectDuplicateFields(payloadBytes);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(pinnedPublicKeyPem);
            if (publicKey.KeySize is < 2048 or > 4096 ||
                signature.Length != publicKey.KeySize / 8 ||
                !publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new InvalidDataException("课堂测验签名与固定公钥不匹配。");
            return JsonSerializer.Deserialize<T>(payloadBytes, JsonOptions)
                   ?? throw new InvalidDataException("课堂测验正文为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("课堂测验签名正文格式无效。", exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("课堂测验签名编码无效。", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("课堂测验签名无法验证。", exception);
        }
    }

    private static byte[] DecodeCanonicalBase64(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{label}为空。");
        var decoded = Convert.FromBase64String(value);
        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
            throw new InvalidDataException($"{label}编码不是规范 Base64。");
        return decoded;
    }

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂测验时间必须使用 UTC。");
    }
}
