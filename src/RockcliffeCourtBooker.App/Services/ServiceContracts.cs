using RockcliffeCourtBooker.App.Models;

namespace RockcliffeCourtBooker.App.Services;

public interface IConfigurationFileService
{
    Task<ConfigurationValidationResult> ValidateAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IFilePickerService
{
    string? PickJsonFile();
}

public interface IBrowserDiagnosticsService
{
    Task<OperationResult> TestLoginAsync(string configurationPath, bool visibleBrowser, CancellationToken cancellationToken = default);

    Task<OperationResult> VerifyPlayersAsync(string configurationPath, bool visibleBrowser, CancellationToken cancellationToken = default);
}

public interface IBookingApplicationService
{
    Task<bool> GetUnattendedSubmissionEnabledAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> SetUnattendedSubmissionEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleSummary>> GetSchedulesAsync(CancellationToken cancellationToken = default);

    Task<ScheduleSummary?> GetScheduleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OperationResult> SaveScheduleAsync(BookingRuleDraft draft, CancellationToken cancellationToken = default);

    Task<OperationResult> SetScheduleEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteScheduleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OperationResult> InspectNowAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OperationResult> DryRunAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ManualBookingTarget> GetBookNowTargetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OperationResult> BookNowAsync(Guid id, DateOnly targetDate, CancellationToken cancellationToken = default);

    Task<SchedulerHealthSnapshot> GetSchedulerHealthAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryApplicationService
{
    Task<IReadOnlyList<BookingAttemptSummary>> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken = default);
}

public interface IUserDialogService
{
    bool Confirm(string title, string message);

    void ShowError(string title, string message);
}

public interface IExternalLauncher
{
    OperationResult Open(string path);
}
