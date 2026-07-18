using CommunityToolkit.Mvvm.ComponentModel;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed partial class NavigationItemViewModel(
    string label,
    string glyph,
    string description,
    object page) : ObservableObject
{
    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    public string Description { get; } = description;

    public object Page { get; } = page;
}

public sealed partial class PlayerChoiceViewModel(
    string id,
    string displayName,
    Action<PlayerChoiceViewModel> selectionChanged) : ObservableObject
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool canToggle = true;

    partial void OnIsSelectedChanged(bool value) => selectionChanged(this);
}

public sealed partial class WeekdayChoiceViewModel(DayOfWeek day, string shortName) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string ShortName { get; } = shortName;

    [ObservableProperty]
    private bool isSelected;
}

public sealed record TimeChoice(TimeOnly Value)
{
    public string Display => Value.ToString("h:mm tt", CultureInfo.CurrentCulture);
}

public sealed record DurationChoice(int Minutes)
{
    public string Display => Minutes == 60 ? "60 minutes (1 hour)" :
        Minutes == 120 ? "120 minutes (2 hours)" :
        $"{Minutes} minutes";
}

public sealed partial class TimePreferenceViewModel(TimeOnly value) : ObservableObject
{
    public TimeOnly Value { get; } = value;

    public string Display => Value.ToString("h:mm tt", CultureInfo.CurrentCulture);
}

public sealed partial class CourtPreferenceViewModel(int courtNumber) : ObservableObject
{
    public int CourtNumber { get; } = courtNumber;

    public string Display => $"Court {CourtNumber} · Clay";
}
