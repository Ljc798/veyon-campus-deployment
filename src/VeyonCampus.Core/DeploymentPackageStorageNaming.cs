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

    public static string CreateObjectKey(string campusName, Guid packageId, int schemaVersion = 3)
    {
        if (schemaVersion is not (3 or 4 or 5))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "配置包版本只允许 3、4 或 5。");
        return $"deployment-packages/v{schemaVersion}/{CreateFileName(campusName, packageId)}";
    }

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
