using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdAdminTool.Models;

public class AdComputer : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string DnsHostName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string DistinguishedName { get; set; } = "";

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
        }
    }

    public string Status => IsOnline ? "Онлайн" : "Офлайн";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}