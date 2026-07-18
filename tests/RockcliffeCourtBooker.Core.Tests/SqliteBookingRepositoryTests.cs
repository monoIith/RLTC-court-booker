using Microsoft.Data.Sqlite;
using RockcliffeCourtBooker.Infrastructure;

namespace RockcliffeCourtBooker.Core.Tests;

public sealed class SqliteBookingRepositoryTests
{
    [Fact]
    public async Task Rules_RoundTripOrderedValuesAndCanBeUpdatedAndDeleted()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var rule = TestModels.ValidOneTimeRule();

        await repository.SaveRuleAsync(rule);
        var loaded = await repository.GetRuleAsync(rule.Id);

        Assert.NotNull(loaded);
        Assert.Equal(rule.Name, loaded.Name);
        Assert.Equal(rule.StartTimes, loaded.StartTimes);
        Assert.Equal(rule.PlayerIds, loaded.PlayerIds);
        Assert.Equal(rule.CourtOrder, loaded.CourtOrder);

        await repository.SaveRuleAsync(rule with { Name = "Updated", CourtOrder = [4, 3, 2, 1] });
        Assert.Equal("Updated", (await repository.GetRuleAsync(rule.Id))!.Name);
        Assert.Equal([4, 3, 2, 1], (await repository.GetRuleAsync(rule.Id))!.CourtOrder);
        Assert.True(await repository.DeleteRuleAsync(rule.Id));
        Assert.Null(await repository.GetRuleAsync(rule.Id));
    }

    [Fact]
    public async Task Rules_BlockDuplicateEnabledDatesButAllowDisabledDrafts()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var first = TestModels.ValidOneTimeRule();
        var duplicate = TestModels.ValidOneTimeRule(first.TargetDate, enabled: true);
        var disabled = TestModels.ValidOneTimeRule(first.TargetDate, enabled: false);

        await repository.SaveRuleAsync(first);

        await Assert.ThrowsAsync<ScheduleConflictException>(() => repository.SaveRuleAsync(duplicate));
        await repository.SaveRuleAsync(disabled);
        Assert.Equal(2, (await repository.GetRulesAsync()).Count);
    }

    [Fact]
    public async Task Rules_BlockOverlappingEnabledRecurringWeekdays()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);

        await repository.SaveRuleAsync(TestModels.ValidWeeklyRule(DayOfWeek.Monday, DayOfWeek.Wednesday));

        await Assert.ThrowsAsync<ScheduleConflictException>(
            () => repository.SaveRuleAsync(TestModels.ValidWeeklyRule(DayOfWeek.Wednesday, DayOfWeek.Friday)));
    }

    [Fact]
    public async Task Rules_BlockOneTimeAndRecurringClaimsForTheSameWeekday()
    {
        using var firstDirectory = new TemporaryDirectory();
        await using var firstRepository = CreateRepository(firstDirectory);
        var monday = new DateOnly(2026, 7, 20);
        await firstRepository.SaveRuleAsync(TestModels.ValidOneTimeRule(monday));

        await Assert.ThrowsAsync<ScheduleConflictException>(
            () => firstRepository.SaveRuleAsync(TestModels.ValidWeeklyRule(DayOfWeek.Monday)));

        using var secondDirectory = new TemporaryDirectory();
        await using var secondRepository = CreateRepository(secondDirectory);
        await secondRepository.SaveRuleAsync(TestModels.ValidWeeklyRule(DayOfWeek.Monday));

        await Assert.ThrowsAsync<ScheduleConflictException>(
            () => secondRepository.SaveRuleAsync(TestModels.ValidOneTimeRule(monday)));
    }

    [Fact]
    public async Task Settings_RoundTripAbsolutePathWithoutSecretData()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var configPath = directory.GetPath("account.json");
        var settings = new AppSettings
        {
            ExternalConfigurationPath = configPath,
            UnattendedSubmissionEnabled = true,
            VisibleBrowserByDefault = true,
            UpdatedAtUtc = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
        };

        await repository.SaveSettingsAsync(settings);
        var loaded = await repository.GetSettingsAsync();

        Assert.Equal(settings, loaded);
        var databaseBytes = await File.ReadAllBytesAsync(repository.DatabasePath);
        Assert.DoesNotContain("never-log-this"u8.ToArray(), databaseBytes);
    }

    [Fact]
    public async Task Attempts_RoundTripUpdateRiskAndClearHistory()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var rule = TestModels.ValidOneTimeRule();
        await repository.SaveRuleAsync(rule);
        var attempt = new BookingAttempt
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            TargetDate = rule.TargetDate!.Value,
            ScheduledForUtc = new DateTimeOffset(2026, 7, 17, 11, 58, 0, TimeSpan.Zero),
            Status = BookingAttemptStatus.Pending,
        };

        await repository.AddAttemptAsync(attempt);
        Assert.False(await repository.HasSubmissionRiskAsync(attempt.TargetDate));

        var completed = attempt with
        {
            StartedAtUtc = attempt.ScheduledForUtc,
            CompletedAtUtc = attempt.ScheduledForUtc.AddMinutes(2),
            Status = BookingAttemptStatus.Succeeded,
            SelectedCourt = 2,
            SelectedStartTime = new TimeOnly(18, 0),
        };
        await repository.UpdateAttemptAsync(completed);

        Assert.Equal(completed, await repository.GetAttemptAsync(attempt.Id));
        Assert.True(await repository.HasSubmissionRiskAsync(attempt.TargetDate));
        Assert.Single(await repository.GetAttemptsAsync());
        Assert.Equal(0, await repository.ClearAttemptsAsync(completed.CompletedAtUtc));
        Assert.Single(await repository.GetAttemptsAsync());

        await repository.UpdateAttemptAsync(completed with { Status = BookingAttemptStatus.Failed });
        Assert.Equal(1, await repository.ClearAttemptsAsync(completed.CompletedAtUtc));
        Assert.Empty(await repository.GetAttemptsAsync());
    }

    [Fact]
    public async Task SubmissionRiskExclusion_IsNotLimitedToNewestHundredAttempts()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var rule = TestModels.ValidOneTimeRule();
        await repository.SaveRuleAsync(rule);
        var targetDate = rule.TargetDate!.Value;
        var baseline = new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var priorSuccess = new BookingAttempt
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            TargetDate = targetDate,
            ScheduledForUtc = baseline,
            StartedAtUtc = baseline,
            CompletedAtUtc = baseline.AddMinutes(1),
            Status = BookingAttemptStatus.Succeeded,
        };
        await repository.AddAttemptAsync(priorSuccess);

        for (var index = 1; index <= 101; index++)
        {
            var timestamp = baseline.AddMinutes(index);
            await repository.AddAttemptAsync(new BookingAttempt
            {
                Id = Guid.NewGuid(),
                RuleId = rule.Id,
                TargetDate = targetDate,
                ScheduledForUtc = timestamp,
                StartedAtUtc = timestamp,
                CompletedAtUtc = timestamp.AddSeconds(1),
                Status = BookingAttemptStatus.Failed,
            });
        }

        var ownAttempt = new BookingAttempt
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            TargetDate = targetDate,
            ScheduledForUtc = baseline.AddDays(1),
            StartedAtUtc = baseline.AddDays(1),
            Status = BookingAttemptStatus.Running,
        };
        await repository.AddAttemptAsync(ownAttempt);

        Assert.True(await repository.HasOtherSubmissionRiskAsync(targetDate, ownAttempt.Id));
    }

    [Fact]
    public async Task ClearAttempts_PreservesAllSubmissionSafetyRows()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        var rule = TestModels.ValidOneTimeRule();
        await repository.SaveRuleAsync(rule);
        var timestamp = new DateTimeOffset(2026, 7, 17, 11, 58, 0, TimeSpan.Zero);
        var pending = new BookingAttempt
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            TargetDate = rule.TargetDate!.Value,
            ScheduledForUtc = timestamp,
            Status = BookingAttemptStatus.Pending,
        };
        var running = pending with
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = timestamp,
            Status = BookingAttemptStatus.Running,
        };
        var failed = running with
        {
            Id = Guid.NewGuid(),
            CompletedAtUtc = timestamp.AddMinutes(1),
            Status = BookingAttemptStatus.Failed,
        };
        var succeeded = failed with
        {
            Id = Guid.NewGuid(),
            Status = BookingAttemptStatus.Succeeded,
        };
        var uncertain = failed with
        {
            Id = Guid.NewGuid(),
            Status = BookingAttemptStatus.UncertainSubmission,
        };
        await repository.AddAttemptAsync(pending);
        await repository.AddAttemptAsync(running);
        await repository.AddAttemptAsync(failed);
        await repository.AddAttemptAsync(succeeded);
        await repository.AddAttemptAsync(uncertain);

        Assert.Equal(1, await repository.ClearAttemptsAsync());
        var remaining = await repository.GetAttemptsAsync();
        Assert.Equal(4, remaining.Count);
        Assert.Contains(remaining, attempt => attempt.Id == pending.Id);
        Assert.Contains(remaining, attempt => attempt.Id == running.Id);
        Assert.Contains(remaining, attempt => attempt.Id == succeeded.Id);
        Assert.Contains(remaining, attempt => attempt.Id == uncertain.Id);
    }

    [Fact]
    public async Task OccurrenceLock_IsExclusiveAcrossRepositoriesAndReleases()
    {
        using var directory = new TemporaryDirectory();
        await using var firstRepository = CreateRepository(directory);
        await using var secondRepository = CreateRepository(directory);
        var date = new DateOnly(2026, 7, 20);

        await using var firstLease = await firstRepository.TryAcquireOccurrenceLockAsync(
            date,
            "worker-one",
            TimeSpan.FromMinutes(20));
        var blocked = await secondRepository.TryAcquireOccurrenceLockAsync(date, "worker-two", TimeSpan.FromMinutes(20));

        Assert.NotNull(firstLease);
        Assert.Null(blocked);
        await firstLease.DisposeAsync();

        await using var secondLease = await secondRepository.TryAcquireOccurrenceLockAsync(
            date,
            "worker-two",
            TimeSpan.FromMinutes(20));
        Assert.NotNull(secondLease);
    }

    [Fact]
    public async Task OccurrenceLock_CanBeTakenOverAfterExpiry()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 17, 11, 58, 0, TimeSpan.Zero));
        await using var firstRepository = CreateRepository(directory, clock);
        await using var secondRepository = CreateRepository(directory, clock);
        var date = new DateOnly(2026, 7, 20);

        var staleLease = await firstRepository.TryAcquireOccurrenceLockAsync(date, "stale", TimeSpan.FromMinutes(1));
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await using var replacement = await secondRepository.TryAcquireOccurrenceLockAsync(date, "replacement", TimeSpan.FromMinutes(10));

        Assert.NotNull(staleLease);
        Assert.NotNull(replacement);
        await staleLease!.DisposeAsync();
        var stillBlocked = await firstRepository.TryAcquireOccurrenceLockAsync(date, "third", TimeSpan.FromMinutes(10));
        Assert.Null(stillBlocked);
    }

    [Fact]
    public async Task Initialize_UsesWalJournalMode()
    {
        using var directory = new TemporaryDirectory();
        await using var repository = CreateRepository(directory);
        await repository.InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
    }

    private static SqliteBookingRepository CreateRepository(
        TemporaryDirectory directory,
        TimeProvider? timeProvider = null) =>
        new(directory.GetPath("bookings.db"), timeProvider: timeProvider);
}
