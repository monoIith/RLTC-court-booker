using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Core;
using RockcliffeCourtBooker.Infrastructure;
using RockcliffeCourtBooker.Worker.Contracts;
using RockcliffeCourtBooker.WorkerLogic;

namespace RockcliffeCourtBooker.Worker.Tests;

public sealed class WorkerFinalizationTests
{
    [Fact]
    public void CutoffCancellation_IsRecognizedAtTheExactBoundary()
    {
        var cutoff = new DateTimeOffset(2026, 7, 17, 12, 15, 0, TimeSpan.Zero);

        Assert.True(WorkerAttemptFinalizer.IsPreSubmissionCutoffCancellation(
            true, false, cutoff, cutoff, BookingAutomationStatus.Cancelled));
        Assert.False(WorkerAttemptFinalizer.IsPreSubmissionCutoffCancellation(
            true, false, cutoff.AddTicks(-1), cutoff, BookingAutomationStatus.Cancelled));
        Assert.False(WorkerAttemptFinalizer.IsPreSubmissionCutoffCancellation(
            false, true, cutoff, cutoff, BookingAutomationStatus.Cancelled));
        Assert.False(WorkerAttemptFinalizer.IsPreSubmissionCutoffCancellation(
            true, true, cutoff, cutoff, BookingAutomationStatus.SubmissionUncertain));
    }

    [Fact]
    public async Task ReturnedPreClickCancellation_PersistsCancelledInsteadOfUncertain()
    {
        await using var fixture = await AttemptFixture.CreateAsync(isDryRun: false);
        var automation = AutomationResult(fixture.Attempt.Id, BookingAutomationStatus.Cancelled);

        await WorkerAttemptFinalizer.RunAndFinalizeAttemptAsync(
            fixture.Repository,
            fixture.Attempt,
            _ => Task.FromResult(WorkerResult(fixture.Attempt.Id, WorkerOutcome.AutomationFailed, automation)),
            CancellationToken.None);

        Assert.Equal(BookingAttemptStatus.Cancelled, (await fixture.ReloadAsync()).Status);
    }

    [Fact]
    public async Task ReturnedMissedWindow_PersistsMissedWindow()
    {
        await using var fixture = await AttemptFixture.CreateAsync(isDryRun: false);

        await WorkerAttemptFinalizer.RunAndFinalizeAttemptAsync(
            fixture.Repository,
            fixture.Attempt,
            _ => Task.FromResult(WorkerResult(fixture.Attempt.Id, WorkerOutcome.MissedWindow)),
            CancellationToken.None);

        Assert.Equal(BookingAttemptStatus.MissedWindow, (await fixture.ReloadAsync()).Status);
    }

    [Fact]
    public async Task EscapedSubmitCancellation_PersistsUncertainSubmission()
    {
        await using var fixture = await AttemptFixture.CreateAsync(isDryRun: false);

        await WorkerAttemptFinalizer.RunAndFinalizeAttemptAsync(
            fixture.Repository,
            fixture.Attempt,
            _ => throw new OperationCanceledException(),
            CancellationToken.None);

        Assert.Equal(BookingAttemptStatus.UncertainSubmission, (await fixture.ReloadAsync()).Status);
    }

    [Fact]
    public async Task ReturnedPostClickUncertainty_PersistsUncertainSubmission()
    {
        await using var fixture = await AttemptFixture.CreateAsync(isDryRun: false);
        var automation = AutomationResult(fixture.Attempt.Id, BookingAutomationStatus.SubmissionUncertain);

        await WorkerAttemptFinalizer.RunAndFinalizeAttemptAsync(
            fixture.Repository,
            fixture.Attempt,
            _ => Task.FromResult(WorkerResult(fixture.Attempt.Id, WorkerOutcome.AutomationFailed, automation)),
            CancellationToken.None);

        Assert.Equal(BookingAttemptStatus.UncertainSubmission, (await fixture.ReloadAsync()).Status);
    }

    private static BookingAutomationResult AutomationResult(Guid attemptId, BookingAutomationStatus status) =>
        new()
        {
            AttemptId = attemptId.ToString("D"),
            Status = status,
            Message = status.ToString(),
        };

    private static BookingWorkerResult WorkerResult(
        Guid attemptId,
        WorkerOutcome outcome,
        BookingAutomationResult? automation = null) =>
        new()
        {
            AttemptId = attemptId,
            Outcome = outcome,
            Message = outcome.ToString(),
            AutomationResult = automation,
        };

    private sealed class AttemptFixture : IAsyncDisposable
    {
        private readonly string directory;

        private AttemptFixture(string directory, SqliteBookingRepository repository, BookingAttempt attempt)
        {
            this.directory = directory;
            Repository = repository;
            Attempt = attempt;
        }

        public SqliteBookingRepository Repository { get; }

        public BookingAttempt Attempt { get; }

        public static async Task<AttemptFixture> CreateAsync(bool isDryRun)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rockcliffe-worker-tests-{Guid.NewGuid():N}");
            var repository = new SqliteBookingRepository(Path.Combine(directory, "bookings.db"));
            await repository.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var attempt = new BookingAttempt
            {
                Id = Guid.NewGuid(),
                RuleId = Guid.NewGuid(),
                TargetDate = new DateOnly(2026, 7, 20),
                ScheduledForUtc = now,
                StartedAtUtc = now,
                Status = BookingAttemptStatus.Running,
                IsDryRun = isDryRun,
            };
            await repository.AddAttemptAsync(attempt);
            return new AttemptFixture(directory, repository, attempt);
        }

        public async Task<BookingAttempt> ReloadAsync() =>
            await Repository.GetAttemptAsync(Attempt.Id) ??
            throw new InvalidOperationException("The finalized attempt was not persisted.");

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A SQLite pool can release asynchronously on some test hosts.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of an isolated temporary test directory.
            }
        }
    }
}
