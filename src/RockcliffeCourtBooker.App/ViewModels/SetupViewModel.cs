using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.App.Services;

namespace RockcliffeCourtBooker.App.ViewModels;

public sealed partial class SetupViewModel(
    ApplicationState applicationState,
    IFilePickerService filePicker,
    IConfigurationFileService configurationService,
    IBrowserDiagnosticsService browserDiagnostics,
    IBookingApplicationService bookingService) : ViewModelBase
{
    private string? validatedConfigurationPath;

    [ObservableProperty]
    private string configurationPath = applicationState.ConfigurationPath ?? string.Empty;

    [ObservableProperty]
    private string accountHolder = "Not configured";

    [ObservableProperty]
    private string usernameDisplay = "—";

    [ObservableProperty]
    private bool isConfigurationValid;

    [ObservableProperty]
    private bool useVisibleBrowser = true;

    [ObservableProperty]
    private bool unattendedSubmissionEnabled;

    [ObservableProperty]
    private string securityWarning =
        "Your JSON file contains a plaintext password. Keep it private; the app will read it only when needed and will never copy the password into its database.";

    public ObservableCollection<PlayerSummary> Players { get; } = [];

    public bool HasPlayers => Players.Count > 0;

    public string UnattendedToggleLabel => UnattendedSubmissionEnabled
        ? "Disable unattended submission"
        : "Enable unattended submission";

    public async Task InitializeAsync()
    {
        UnattendedSubmissionEnabled = await bookingService.GetUnattendedSubmissionEnabledAsync();
        if (!string.IsNullOrWhiteSpace(ConfigurationPath))
        {
            await RunValidationAsync();
        }

        UpdateCommandStates();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selectedPath = filePicker.PickJsonFile();
        if (selectedPath is null)
        {
            return;
        }

        ConfigurationPath = selectedPath;
        await RunValidationAsync();
    }

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private Task ValidateAsync() => RunValidationAsync();

    [RelayCommand(CanExecute = nameof(CanRunDiagnostics))]
    private async Task TestLoginAsync()
    {
        if (!await EnsureFreshConfigurationAsync())
        {
            return;
        }

        IsBusy = true;
        UpdateCommandStates();
        SetStatus("Opening a diagnostic browser…", UiStatusTone.Info);
        try
        {
            var result = await browserDiagnostics.TestLoginAsync(ConfigurationPath, UseVisibleBrowser);
            SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleUnattendedSubmission))]
    private async Task ToggleUnattendedSubmissionAsync()
    {
        IsBusy = true;
        UpdateCommandStates();
        var desired = !UnattendedSubmissionEnabled;
        SetStatus(
            desired ? "Checking Windows and enabling unattended submission…" : "Disabling unattended submission…",
            UiStatusTone.Info);
        try
        {
            var result = await bookingService.SetUnattendedSubmissionEnabledAsync(desired);
            UnattendedSubmissionEnabled = await bookingService.GetUnattendedSubmissionEnabledAsync();
            SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunDiagnostics))]
    private async Task VerifyPlayersAsync()
    {
        if (!await EnsureFreshConfigurationAsync())
        {
            return;
        }

        IsBusy = true;
        UpdateCommandStates();
        SetStatus("Checking configured players against Matchpoint…", UiStatusTone.Info);
        try
        {
            var result = await browserDiagnostics.VerifyPlayersAsync(ConfigurationPath, UseVisibleBrowser);
            SetStatus(result.Message, result.IsSuccess ? UiStatusTone.Success : UiStatusTone.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private bool CanValidate() => !IsBusy && !string.IsNullOrWhiteSpace(ConfigurationPath);

    private bool CanRunDiagnostics() => !IsBusy && IsConfigurationValid;

    private bool CanToggleUnattendedSubmission() =>
        !IsBusy && (UnattendedSubmissionEnabled || IsConfigurationValid);

    private async Task RunValidationAsync()
    {
        IsBusy = true;
        UpdateCommandStates();
        SetStatus("Validating configuration…", UiStatusTone.Info);
        try
        {
            var result = await configurationService.ValidateAsync(ConfigurationPath);
            if (!result.IsValid || result.Configuration is null)
            {
                await DisableUnattendedForInvalidConfigurationAsync();
                validatedConfigurationPath = null;
                IsConfigurationValid = false;
                AccountHolder = "Not configured";
                UsernameDisplay = "—";
                Players.Clear();
                applicationState.MarkConfigurationInvalid();
                SetStatus(result.Message, UiStatusTone.Error);
                OnPropertyChanged(nameof(HasPlayers));
                return;
            }

            var configuration = result.Configuration;
            validatedConfigurationPath = configuration.FilePath;
            IsConfigurationValid = true;
            AccountHolder = configuration.MemberName;
            UsernameDisplay = MaskUsername(configuration.Username);
            SecurityWarning = configuration.SecurityWarning;
            Players.Clear();
            foreach (var player in configuration.Players)
            {
                Players.Add(player);
            }

            applicationState.ApplyConfiguration(configuration);
            SetStatus(result.Message, UiStatusTone.Success);
            OnPropertyChanged(nameof(HasPlayers));
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private async Task<bool> EnsureFreshConfigurationAsync()
    {
        var result = await configurationService.ValidateAsync(ConfigurationPath);
        if (!result.IsValid || result.Configuration is null)
        {
            await DisableUnattendedForInvalidConfigurationAsync();
            validatedConfigurationPath = null;
            IsConfigurationValid = false;
            applicationState.MarkConfigurationInvalid();
            SetStatus(result.Message, UiStatusTone.Error);
            UpdateCommandStates();
            return false;
        }

        validatedConfigurationPath = result.Configuration.FilePath;
        IsConfigurationValid = true;
        applicationState.ApplyConfiguration(result.Configuration);
        return true;
    }

    private async Task DisableUnattendedForInvalidConfigurationAsync()
    {
        if (!UnattendedSubmissionEnabled)
        {
            return;
        }

        await bookingService.SetUnattendedSubmissionEnabledAsync(false);
        UnattendedSubmissionEnabled = false;
    }

    private void UpdateCommandStates()
    {
        ValidateCommand.NotifyCanExecuteChanged();
        TestLoginCommand.NotifyCanExecuteChanged();
        VerifyPlayersCommand.NotifyCanExecuteChanged();
        ToggleUnattendedSubmissionCommand.NotifyCanExecuteChanged();
    }

    private static string MaskUsername(string username)
    {
        var at = username.IndexOf('@', StringComparison.Ordinal);
        if (at > 1)
        {
            return $"{username[0]}{new string('•', Math.Min(6, at - 1))}{username[at..]}";
        }

        return username.Length <= 2
            ? new string('•', Math.Max(2, username.Length))
            : $"{username[0]}{new string('•', Math.Min(8, username.Length - 2))}{username[^1]}";
    }

    partial void OnConfigurationPathChanged(string value)
    {
        if (IsConfigurationValid &&
            !string.Equals(value, validatedConfigurationPath, StringComparison.OrdinalIgnoreCase))
        {
            IsConfigurationValid = false;
            AccountHolder = "Not validated";
            UsernameDisplay = "—";
            Players.Clear();
            applicationState.MarkConfigurationInvalid();
            SetStatus("Validate this path before creating or changing booking schedules.", UiStatusTone.Warning);
            OnPropertyChanged(nameof(HasPlayers));
        }

        UpdateCommandStates();
    }

    partial void OnIsConfigurationValidChanged(bool value) => UpdateCommandStates();

    partial void OnUnattendedSubmissionEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(UnattendedToggleLabel));
        UpdateCommandStates();
    }
}
