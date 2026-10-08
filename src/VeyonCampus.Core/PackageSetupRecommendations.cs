namespace VeyonCampus.Core;

/// <summary>Optional first-deployment suggestions carried by schema v6 packages.</summary>
public sealed record PackageSetupRecommendations(bool InstallVeyon, bool RenameComputer,
    bool CreateStudentAccount, bool ChangeAdminPassword)
{
    public static PackageSetupRecommendations Default { get; } = new(
        InstallVeyon: true,
        RenameComputer: false,
        CreateStudentAccount: false,
        ChangeAdminPassword: false);
}
