using CommunityToolkit.Mvvm.ComponentModel;
using RockcliffeCourtBooker.App.Models;

namespace RockcliffeCourtBooker.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private UiStatusTone statusTone = UiStatusTone.Neutral;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    protected void SetStatus(string message, UiStatusTone tone)
    {
        StatusMessage = message;
        StatusTone = tone;
        OnPropertyChanged(nameof(HasStatus));
    }

    protected void ClearStatus() => SetStatus(string.Empty, UiStatusTone.Neutral);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
}
