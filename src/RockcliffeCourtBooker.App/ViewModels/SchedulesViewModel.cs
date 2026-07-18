using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.App.Services;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed class ScheduleRowViewModel(ScheduleSummary model, IReadOnlyDictionary<string, string> playerNames)
{
    public ScheduleSummary Model { get; } = model;

    public Guid Id => Model.Id;

    public string Name => Model.Name;

    public string Pattern => Model.Kind == RuleKind.OneTime
        ? Model.TargetDate?.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture) ?? "Date missing"
        : string.Join(", ", Model.Weekdays.Select(day => day.ToString()[..3]));

    public string TimeChoices => string.Join(" → ", Model.StartTimes.Select(time =>
        time.ToString("h:mm tt", CultureInfo.CurrentCulture)));

    public string Booking => $"{Model.BookingType} · {Model.DurationMinutes} min";

    public string Players => string.Join(", ", Model.PlayerIds.Select(id =>
        playerNames.TryGetValue(id, out var name) ? name : id));

    public string Courts => string.Join(" → ", Model.CourtOrder.Select(court =>
        court.ToString(CultureInfo.CurrentCulture)));

    public string NextAttempt => Model.NextAttempt?.ToLocalTime().ToString(
        "ddd, MMM d · h:mm tt",
        CultureInfo.CurrentCulture) ?? "Not scheduled";

    public string Status => Model.Status;

    public bool IsEnabled => Model.IsEnabled;

    public string ToggleLabel => Model.IsEnabled ? "Pause" : "Enable";
}

public sealed partial class SchedulesViewModel(
    ApplicationState applicationState,
    IBookingApplicationService bookingService,
    IUserDialogService dialogs) : ViewModelBase
{
    [ObservableProperty]
    private ScheduleRowViewModel? selectedSchedule;

    [ObservableProperty]
    private string schedulerStatus = "Checking scheduler…";

    [ObservableProperty]
    private string schedulerDetail = string.Empty;

    [ObservableProperty]
    private string nextSchedulerRun = "—";

    [ObservableProperty]
    private UiStatusTone schedulerTone = UiStatusTone.Neutral;

    public ObservableCollection<ScheduleRowViewModel> Schedules { get; } = [];

    public bool HasSchedules => Schedules.Count > 0;

    public event Action<Guid>? EditRequested;

    public void SetStatusFromShell(string message, UiStatusTone tone) => SetStatus(message, tone);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        UpdateCommandStates();
        try
        {
            var schedules = await bookingService.GetSchedulesAsync();
            var playerNames = applicationState.Players.ToDictionary(
                player => player.Id,
                player => player.DisplayName,
                StringComparer.OrdinalIgnoreCase);

            Schedules.Clear();
            foreach (var schedule in schedules)
            {
                Schedules.Add(new ScheduleRowViewModel(schedule, playerNames));
            }

            SelectedSchedule = Schedules.FirstOrDefault();
            OnPropertyChanged(nameof(HasSchedules));

            var health = await bookingService.GetSchedulerHealthAsync();
            SchedulerStatus = health.Status;
            SchedulerDetail = health.Detail;
            NextSchedulerRun = health.NextRun?.ToLocalTime().ToString(
                "ddd, MMM d · h:mm tt",
                CultureInfo.CurrentCulture) ?? "No run scheduled";
            SchedulerTone = health.IsHealthy ? UiStatusTone.Success : UiStatusTone.Warning;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SetStatus("Schedules could not be loaded. Check the diagnostics and try again.", UiStatusTone.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (SelectedSchedule is not null)
        {
            EditRequested?.Invoke(SelectedSchedule.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedSchedule is null)
        {
            return;
        }

        await RunSelectedActionAsync(() => bookingService.SetScheduleEnabledAsync(
            SelectedSchedule.Id,
            !SelectedSchedule.IsEnabled));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedSchedule is null ||
            !dialogs.Confirm("Delete schedule", $"Delete '{SelectedSchedule.Name}'? This cannot be undone."))
        {
            return;
        }

        await RunSelectedActionAsync(() => bookingService.DeleteScheduleAsync(SelectedSchedule.Id));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task InspectNowAsync()
    {
        if (SelectedSchedule is null)
        {
            return;
        }

        await RunSelectedActionAsync(
            () => bookingService.InspectNowAsync(SelectedSchedule.Id),
            refreshAfter: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DryRunAsync()
    {
        if (SelectedSchedule is null)
        {
            return;
        }

        if (!dialogs.Confirm(
                "Run supervised dry run",
                "Open a visible browser and continue through player selection and $0.00 validation? The final Book button will not be clicked."))
        {
            return;
        }

        await RunSelectedActionAsync(
            () => bookingService.DryRunAsync(SelectedSchedule.Id),
            refreshAfter: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BookNowAsync()
    {
        if (SelectedSchedule is null)
        {
            return;
        }

        var target = await bookingService.GetBookNowTargetAsync(SelectedSchedule.Id);
        if (!target.IsAvailable || target.TargetDate is not { } targetDate)
        {
            SetStatus(target.Message, UiStatusTone.Error);
            return;
        }

        if (!dialogs.Confirm(
                "Book now",
                $"Run this booking for {targetDate:dddd, MMMM d, yyyy}? " +
                "The worker will still fail closed if players, price, or availability do not match."))
        {
            return;
        }

        await RunSelectedActionAsync(
            () => bookingService.BookNowAsync(SelectedSchedule.Id, targetDate),
            refreshAfter: false);
    }

    private bool HasSelection() => SelectedSchedule is not null && !IsBusy;

    private async Task RunSelectedActionAsync(
        Func<Task<OperationResult>> action,
        bool refreshAfter = true)
    {
        IsBusy = true;
        UpdateCommandStates();
        try
        {
            var result = await action();
            SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
            if (result.IsSuccess && refreshAfter)
            {
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private void UpdateCommandStates()
    {
        EditCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        InspectNowCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
        BookNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedScheduleChanged(ScheduleRowViewModel? value) => UpdateCommandStates();
}
