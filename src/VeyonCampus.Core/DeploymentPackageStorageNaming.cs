using System.Text;

namespace VeyonCampus.Core;

/// <summary>Creates a readable, unique object name for a published campus package.</summary>
public static class DeploymentPackageStorageNaming
{
    public static string CreateFileName(string campusName, Guid packageId)
    {
        if (string.IsNullOrWhiteSpace(campusName))
            throw new ArgumentException("校区名称不能为空。", nameof(campusName));

        return $"{CreateCampusFileStem(campusName)}-{packageId:N}.zip";
    }

    public static string CreateObjectKey(string campusName, Guid packageId) =>
        $"deployment-packages/v3/{CreateFileName(campusName, packageId)}";

    private static string CreateCampusFileStem(string campusName)
    {
        var normalized = campusName.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            result.Append(char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '-'
                : character);
        }

        var stem = result.ToString().Trim(' ', '.');
        return stem.Length == 0 ? "campus" : stem;
    }
}
