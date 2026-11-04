namespace VeyonCampus.Core;

public enum CampusPackageSchemaMigrationDisposition
{
    NoMigration,
    ExplicitRebuild7To8,
    Rejected
}

/// <summary>
/// A single, allow-listed migration step. Every plan states which inputs must be supplied by an
/// operator and which safety properties the writer must uphold; none of them imply that the
/// transformation has already been executed or verified on a real machine.
/// </summary>
public sealed record CampusPackageSchemaMigrationPlan(
    int FromSchemaVersion,
    int ToSchemaVersion,
    CampusPackageSchemaMigrationDisposition Disposition,
    string Reason,
    IReadOnlyList<string> RequiredInputs,
    bool RequiresSignedRotationAuthorizations,
    bool RequiresAtomicReplace,
    bool RequiresReadbackVerification,
    bool RequiresOriginalByteRestore,
    bool AllowsImplicitFieldInference);

/// <summary>
/// Migration policy for campus configuration packages (CM-03).
///
/// Published facts, taken from PackageManifest.Load / PackageBuilder.BuildCore:
///   schemaVersion 7 = teacherChannel (1-16 endpoints + .cer root), full policy keys,
///                     Student Agent compatibility range, no rotation authorizations.
///   schemaVersion 8 = everything in 7 plus 1-3 signed policyTrustRotations authorizations;
///                     each authorization must match the campus, use a distinct key role,
///                     and its new public key must equal the corresponding key inside the package.
///   schemaVersion 9 = NOT PUBLISHED. No field shape, no verifier, no writer exists.
///
/// Therefore 7->8 is an explicit, operator-driven package rebuild, never a silent field
/// inference from an old archive; 8->9 and every downgrade / skipped step are rejected.
/// </summary>
public static class CampusPackageSchemaMigration
{
    public const int MinimumPublishedVersion = 1;
    public const int TeacherChannelVersion = 7;
    public const int PolicyTrustRotationVersion = 8;
    public const int MaximumPublishedVersion = 8;
    public const int ReservedNextVersion = 9;

    private static readonly string[] SevenToEightInputs =
    [
        "完整 teacherChannel：1–16 个连接地址与 .cer TLS 根证书",
        "网站 / 应用 / 系统策略公钥与 Student Agent 兼容范围（沿用 schemaVersion 7 的全部约束）",
        "1–3 个 PolicyTrustRotation 双签名授权：校区、密钥用途、旧根指纹、候选根指纹、退休时间",
        "包内对应策略公钥与授权中的新公钥逐字节一致"
    ];

    public static bool IsPublished(int version) =>
        version >= MinimumPublishedVersion && version <= MaximumPublishedVersion;

    public static CampusPackageSchemaMigrationPlan Plan(int from, int to)
    {
        if (from == to && IsPublished(from))
            return new(from, to, CampusPackageSchemaMigrationDisposition.NoMigration,
                "同版本无需迁移；按原 schema 校验并读回即可。", [], false, false, false, false, false);
        if (from == TeacherChannelVersion && to == PolicyTrustRotationVersion)
            return new(from, to, CampusPackageSchemaMigrationDisposition.ExplicitRebuild7To8,
                "7→8 增加策略信任根轮换授权。必须由操作者先生成候选根与双签名授权，再显式重建包；" +
                "旧包里没有轮换字段，禁止静默推导或自动补全。",
                Array.AsReadOnly(SevenToEightInputs), true, true, true, true, false);
        if (to > MaximumPublishedVersion)
            return Reject(from, to,
                $"schemaVersion={to} 尚未发布字段结构；当前最高已发布版本为 {MaximumPublishedVersion}，" +
                "不得虚构目标 schema 或伪造迁移结果。");
        if (to < from)
            return Reject(from, to, "禁止降级：新 schema 的字段无法由旧包反推，且会让旧根提前退休。");
        if (to > from + 1)
            return Reject(from, to, "禁止跳级：必须逐级迁移并在每一级完成原子替换与读回校验。");
        if (!IsPublished(from) || !IsPublished(to))
            return Reject(from, to, "版本不在已发布 1–8 范围内。");
        return Reject(from, to, "该版本对没有已定义的字段变换；不要自动补全或替换包内容。");
    }

    /// <summary>
    /// Guards the only allowed upgrade. PackageBuilder already verifies the cryptographic
    /// authorization contents; this check makes the migration prerequisites explicit at the
    /// point where a schemaVersion=8 package is produced.
    /// </summary>
    public static void RequireExplicitRebuildInputs(
        IReadOnlyList<string>? rotationAuthorizations,
        IReadOnlyList<string>? teacherChannelEndpoints,
        byte[]? teacherChannelRootCertificate,
        string? studentSystemPolicyPublicKeyPem)
    {
        var plan = Plan(TeacherChannelVersion, PolicyTrustRotationVersion);
        if (plan.Disposition != CampusPackageSchemaMigrationDisposition.ExplicitRebuild7To8 ||
            plan.AllowsImplicitFieldInference)
            throw new InvalidDataException("7→8 迁移计划未获允许；拒绝生成 schemaVersion=8 配置包。");
        if (rotationAuthorizations is null or { Count: < 1 or > 3 })
            throw new InvalidDataException("7→8 必须显式提供 1 至 3 个策略信任根轮换授权。");
        if (teacherChannelEndpoints is null or { Count: 0 } ||
            teacherChannelRootCertificate is null or { Length: 0 })
            throw new InvalidDataException("7→8 必须保留完整 teacherChannel（地址与根证书）。");
        if (string.IsNullOrWhiteSpace(studentSystemPolicyPublicKeyPem))
            throw new InvalidDataException("7→8 必须保留系统策略公钥等 schemaVersion 7 的完整载荷。");
    }

    private static CampusPackageSchemaMigrationPlan Reject(int from, int to, string reason) =>
        new(from, to, CampusPackageSchemaMigrationDisposition.Rejected, reason, [],
            false, false, false, false, false);
}
