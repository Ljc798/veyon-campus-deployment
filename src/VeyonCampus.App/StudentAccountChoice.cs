using System.ComponentModel;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class StudentAccountChoice(string target, StudentAccountInventoryItem account) : INotifyPropertyChanged
{
    private bool _isSelected;
    public string Target { get; } = target;
    public StudentAccountInventoryItem Account { get; } = account;
    public string Summary => $"{Target} · {Account.Name}";
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
