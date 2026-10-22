using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VeyonCampus.App;

public sealed record ApplicationCatalogCandidateReference(string Target, string Id);

public sealed class ApplicationCatalogProductChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public string ProductName { get; }
    public string CompanyName { get; }
    public string DisplayName { get; }
    public string Summary { get; }
    public string Details { get; }
    public IReadOnlyList<ApplicationCatalogCandidateReference> Candidates { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public ApplicationCatalogProductChoice(string productName, string companyName, string displayName,
        string summary, string details, IReadOnlyList<ApplicationCatalogCandidateReference> candidates)
    {
        ProductName = productName;
        CompanyName = companyName;
        DisplayName = displayName;
        Summary = summary;
        Details = details;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
