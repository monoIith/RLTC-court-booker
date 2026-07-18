using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.App.Services;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed partial class NewBookingViewModel : ViewModelBase
{
    private readonly ApplicationState applicationState;
    private readonly IBookingApplicationService bookingService;
    private bool suppressPlayerSelectionUpdates;

    [ObservableProperty]
    private Guid? editingRuleId;

    [ObservableProperty]
    private string ruleName = string.Empty;

    [ObservableProperty]
    private RuleKind selectedRuleKind = RuleKind.OneTime;

    [ObservableProperty]
    private DateTime? targetDate = DateTime.Today.AddDays(3);

    [ObservableProperty]
    private BookingType selectedBookingType = BookingType.Doubles;

    [ObservableProperty]
    private DurationChoice selectedDuration;

    [ObservableProperty]
    private TimeChoice? selectedAvailableTime;

    [ObservableProperty]
    private bool termsAuthorized;

    [ObservableProperty]
    private string selectedPlayerSummary = "Select 3 playing partners";

    public NewBookingViewModel(ApplicationState applicationState, IBookingApplicationService bookingService)
    {
        this.applicationState = applicationState;
        this.bookingService = bookingService;

        DurationChoices = [new(30), new(60), new(90), new(120)];
        selectedDuration = DurationChoices[2];

        AvailableTimes = Enumerable.Range(0, 33)
            .Select(index => new TimeChoice(new TimeOnly(6, 0).AddMinutes(index * 30)))
            .ToArray();
        selectedAvailableTime = AvailableTimes.First(choice => choice.Value == new TimeOnly(18, 0));

        Weekdays =
        [
            new(DayOfWeek.Monday, "Mon"),
            new(DayOfWeek.Tuesday, "Tue"),
            new(DayOfWeek.Wednesday, "Wed"),
            new(DayOfWeek.Thursday, "Thu"),
            new(DayOfWeek.Friday, "Fri"),
            new(DayOfWeek.Saturday, "Sat"),
            new(DayOfWeek.Sunday, "Sun")
        ];

        CourtPreferences = [new(1), new(2), new(3), new(4)];
        RefreshPlayers();
        applicationState.Players.CollectionChanged += OnPlayersChanged;
    }

    public event Action? RuleSaved;

    public IReadOnlyList<DurationChoice> DurationChoices { get; }

    public IReadOnlyList<TimeChoice> AvailableTimes { get; }

    public IReadOnlyList<BookingType> BookingTypes { get; } = [BookingType.Singles, BookingType.Doubles];

    public ObservableCollection<WeekdayChoiceViewModel> Weekdays { get; }

    public ObservableCollection<TimePreferenceViewModel> TimePreferences { get; } = [];

    public ObservableCollection<CourtPreferenceViewModel> CourtPreferences { get; }

    public ObservableCollection<PlayerChoiceViewModel> Players { get; } = [];

    public bool IsOneTime => SelectedRuleKind == RuleKind.OneTime;

    public bool IsWeekly => SelectedRuleKind == RuleKind.Weekly;

    public int RequiredPlayerCount => SelectedBookingType == BookingType.Singles ? 1 : 3;

    public string FormTitle => EditingRuleId is null ? "Create booking schedule" : "Edit booking schedule";

    public string SaveButtonText => EditingRuleId is null ? "Save schedule" : "Save changes";

    [RelayCommand(CanExecute = nameof(CanAddTime))]
    private void AddTime()
    {
        if (SelectedAvailableTime is null || TimePreferences.Any(item => item.Value == SelectedAvailableTime.Value))
        {
            return;
        }

        TimePreferences.Add(new TimePreferenceViewModel(SelectedAvailableTime.Value));
        AddTimeCommand.NotifyCanExecuteChanged();
        ClearStatus();
    }

    [RelayCommand]
    private void RemoveTime(TimePreferenceViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        TimePreferences.Remove(item);
        AddTimeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void MoveTimeUp(TimePreferenceViewModel? item) => MoveItem(TimePreferences, item, -1);

    [RelayCommand]
    private void MoveTimeDown(TimePreferenceViewModel? item) => MoveItem(TimePreferences, item, 1);

    [RelayCommand]
    private void MoveCourtUp(CourtPreferenceViewModel? item) => MoveItem(CourtPreferences, item, -1);

    [RelayCommand]
    private void MoveCourtDown(CourtPreferenceViewModel? item) => MoveItem(CourtPreferences, item, 1);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var validationMessage = ValidateDraft();
        if (validationMessage is not null)
        {
            SetStatus(validationMessage, UiStatusTone.Error);
            return;
        }

        IsBusy = true;
        SaveCommand.NotifyCanExecuteChanged();
        SetStatus("Saving booking schedule…", UiStatusTone.Info);
        try
        {
            var selectedDays = SelectedRuleKind == RuleKind.Weekly
                ? Weekdays.Where(day => day.IsSelected).Select(day => day.Day).ToArray()
                : [];
            var selectedPlayers = Players.Where(player => player.IsSelected).Select(player => player.Id).ToArray();
            DateOnly? date = SelectedRuleKind == RuleKind.OneTime && TargetDate is not null
                ? DateOnly.FromDateTime(TargetDate.Value)
                : null;
            var name = string.IsNullOrWhiteSpace(RuleName)
                ? BuildDefaultName(date, selectedDays)
                : RuleName.Trim();

            var draft = new BookingRuleDraft(
                EditingRuleId,
                name,
                SelectedRuleKind,
                date,
                selectedDays,
                TimePreferences.Select(time => time.Value).ToArray(),
                SelectedBookingType,
                SelectedDuration.Minutes,
                selectedPlayers,
                CourtPreferences.Select(court => court.CourtNumber).ToArray(),
                TermsAuthorized,
                TermsAuthorized ? DateTimeOffset.UtcNow : null);

            var result = await bookingService.SaveScheduleAsync(draft);
            SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
            if (result.IsSuccess)
            {
                RuleSaved?.Invoke();
            }
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Reset()
    {
        EditingRuleId = null;
        RuleName = string.Empty;
        SelectedRuleKind = RuleKind.OneTime;
        TargetDate = DateTime.Today.AddDays(3);
        SelectedBookingType = BookingType.Doubles;
        SelectedDuration = DurationChoices[2];
        TermsAuthorized = false;
        Weekdays.ToList().ForEach(day => day.IsSelected = false);
        TimePreferences.Clear();
        CourtPreferences.Clear();
        foreach (var court in Enumerable.Range(1, 4))
        {
            CourtPreferences.Add(new CourtPreferenceViewModel(court));
        }

        suppressPlayerSelectionUpdates = true;
        foreach (var player in Players)
        {
            player.IsSelected = false;
        }

        suppressPlayerSelectionUpdates = false;
        RefreshPlayerSelectionState();
        ClearStatus();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    public void LoadSchedule(ScheduleSummary schedule)
    {
        EditingRuleId = schedule.Id;
        RuleName = schedule.Name;
        SelectedRuleKind = schedule.Kind;
        TargetDate = schedule.TargetDate?.ToDateTime(TimeOnly.MinValue);
        SelectedBookingType = schedule.BookingType;
        SelectedDuration = DurationChoices.First(choice => choice.Minutes == schedule.DurationMinutes);
        TermsAuthorized = schedule.TermsAuthorizedAt is not null;

        foreach (var day in Weekdays)
        {
            day.IsSelected = schedule.Weekdays.Contains(day.Day);
        }

        TimePreferences.Clear();
        foreach (var time in schedule.StartTimes)
        {
            TimePreferences.Add(new TimePreferenceViewModel(time));
        }

        CourtPreferences.Clear();
        foreach (var court in schedule.CourtOrder)
        {
            CourtPreferences.Add(new CourtPreferenceViewModel(court));
        }

        RefreshPlayers(schedule.PlayerIds);
        SetStatus("Editing an existing schedule. Save changes when you are ready.", UiStatusTone.Info);
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private string? ValidateDraft()
    {
        if (!applicationState.ConfigurationIsValid)
        {
            return "Validate an account configuration on the Setup page before creating a schedule.";
        }

        if (SelectedRuleKind == RuleKind.OneTime)
        {
            if (TargetDate is null)
            {
                return "Choose the date you want to play.";
            }

            if (DateOnly.FromDateTime(TargetDate.Value) < DateOnly.FromDateTime(DateTime.Today))
            {
                return "The target date cannot be in the past.";
            }
        }
        else if (!Weekdays.Any(day => day.IsSelected))
        {
            return "Select at least one recurring weekday.";
        }

        if (TimePreferences.Count == 0)
        {
            return "Add at least one preferred start time.";
        }

        var selectedPlayerCount = Players.Count(player => player.IsSelected);
        if (selectedPlayerCount != RequiredPlayerCount)
        {
            return $"{SelectedBookingType} requires exactly {RequiredPlayerCount} playing {(RequiredPlayerCount == 1 ? "partner" : "partners")}.";
        }

        if (!CourtPreferences.Select(court => court.CourtNumber).Order().SequenceEqual([1, 2, 3, 4]))
        {
            return "The court order must contain clay courts 1, 2, 3, and 4 exactly once.";
        }

        if (!TermsAuthorized)
        {
            return "Authorize the app to accept Matchpoint's legal conditions for this rule.";
        }

        return null;
    }

    private string BuildDefaultName(DateOnly? date, IReadOnlyList<DayOfWeek> weekdays)
    {
        var target = SelectedRuleKind == RuleKind.OneTime
            ? date?.ToString("MMM d", CultureInfo.CurrentCulture) ?? "One-time"
            : string.Join("/", weekdays.Select(day => day.ToString()[..3]));
        var firstTime = TimePreferences.First().Value.ToString("h:mm tt", CultureInfo.CurrentCulture);
        return $"{target} · {firstTime} {SelectedBookingType}";
    }

    private bool CanAddTime() =>
        !IsBusy &&
        SelectedAvailableTime is not null &&
        TimePreferences.All(item => item.Value != SelectedAvailableTime.Value);

    private bool CanSave() => !IsBusy;

    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPlayers();

    private void RefreshPlayers(IReadOnlyList<string>? selectedIds = null)
    {
        selectedIds ??= Players.Where(player => player.IsSelected).Select(player => player.Id).ToArray();
        var selectedSet = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        suppressPlayerSelectionUpdates = true;
        Players.Clear();
        foreach (var player in applicationState.Players)
        {
            Players.Add(new PlayerChoiceViewModel(player.Id, player.DisplayName, OnPlayerSelectionChanged)
            {
                IsSelected = selectedSet.Contains(player.Id)
            });
        }

        suppressPlayerSelectionUpdates = false;
        RefreshPlayerSelectionState();
    }

    private void OnPlayerSelectionChanged(PlayerChoiceViewModel player)
    {
        if (suppressPlayerSelectionUpdates)
        {
            return;
        }

        RefreshPlayerSelectionState();
    }

    private void RefreshPlayerSelectionState()
    {
        var selectedCount = Players.Count(player => player.IsSelected);
        foreach (var player in Players)
        {
            player.CanToggle = player.IsSelected || selectedCount < RequiredPlayerCount;
        }

        SelectedPlayerSummary = selectedCount == RequiredPlayerCount
            ? $"{selectedCount} of {RequiredPlayerCount} partners selected"
            : $"Select {RequiredPlayerCount - selectedCount} more of {RequiredPlayerCount}";
    }

    private static void MoveItem<T>(ObservableCollection<T> collection, T? item, int offset)
        where T : class
    {
        if (item is null)
        {
            return;
        }

        var oldIndex = collection.IndexOf(item);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= collection.Count)
        {
            return;
        }

        collection.Move(oldIndex, newIndex);
    }

    partial void OnSelectedRuleKindChanged(RuleKind value)
    {
        OnPropertyChanged(nameof(IsOneTime));
        OnPropertyChanged(nameof(IsWeekly));
    }

    partial void OnSelectedBookingTypeChanged(BookingType value)
    {
        suppressPlayerSelectionUpdates = true;
        foreach (var player in Players.Where(player => player.IsSelected).Skip(RequiredPlayerCount))
        {
            player.IsSelected = false;
        }

        suppressPlayerSelectionUpdates = false;
        OnPropertyChanged(nameof(RequiredPlayerCount));
        RefreshPlayerSelectionState();
    }

    partial void OnSelectedAvailableTimeChanged(TimeChoice? value) => AddTimeCommand.NotifyCanExecuteChanged();

    partial void OnEditingRuleIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }
}
