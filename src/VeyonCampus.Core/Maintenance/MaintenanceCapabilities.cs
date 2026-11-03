namespace VeyonCampus.Core.Maintenance;

/// <summary>Versions read from authenticated component/status responses.</summary>
public sealed record MaintenanceComponentVersions
{
    public string? StudentSetupVersion { get; }
    public string? AgentRuntimeVersion { get; }
    public string? AgentAssemblyVersion { get; }
    public string? MaintenanceVersion { get; }

    public MaintenanceComponentVersions(string? studentSetupVersion, string? agentRuntimeVersion,
        string? agentAssemblyVersion, string? maintenanceVersion)
    {
        StudentSetupVersion = MaintenanceValidation.OptionalVersion(studentSetupVersion,
            nameof(studentSetupVersion));
        AgentRuntimeVersion = MaintenanceValidation.OptionalVersion(agentRuntimeVersion,
            nameof(agentRuntimeVersion));
        AgentAssemblyVersion = MaintenanceValidation.OptionalVersion(agentAssemblyVersion,
            nameof(agentAssemblyVersion));
        MaintenanceVersion = MaintenanceValidation.OptionalVersion(maintenanceVersion,
            nameof(maintenanceVersion));
    }
}

/// <summary>
/// Capability facts after the adapter has verified a signed response against the pinned Agent identity.
/// Raw signatures and private identity material are never stored in the maintenance journal.
/// </summary>
public sealed class MaintenanceCapabilities
{
    public string AgentIdentityFingerprint { get; }
    public int ProtocolVersion { get; }
    public int? StudentUpdateProtocolVersion { get; }
    public string AgentRuntimeVersion { get; }
    public string ConfigurationFingerprint { get; }
    public string? TeacherChannelFingerprint { get; }
    public string? TeacherChannelResponseSha256 { get; }
    public string? CapabilityResponseSha256 { get; }
    public string? MaintenanceVersion { get; }
    public IReadOnlyList<string> Names { get; }
    public string SignedResponseSha256 { get; }
    public string SignatureAlgorithm { get; }
    public DateTimeOffset VerifiedUtc { get; }

    public MaintenanceCapabilities(string agentIdentityFingerprint, int protocolVersion,
        int? studentUpdateProtocolVersion, string agentRuntimeVersion, string configurationFingerprint,
        string? maintenanceVersion, IReadOnlyList<string> names, string signedResponseSha256,
        string signatureAlgorithm, DateTimeOffset verifiedUtc, string? teacherChannelFingerprint = null,
        string? teacherChannelResponseSha256 = null, string? capabilityResponseSha256 = null)
    {
        AgentIdentityFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        if (protocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        ProtocolVersion = protocolVersion;
        if (studentUpdateProtocolVersion is < 1 or > 32767)
            throw new ArgumentOutOfRangeException(nameof(studentUpdateProtocolVersion));
        StudentUpdateProtocolVersion = studentUpdateProtocolVersion;
        AgentRuntimeVersion = MaintenanceValidation.OptionalVersion(agentRuntimeVersion,
            nameof(agentRuntimeVersion))!;
        ConfigurationFingerprint = MaintenanceValidation.Fingerprint(configurationFingerprint,
            nameof(configurationFingerprint));
        TeacherChannelFingerprint = teacherChannelFingerprint is null
            ? null
            : MaintenanceValidation.Fingerprint(teacherChannelFingerprint, nameof(teacherChannelFingerprint));
        TeacherChannelResponseSha256 = teacherChannelResponseSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(teacherChannelResponseSha256, nameof(teacherChannelResponseSha256));
        CapabilityResponseSha256 = capabilityResponseSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(capabilityResponseSha256, nameof(capabilityResponseSha256));
        if ((TeacherChannelFingerprint is null) != (TeacherChannelResponseSha256 is null))
            throw new InvalidDataException("教师连接能力指纹和其签名回执摘要必须同时存在。");
        if (TeacherChannelResponseSha256 is not null &&
            !string.Equals(TeacherChannelResponseSha256, CapabilityResponseSha256, StringComparison.Ordinal))
            throw new InvalidDataException("教师连接指纹摘要必须来自能力对象的签名回执。");
        MaintenanceVersion = MaintenanceValidation.OptionalVersion(maintenanceVersion,
            nameof(maintenanceVersion));
        ArgumentNullException.ThrowIfNull(names);
        var normalizedNames = names.Select(name => MaintenanceValidation.RequiredText(name,
                nameof(names), 64))
            .ToArray();
        foreach (var name in normalizedNames) MaintenanceValidation.ValidateCapabilityName(name);
        if (normalizedNames.Distinct(StringComparer.Ordinal).Count() != normalizedNames.Length)
            throw new ArgumentException("维护能力列表包含重复项。", nameof(names));
        Names = Array.AsReadOnly(normalizedNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        SignedResponseSha256 = MaintenanceValidation.Fingerprint(signedResponseSha256,
            nameof(signedResponseSha256));
        SignatureAlgorithm = MaintenanceValidation.RequiredText(signatureAlgorithm,
            nameof(signatureAlgorithm), 64);
        VerifiedUtc = MaintenanceValidation.Utc(verifiedUtc);
    }
}
