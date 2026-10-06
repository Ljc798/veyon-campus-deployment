using System.Text.Json;

namespace VeyonCampus.Core;

internal static class PolicyJson
{
    public static void RejectDuplicateFields(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);
    }
    private static void Visit(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("策略 JSON 包含重复字段。");
                Visit(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Visit(child);
    }
}
