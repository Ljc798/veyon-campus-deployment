using System.ComponentModel;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class StudentAccountChoice(string target, StudentAccountInventoryItem account,
    string? displayTarget = null) : INotifyPropertyChanged
{
    private bool _isSelected = true;
    public string Target { get; } = target;
    public string DisplayTarget { get; } = displayTarget ?? target;
    public StudentAccountInventoryItem Account { get; } = account;
    public string Summary => $"{DisplayTarget} · {Account.Name}";
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
