using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RockcliffeCourtBooker.App.Services;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IBookingApplicationService bookingService;
    private bool initialized;

    [ObservableProperty]
    private NavigationItemViewModel? selectedNavigation;

    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private string currentPageTitle = "Setup";

    [ObservableProperty]
    private string currentPageDescription = "Connect and validate your Matchpoint account configuration.";

    public MainViewModel(
        ApplicationState applicationState,
        IBookingApplicationService bookingService,
        SetupViewModel setup,
        NewBookingViewModel newBooking,
        SchedulesViewModel schedules,
        HistoryViewModel history)
    {
        this.bookingService = bookingService;
        ApplicationState = applicationState;
        Setup = setup;
        NewBooking = newBooking;
        Schedules = schedules;
        History = history;

        NavigationItems =
        [
            new("Setup", "\uE713", "Account and diagnostics", Setup),
            new("New booking", "\uE787", "Date, time, and players", NewBooking),
            new("Schedules", "\uE823", "Upcoming booking rules", Schedules),
            new("History", "\uE81C", "Results and diagnostics", History)
        ];

        NewBooking.RuleSaved += OnRuleSaved;
        Schedules.EditRequested += OnEditRequested;
        SelectedNavigation = NavigationItems[0];
    }

    public ApplicationState ApplicationState { get; }

    public SetupViewModel Setup { get; }

    public NewBookingViewModel NewBooking { get; }

    public SchedulesViewModel Schedules { get; }

    public HistoryViewModel History { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public string AdapterStatus { get; } = "SQLite · Windows scheduler · Playwright worker";

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        await Setup.InitializeAsync();
        await Task.WhenAll(Schedules.RefreshAsync(), History.RefreshAsync());
    }

    public void NavigateToHistory() =>
        SelectedNavigation = NavigationItems.First(item => ReferenceEquals(item.Page, History));

    private async void OnEditRequested(Guid id)
    {
        try
        {
            var schedule = await bookingService.GetScheduleAsync(id);
            if (schedule is null)
            {
                Schedules.SetStatusFromShell("The selected schedule no longer exists.", Models.UiStatusTone.Error);
                return;
            }

            NewBooking.LoadSchedule(schedule);
            SelectedNavigation = NavigationItems.First(item => ReferenceEquals(item.Page, NewBooking));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Schedules.SetStatusFromShell("The schedule could not be opened for editing.", Models.UiStatusTone.Error);
        }
    }

    private async void OnRuleSaved()
    {
        await Schedules.RefreshAsync();
    }

    partial void OnSelectedNavigationChanged(NavigationItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = value.Page;
        CurrentPageTitle = value.Label;
        CurrentPageDescription = value.Description;

        if (ReferenceEquals(value.Page, Schedules))
        {
            _ = Schedules.RefreshAsync();
        }
        else if (ReferenceEquals(value.Page, History))
        {
            _ = History.RefreshAsync();
        }
    }
}
