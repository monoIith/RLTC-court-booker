namespace RockcliffeCourtBooker.Core;

public interface IExternalConfigurationLoader
{
    Task<ExternalConfigurationLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IBookingRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveRuleAsync(BookingRule rule, CancellationToken cancellationToken = default);

    Task<BookingRule?> GetRuleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookingRule>> GetRulesAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAttemptAsync(BookingAttempt attempt, CancellationToken cancellationToken = default);

    Task UpdateAttemptAsync(BookingAttempt attempt, CancellationToken cancellationToken = default);

    Task<BookingAttempt?> GetAttemptAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookingAttempt>> GetAttemptsAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task<int> ClearAttemptsAsync(DateTimeOffset? completedBeforeUtc = null, CancellationToken cancellationToken = default);

    Task<bool> HasSubmissionRiskAsync(DateOnly targetDate, CancellationToken cancellationToken = default);

    Task<bool> HasOtherSubmissionRiskAsync(
        DateOnly targetDate,
        Guid excludedAttemptId,
        CancellationToken cancellationToken = default);

    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<IOccurrenceLease?> TryAcquireOccurrenceLockAsync(
        DateOnly targetDate,
        string ownerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

public interface IOccurrenceLease : IAsyncDisposable
{
    DateOnly TargetDate { get; }

    string OwnerToken { get; }

    DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class ScheduleConflictException : Exception
{
    public ScheduleConflictException(string message)
        : base(message)
    {
    }
}
