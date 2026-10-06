using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class ApplicationInventoryChoice(string target, ApplicationInventoryItem item, int computerCount = 1) : INotifyPropertyChanged
{
    private bool _isSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public int ComputerCount { get; } = computerCount;
    public ApplicationInventoryItem Item { get; } = item;
    public string DisplayName => Item.DisplayName;
    public string Publisher => Item.PublisherName ?? "未签名/无发布者条件";
    public string BinaryName => Item.BinaryName;
    public string FilePath => Item.FilePath;
    public string Summary => $"{DisplayName} · {BinaryName} · {Publisher} · {ComputerCount} 台：{Target}";
    public string RuleAvailability => RuleLine is null
        ? "仅供查看：该文件没有完整、受支持的 AppLocker 规则条件。"
        : RuleLine.StartsWith("publisher|", StringComparison.Ordinal)
            ? $"发布者规则：{Item.BinaryVersion}（精确版本）"
            : "文件哈希规则（更新文件后须重新扫描）";
    public string? RuleLine => BuildRuleLine();
    public bool CanSelect => RuleLine is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Changed();
        }
    }

    private string? BuildRuleLine()
    {
        var displayName = SafeField(DisplayName, 120);
        var binaryName = SafeField(BinaryName, 256);
        if (displayName is null || binaryName is null || !binaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (Item.PublisherName is { } publisher && Item.ProductName is { } product && Item.BinaryVersion is { } version &&
            SafeField(publisher, 512) is not null && SafeField(product, 256) is not null &&
            Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
        {
            try
            {
                ApplicationPolicyCompiler.ValidateRule(new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Publisher,
                    displayName, publisher, product, binaryName, version, version));
                return string.Join('|', "publisher", displayName, publisher, product, binaryName, version, version);
            }
            catch (InvalidDataException) { }
        }
        if (Regex.IsMatch(Item.FileSha256, "^[A-Fa-f0-9]{64}$") &&
            Regex.IsMatch(Item.AppLockerHashSha256, "^[A-Fa-f0-9]{64}$") && Item.FileLength > 0)
        {
            try
            {
                ApplicationPolicyCompiler.ValidateRule(new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Hash,
                    displayName, SourceFileName: binaryName, FileSha256: Item.FileSha256,
                    AppLockerHashSha256: Item.AppLockerHashSha256, SourceFileLength: Item.FileLength));
                return string.Join('|', "hash", displayName, binaryName, Item.FileSha256,
                    Item.AppLockerHashSha256, Item.FileLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (InvalidDataException) { }
        }
        return null;
    }

    private static string? SafeField(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value == value.Trim() &&
        !value.Any(character => char.IsControl(character) || character is '|' or '*' or '?') ? value : null;

    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
