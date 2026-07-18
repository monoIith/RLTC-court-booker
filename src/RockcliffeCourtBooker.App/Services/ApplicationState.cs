using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RockcliffeCourtBooker.App.Models;

namespace RockcliffeCourtBooker.App.Services;

public sealed partial class ApplicationState : ObservableObject
{
    [ObservableProperty]
    private string? configurationPath;

    [ObservableProperty]
    private string memberName = "No account configured";

    [ObservableProperty]
    private bool configurationIsValid;

    public ObservableCollection<PlayerSummary> Players { get; } = [];

    public void LoadSavedConfigurationPath(string? path)
    {
        ConfigurationPath = path;
    }

    public void ApplyConfiguration(ConfigurationSummary configuration)
    {
        ConfigurationPath = configuration.FilePath;
        MemberName = configuration.MemberName;
        ConfigurationIsValid = true;

        Players.Clear();
        foreach (var player in configuration.Players)
        {
            Players.Add(player);
        }
    }

    public void MarkConfigurationInvalid()
    {
        ConfigurationIsValid = false;
        MemberName = "Configuration needs attention";
        Players.Clear();
    }
}
