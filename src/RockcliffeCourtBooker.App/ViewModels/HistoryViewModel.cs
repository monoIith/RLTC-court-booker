using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.App.Services;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed class AttemptRowViewModel(BookingAttemptSummary model)
{
    public BookingAttemptSummary Model { get; } = model;

    public string Started => Model.StartedAt.ToLocalTime().ToString(
        "MMM d, yyyy · h:mm:ss tt",
        CultureInfo.CurrentCulture);

    public string TargetDate => Model.TargetDate.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture);

    public string Outcome => Model.Outcome switch
    {
        AttemptOutcome.NeedsAttention => "Needs attention",
        AttemptOutcome.MissedWindow => "Missed window",
        AttemptOutcome.GridInspected => "Grid inspected",
        AttemptOutcome.DryRunValidated => "Dry run validated",
        _ => Model.Outcome.ToString()
    };

    public string CourtAndTime => Model.CourtNumber is null || Model.StartTime is null
        ? "—"
        : $"Court {Model.CourtNumber} · {Model.StartTime.Value:h:mm tt}";

    public string Duration => $"{Model.DurationMinutes} min";

    public string Message => Model.Message;

    public UiStatusTone Tone => Model.Outcome switch
    {
        AttemptOutcome.Successful => UiStatusTone.Success,
        AttemptOutcome.Pending or AttemptOutcome.Preparing or
            AttemptOutcome.GridInspected or AttemptOutcome.DryRunValidated => UiStatusTone.Info,
        AttemptOutcome.NeedsAttention or AttemptOutcome.MissedWindow => UiStatusTone.Warning,
        _ => UiStatusTone.Error
    };

    public bool HasScreenshot => !string.IsNullOrWhiteSpace(Model.ScreenshotPath);

    public bool HasTrace => !string.IsNullOrWhiteSpace(Model.TracePath);
}

public sealed partial class HistoryViewModel(
    IHistoryApplicationService historyService,
    IUserDialogService dialogs,
    IExternalLauncher launcher) : ViewModelBase
{
    private readonly List<AttemptRowViewModel> allAttempts = [];
    private Guid? attemptToSelect;

    [ObservableProperty]
    private AttemptRowViewModel? selectedAttempt;

    [ObservableProperty]
    private string selectedFilter = "All outcomes";

    public IReadOnlyList<string> Filters { get; } =
        ["All outcomes", "Booked", "Diagnostics", "Needs attention", "Failed or unavailable"];

    public ObservableCollection<AttemptRowViewModel> Attempts { get; } = [];

    public bool HasAttempts => Attempts.Count > 0;

    public bool HasSelection => SelectedAttempt is not null;

    public void SelectAttempt(Guid attemptId)
    {
        attemptToSelect = attemptId;
        SelectedFilter = "All outcomes";
        SelectedAttempt = Attempts.FirstOrDefault(attempt => attempt.Model.Id == attemptId);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var attempts = await historyService.GetHistoryAsync();
            allAttempts.Clear();
            allAttempts.AddRange(attempts.Select(attempt => new AttemptRowViewModel(attempt)));
            ApplyFilter();
            ClearStatus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SetStatus("Booking history could not be loaded.", UiStatusTone.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!dialogs.Confirm(
                "Clear booking history",
                "Clear completed non-protected history? Pending, running, successful, and uncertain records are retained to prevent duplicate submissions. Failure diagnostics remain subject to the automatic 14-day retention period. Your external account JSON will not be changed."))
        {
            return;
        }

        var result = await historyService.ClearHistoryAsync();
        SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
        if (result.IsSuccess)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenScreenshot))]
    private void OpenScreenshot()
    {
        if (SelectedAttempt?.Model.ScreenshotPath is { } path)
        {
            ShowLaunchResult(launcher.Open(path));
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTrace))]
    private void OpenTrace()
    {
        if (SelectedAttempt?.Model.TracePath is { } path)
        {
            ShowLaunchResult(launcher.Open(path));
        }
    }

    private bool CanOpenScreenshot() => SelectedAttempt?.HasScreenshot == true;

    private bool CanOpenTrace() => SelectedAttempt?.HasTrace == true;

    private void ApplyFilter()
    {
        IEnumerable<AttemptRowViewModel> filtered = SelectedFilter switch
        {
            "Booked" => allAttempts.Where(attempt => attempt.Model.Outcome == AttemptOutcome.Successful),
            "Diagnostics" => allAttempts.Where(attempt =>
                attempt.Model.Outcome is AttemptOutcome.GridInspected or AttemptOutcome.DryRunValidated),
            "Needs attention" => allAttempts.Where(attempt =>
                attempt.Model.Outcome is AttemptOutcome.NeedsAttention or AttemptOutcome.MissedWindow),
            "Failed or unavailable" => allAttempts.Where(attempt =>
                attempt.Model.Outcome is AttemptOutcome.Failed or AttemptOutcome.Unavailable),
            _ => allAttempts
        };

        Attempts.Clear();
        foreach (var attempt in filtered)
        {
            Attempts.Add(attempt);
        }

        SelectedAttempt = attemptToSelect is { } attemptId
            ? Attempts.FirstOrDefault(attempt => attempt.Model.Id == attemptId) ?? Attempts.FirstOrDefault()
            : Attempts.FirstOrDefault();
        OnPropertyChanged(nameof(HasAttempts));
    }

    private void ShowLaunchResult(OperationResult result)
    {
        if (!result.IsSuccess)
        {
            SetStatus(result.Message, UiStatusTone.Error);
        }
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedAttemptChanged(AttemptRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OpenScreenshotCommand.NotifyCanExecuteChanged();
        OpenTraceCommand.NotifyCanExecuteChanged();
    }
}
