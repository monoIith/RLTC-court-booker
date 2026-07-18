using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.Infrastructure;

public sealed class SqliteBookingRepository : IBookingRepository, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss";
    private const string TimestampFormat = "O";

    private readonly string _connectionString;
    private readonly BookingRuleValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    public SqliteBookingRepository(
        string databasePath,
        BookingRuleValidator? validator = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The database path must include a directory.", nameof(databasePath));
        }

        DatabasePath = fullPath;
        _validator = validator ?? new BookingRuleValidator();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var currentVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (currentVersion > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema version {currentVersion} is newer than supported version {SchemaVersion}.");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task SaveRuleAsync(BookingRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _validator.Validate(rule).ThrowIfInvalid();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await EnsureNoCrossKindScheduleConflictAsync(connection, transaction, rule, cancellationToken)
                .ConfigureAwait(false);
            await UpsertRuleRowAsync(connection, transaction, rule, cancellationToken).ConfigureAwait(false);
            await DeleteRuleChildrenAsync(connection, transaction, rule.Id, cancellationToken).ConfigureAwait(false);
            await InsertRuleChildrenAsync(connection, transaction, rule, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ScheduleConflictException(
                rule.Kind == BookingRuleKind.OneTime
                    ? "Another enabled rule already claims this target date."
                    : "Another enabled recurring rule already claims one of these weekdays.");
        }
    }

    private static async Task EnsureNoCrossKindScheduleConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BookingRule rule,
        CancellationToken cancellationToken)
    {
        if (!rule.Enabled)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", FormatGuid(rule.Id));
        command.Parameters.AddWithValue("$enabled", 1);

        if (rule.Kind == BookingRuleKind.OneTime)
        {
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM booking_rules AS rules
                    INNER JOIN booking_rule_weekdays AS weekdays ON weekdays.rule_id = rules.id
                    WHERE rules.id <> $id
                      AND rules.enabled = $enabled
                      AND rules.kind = $weekly
                      AND weekdays.weekday = $weekday
                );
                """;
            command.Parameters.AddWithValue("$weekly", (int)BookingRuleKind.Weekly);
            command.Parameters.AddWithValue("$weekday", (int)rule.TargetDate!.Value.DayOfWeek);
        }
        else
        {
            var weekdayParameters = rule.Weekdays
                .Select((day, index) => (Name: $"$weekday{index}", Value: (int)day))
                .ToArray();
            command.CommandText = $"""
                SELECT EXISTS (
                    SELECT 1
                    FROM booking_rules AS rules
                    WHERE rules.id <> $id
                      AND rules.enabled = $enabled
                      AND rules.kind = $oneTime
                      AND CAST(strftime('%w', rules.target_date) AS INTEGER)
                          IN ({string.Join(", ", weekdayParameters.Select(static item => item.Name))})
                );
                """;
            command.Parameters.AddWithValue("$oneTime", (int)BookingRuleKind.OneTime);
            foreach (var parameter in weekdayParameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
        }

        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1)
        {
            throw new ScheduleConflictException(
                "An enabled one-time and recurring rule cannot claim the same target weekday.");
        }
    }

    public async Task<BookingRule?> GetRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await GetRuleRowAsync(connection, id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await MaterializeRuleAsync(connection, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BookingRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RuleRow>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"{RuleSelectSql} ORDER BY created_at_utc, id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRuleRow(reader));
            }
        }

        var rules = new List<BookingRule>(rows.Count);
        foreach (var row in rows)
        {
            rules.Add(await MaterializeRuleAsync(connection, row, cancellationToken).ConfigureAwait(false));
        }

        return rules;
    }

    public async Task<bool> DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM booking_rules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatGuid(id));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task AddAttemptAsync(BookingAttempt attempt, CancellationToken cancellationToken = default)
    {
        ValidateAttempt(attempt);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO booking_attempts (
                id, rule_id, target_date, scheduled_for_utc, started_at_utc, completed_at_utc,
                status, selected_court, selected_start_time, error_code, sanitized_message,
                screenshot_path, trace_path, is_dry_run)
            VALUES (
                $id, $ruleId, $targetDate, $scheduled, $started, $completed,
                $status, $court, $startTime, $errorCode, $message,
                $screenshot, $trace, $dryRun);
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAttemptAsync(BookingAttempt attempt, CancellationToken cancellationToken = default)
    {
        ValidateAttempt(attempt);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE booking_attempts SET
                rule_id = $ruleId,
                target_date = $targetDate,
                scheduled_for_utc = $scheduled,
                started_at_utc = $started,
                completed_at_utc = $completed,
                status = $status,
                selected_court = $court,
                selected_start_time = $startTime,
                error_code = $errorCode,
                sanitized_message = $message,
                screenshot_path = $screenshot,
                trace_path = $trace,
                is_dry_run = $dryRun
            WHERE id = $id;
            """;
        AddAttemptParameters(command, attempt);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Booking attempt '{attempt.Id}' does not exist.");
        }
    }

    public async Task<BookingAttempt?> GetAttemptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{AttemptSelectSql} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatGuid(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAttempt(reader) : null;
    }

    public async Task<IReadOnlyList<BookingAttempt>> GetAttemptsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The history limit must be between 1 and 10,000.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{AttemptSelectSql} ORDER BY scheduled_for_utc DESC, id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var attempts = new List<BookingAttempt>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    public async Task<int> ClearAttemptsAsync(
        DateTimeOffset? completedBeforeUtc = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (completedBeforeUtc is null)
        {
            command.CommandText = """
                DELETE FROM booking_attempts
                WHERE status NOT IN ($pending, $running, $succeeded, $uncertain);
                """;
        }
        else
        {
            command.CommandText = """
                DELETE FROM booking_attempts
                WHERE completed_at_utc IS NOT NULL
                  AND completed_at_utc <= $cutoff
                  AND status NOT IN ($pending, $running, $succeeded, $uncertain);
                """;
            command.Parameters.AddWithValue("$cutoff", FormatTimestamp(completedBeforeUtc.Value));
        }

        command.Parameters.AddWithValue("$pending", (int)BookingAttemptStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)BookingAttemptStatus.Running);
        command.Parameters.AddWithValue("$succeeded", (int)BookingAttemptStatus.Succeeded);
        command.Parameters.AddWithValue("$uncertain", (int)BookingAttemptStatus.UncertainSubmission);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasSubmissionRiskAsync(DateOnly targetDate, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM booking_attempts
                WHERE target_date = $targetDate
                  AND status IN ($running, $succeeded, $uncertain)
            );
            """;
        command.Parameters.AddWithValue("$targetDate", FormatDate(targetDate));
        command.Parameters.AddWithValue("$running", (int)BookingAttemptStatus.Running);
        command.Parameters.AddWithValue("$succeeded", (int)BookingAttemptStatus.Succeeded);
        command.Parameters.AddWithValue("$uncertain", (int)BookingAttemptStatus.UncertainSubmission);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> HasOtherSubmissionRiskAsync(
        DateOnly targetDate,
        Guid excludedAttemptId,
        CancellationToken cancellationToken = default)
    {
        if (excludedAttemptId == Guid.Empty)
        {
            throw new ArgumentException("The excluded attempt ID cannot be empty.", nameof(excludedAttemptId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM booking_attempts
                WHERE target_date = $targetDate
                  AND id <> $excludedAttemptId
                  AND status IN ($running, $succeeded, $uncertain)
            );
            """;
        command.Parameters.AddWithValue("$targetDate", FormatDate(targetDate));
        command.Parameters.AddWithValue("$excludedAttemptId", FormatGuid(excludedAttemptId));
        command.Parameters.AddWithValue("$running", (int)BookingAttemptStatus.Running);
        command.Parameters.AddWithValue("$succeeded", (int)BookingAttemptStatus.Succeeded);
        command.Parameters.AddWithValue("$uncertain", (int)BookingAttemptStatus.UncertainSubmission);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT external_configuration_path, unattended_submission_enabled,
                   visible_browser_by_default, updated_at_utc
            FROM app_settings WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AppSettings();
        }

        return new AppSettings
        {
            ExternalConfigurationPath = reader.IsDBNull(0) ? null : reader.GetString(0),
            UnattendedSubmissionEnabled = reader.GetInt64(1) != 0,
            VisibleBrowserByDefault = reader.GetInt64(2) != 0,
            UpdatedAtUtc = ParseTimestamp(reader.GetString(3)),
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ExternalConfigurationPath is { } path && !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The external configuration path must be absolute.", nameof(settings));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (
                id, external_configuration_path, unattended_submission_enabled,
                visible_browser_by_default, updated_at_utc)
            VALUES (1, $path, $unattended, $visible, $updated)
            ON CONFLICT(id) DO UPDATE SET
                external_configuration_path = excluded.external_configuration_path,
                unattended_submission_enabled = excluded.unattended_submission_enabled,
                visible_browser_by_default = excluded.visible_browser_by_default,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$path", DbValue(settings.ExternalConfigurationPath));
        command.Parameters.AddWithValue("$unattended", settings.UnattendedSubmissionEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$visible", settings.VisibleBrowserByDefault ? 1 : 0);
        command.Parameters.AddWithValue("$updated", FormatTimestamp(settings.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IOccurrenceLease?> TryAcquireOccurrenceLockAsync(
        DateOnly targetDate,
        string ownerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);
        if (ownerToken.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerToken), "The owner token cannot exceed 200 characters.");
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "The lease duration must be between zero and one hour.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var expires = now.Add(leaseDuration);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO booking_occurrence_locks (target_date, owner_token, acquired_at_utc, expires_at_utc)
            VALUES ($targetDate, $owner, $now, $expires)
            ON CONFLICT(target_date) DO UPDATE SET
                owner_token = excluded.owner_token,
                acquired_at_utc = excluded.acquired_at_utc,
                expires_at_utc = excluded.expires_at_utc
            WHERE booking_occurrence_locks.expires_at_utc <= $now
               OR booking_occurrence_locks.owner_token = $owner;
            """;
        command.Parameters.AddWithValue("$targetDate", FormatDate(targetDate));
        command.Parameters.AddWithValue("$owner", ownerToken);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$expires", FormatTimestamp(expires));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? new OccurrenceLease(this, targetDate, ownerToken, expires)
            : null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _initializationGate.Dispose();
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }

        return ValueTask.CompletedTask;
    }

    private async Task ReleaseOccurrenceLockAsync(DateOnly targetDate, string ownerToken)
    {
        if (_disposed)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM booking_occurrence_locks WHERE target_date = $targetDate AND owner_token = $owner;";
        command.Parameters.AddWithValue("$targetDate", FormatDate(targetDate));
        command.Parameters.AddWithValue("$owner", ownerToken);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertRuleRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BookingRule rule,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO booking_rules (
                id, name, kind, target_date, booking_type, duration_minutes, enabled,
                terms_authorized_at_utc, created_at_utc, updated_at_utc)
            VALUES (
                $id, $name, $kind, $targetDate, $bookingType, $duration, $enabled,
                $terms, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                kind = excluded.kind,
                target_date = excluded.target_date,
                booking_type = excluded.booking_type,
                duration_minutes = excluded.duration_minutes,
                enabled = excluded.enabled,
                terms_authorized_at_utc = excluded.terms_authorized_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(rule.Id));
        command.Parameters.AddWithValue("$name", rule.Name.Trim());
        command.Parameters.AddWithValue("$kind", (int)rule.Kind);
        command.Parameters.AddWithValue("$targetDate", DbValue(rule.TargetDate is { } date ? FormatDate(date) : null));
        command.Parameters.AddWithValue("$bookingType", (int)rule.BookingType);
        command.Parameters.AddWithValue("$duration", rule.DurationMinutes);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$terms", DbValue(rule.TermsAuthorizedAtUtc is { } terms ? FormatTimestamp(terms) : null));
        command.Parameters.AddWithValue("$created", FormatTimestamp(rule.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(rule.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteRuleChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM booking_rule_times WHERE rule_id = $id;
            DELETE FROM booking_rule_weekdays WHERE rule_id = $id;
            DELETE FROM booking_rule_players WHERE rule_id = $id;
            DELETE FROM booking_rule_courts WHERE rule_id = $id;
            DELETE FROM booking_schedule_claims WHERE rule_id = $id;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRuleChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BookingRule rule,
        CancellationToken cancellationToken)
    {
        await InsertOrderedValuesAsync(
            connection,
            transaction,
            "booking_rule_times",
            "time_value",
            rule.Id,
            rule.StartTimes.Select(FormatTime),
            cancellationToken).ConfigureAwait(false);
        await InsertOrderedValuesAsync(
            connection,
            transaction,
            "booking_rule_players",
            "player_id",
            rule.Id,
            rule.PlayerIds,
            cancellationToken).ConfigureAwait(false);
        await InsertOrderedValuesAsync(
            connection,
            transaction,
            "booking_rule_courts",
            "court_number",
            rule.Id,
            rule.CourtOrder.Cast<object>(),
            cancellationToken).ConfigureAwait(false);

        foreach (var day in rule.Weekdays)
        {
            await using var weekdayCommand = connection.CreateCommand();
            weekdayCommand.Transaction = transaction;
            weekdayCommand.CommandText = "INSERT INTO booking_rule_weekdays (rule_id, weekday) VALUES ($id, $weekday);";
            weekdayCommand.Parameters.AddWithValue("$id", FormatGuid(rule.Id));
            weekdayCommand.Parameters.AddWithValue("$weekday", (int)day);
            await weekdayCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!rule.Enabled)
        {
            return;
        }

        if (rule.Kind == BookingRuleKind.OneTime)
        {
            await InsertClaimAsync(
                connection,
                transaction,
                rule.Id,
                "date",
                FormatDate(rule.TargetDate!.Value),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var day in rule.Weekdays)
            {
                await InsertClaimAsync(
                    connection,
                    transaction,
                    rule.Id,
                    "weekday",
                    ((int)day).ToString(CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task InsertOrderedValuesAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string valueColumn,
        Guid ruleId,
        IEnumerable<T> values,
        CancellationToken cancellationToken)
    {
        var ordinal = 0;
        foreach (var value in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} (rule_id, ordinal, {valueColumn}) VALUES ($id, $ordinal, $value);";
            command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
            command.Parameters.AddWithValue("$ordinal", ordinal++);
            command.Parameters.AddWithValue("$value", value!);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid ruleId,
        string claimType,
        string claimValue,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO booking_schedule_claims (rule_id, claim_type, claim_value)
            VALUES ($id, $type, $value);
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
        command.Parameters.AddWithValue("$type", claimType);
        command.Parameters.AddWithValue("$value", claimValue);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RuleRow?> GetRuleRowAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{RuleSelectSql} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatGuid(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRuleRow(reader) : null;
    }

    private static RuleRow ReadRuleRow(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        (BookingRuleKind)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
        (BookingType)reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
        ParseTimestamp(reader.GetString(8)),
        ParseTimestamp(reader.GetString(9)));

    private static async Task<BookingRule> MaterializeRuleAsync(
        SqliteConnection connection,
        RuleRow row,
        CancellationToken cancellationToken)
    {
        var times = await ReadOrderedStringsAsync(connection, "booking_rule_times", "time_value", row.Id, cancellationToken).ConfigureAwait(false);
        var players = await ReadOrderedStringsAsync(connection, "booking_rule_players", "player_id", row.Id, cancellationToken).ConfigureAwait(false);
        var courts = await ReadOrderedIntegersAsync(connection, "booking_rule_courts", "court_number", row.Id, cancellationToken).ConfigureAwait(false);
        var weekdays = await ReadWeekdaysAsync(connection, row.Id, cancellationToken).ConfigureAwait(false);

        return new BookingRule
        {
            Id = row.Id,
            Name = row.Name,
            Kind = row.Kind,
            TargetDate = row.TargetDate,
            Weekdays = weekdays,
            StartTimes = times.Select(ParseTime).ToArray(),
            BookingType = row.BookingType,
            DurationMinutes = row.DurationMinutes,
            PlayerIds = players,
            CourtOrder = courts,
            Enabled = row.Enabled,
            TermsAuthorizedAtUtc = row.TermsAuthorizedAtUtc,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
        };
    }

    private static async Task<IReadOnlyList<string>> ReadOrderedStringsAsync(
        SqliteConnection connection,
        string table,
        string column,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE rule_id = $id ORDER BY ordinal;";
        command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<IReadOnlyList<int>> ReadOrderedIntegersAsync(
        SqliteConnection connection,
        string table,
        string column,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE rule_id = $id ORDER BY ordinal;";
        command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetInt32(0));
        }

        return values;
    }

    private static async Task<IReadOnlyList<DayOfWeek>> ReadWeekdaysAsync(
        SqliteConnection connection,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT weekday FROM booking_rule_weekdays WHERE rule_id = $id ORDER BY weekday;";
        command.Parameters.AddWithValue("$id", FormatGuid(ruleId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<DayOfWeek>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add((DayOfWeek)reader.GetInt32(0));
        }

        return values;
    }

    private static void AddAttemptParameters(SqliteCommand command, BookingAttempt attempt)
    {
        command.Parameters.AddWithValue("$id", FormatGuid(attempt.Id));
        command.Parameters.AddWithValue("$ruleId", FormatGuid(attempt.RuleId));
        command.Parameters.AddWithValue("$targetDate", FormatDate(attempt.TargetDate));
        command.Parameters.AddWithValue("$scheduled", FormatTimestamp(attempt.ScheduledForUtc));
        command.Parameters.AddWithValue("$started", DbValue(attempt.StartedAtUtc is { } started ? FormatTimestamp(started) : null));
        command.Parameters.AddWithValue("$completed", DbValue(attempt.CompletedAtUtc is { } completed ? FormatTimestamp(completed) : null));
        command.Parameters.AddWithValue("$status", (int)attempt.Status);
        command.Parameters.AddWithValue("$court", DbValue(attempt.SelectedCourt));
        command.Parameters.AddWithValue("$startTime", DbValue(attempt.SelectedStartTime is { } time ? FormatTime(time) : null));
        command.Parameters.AddWithValue("$errorCode", DbValue(attempt.ErrorCode));
        command.Parameters.AddWithValue("$message", DbValue(attempt.SanitizedMessage));
        command.Parameters.AddWithValue("$screenshot", DbValue(attempt.ScreenshotPath));
        command.Parameters.AddWithValue("$trace", DbValue(attempt.TracePath));
        command.Parameters.AddWithValue("$dryRun", attempt.IsDryRun ? 1 : 0);
    }

    private static BookingAttempt ReadAttempt(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        RuleId = Guid.Parse(reader.GetString(1)),
        TargetDate = ParseDate(reader.GetString(2)),
        ScheduledForUtc = ParseTimestamp(reader.GetString(3)),
        StartedAtUtc = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
        CompletedAtUtc = reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
        Status = (BookingAttemptStatus)reader.GetInt32(6),
        SelectedCourt = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        SelectedStartTime = reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
        ErrorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
        SanitizedMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
        ScreenshotPath = reader.IsDBNull(11) ? null : reader.GetString(11),
        TracePath = reader.IsDBNull(12) ? null : reader.GetString(12),
        IsDryRun = reader.GetInt64(13) != 0,
    };

    private static void ValidateAttempt(BookingAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Id == Guid.Empty || attempt.RuleId == Guid.Empty)
        {
            throw new ArgumentException("Attempt and rule IDs cannot be empty.", nameof(attempt));
        }

        if (!Enum.IsDefined(attempt.Status))
        {
            throw new ArgumentException("The attempt status is invalid.", nameof(attempt));
        }

        if (attempt.SelectedCourt is not null and (< 1 or > 4))
        {
            throw new ArgumentException("A selected court must be a clay court from 1 to 4.", nameof(attempt));
        }

        if (attempt.SanitizedMessage?.Length > 4_000 || attempt.ErrorCode?.Length > 100)
        {
            throw new ArgumentException("Attempt diagnostic text is too long.", nameof(attempt));
        }

        if (attempt.CompletedAtUtc < attempt.StartedAtUtc)
        {
            throw new ArgumentException("Attempt completion cannot precede its start.", nameof(attempt));
        }
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string FormatGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);

    private static string FormatTime(TimeOnly value) => value.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record RuleRow(
        Guid Id,
        string Name,
        BookingRuleKind Kind,
        DateOnly? TargetDate,
        BookingType BookingType,
        int DurationMinutes,
        bool Enabled,
        DateTimeOffset? TermsAuthorizedAtUtc,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed class OccurrenceLease : IOccurrenceLease
    {
        private readonly SqliteBookingRepository _repository;
        private int _disposed;

        public OccurrenceLease(
            SqliteBookingRepository repository,
            DateOnly targetDate,
            string ownerToken,
            DateTimeOffset expiresAtUtc)
        {
            _repository = repository;
            TargetDate = targetDate;
            OwnerToken = ownerToken;
            ExpiresAtUtc = expiresAtUtc;
        }

        public DateOnly TargetDate { get; }

        public string OwnerToken { get; }

        public DateTimeOffset ExpiresAtUtc { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _repository.ReleaseOccurrenceLockAsync(TargetDate, OwnerToken).ConfigureAwait(false);
            }
        }
    }

    private const string RuleSelectSql = """
        SELECT id, name, kind, target_date, booking_type, duration_minutes, enabled,
               terms_authorized_at_utc, created_at_utc, updated_at_utc
        FROM booking_rules
        """;

    private const string AttemptSelectSql = """
        SELECT id, rule_id, target_date, scheduled_for_utc, started_at_utc, completed_at_utc,
               status, selected_court, selected_start_time, error_code, sanitized_message,
               screenshot_path, trace_path, is_dry_run
        FROM booking_attempts
        """;

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS booking_rules (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
            kind INTEGER NOT NULL,
            target_date TEXT NULL,
            booking_type INTEGER NOT NULL,
            duration_minutes INTEGER NOT NULL,
            enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
            terms_authorized_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_rule_times (
            rule_id TEXT NOT NULL REFERENCES booking_rules(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            time_value TEXT NOT NULL,
            PRIMARY KEY (rule_id, ordinal),
            UNIQUE (rule_id, time_value)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_rule_weekdays (
            rule_id TEXT NOT NULL REFERENCES booking_rules(id) ON DELETE CASCADE,
            weekday INTEGER NOT NULL CHECK(weekday BETWEEN 0 AND 6),
            PRIMARY KEY (rule_id, weekday)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_rule_players (
            rule_id TEXT NOT NULL REFERENCES booking_rules(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            player_id TEXT NOT NULL,
            PRIMARY KEY (rule_id, ordinal),
            UNIQUE (rule_id, player_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_rule_courts (
            rule_id TEXT NOT NULL REFERENCES booking_rules(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            court_number INTEGER NOT NULL CHECK(court_number BETWEEN 1 AND 4),
            PRIMARY KEY (rule_id, ordinal),
            UNIQUE (rule_id, court_number)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_schedule_claims (
            rule_id TEXT NOT NULL REFERENCES booking_rules(id) ON DELETE CASCADE,
            claim_type TEXT NOT NULL CHECK(claim_type IN ('date', 'weekday')),
            claim_value TEXT NOT NULL,
            PRIMARY KEY (claim_type, claim_value),
            UNIQUE (rule_id, claim_type, claim_value)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_booking_schedule_claims_rule
            ON booking_schedule_claims(rule_id);

        CREATE TABLE IF NOT EXISTS booking_attempts (
            id TEXT PRIMARY KEY NOT NULL,
            rule_id TEXT NOT NULL,
            target_date TEXT NOT NULL,
            scheduled_for_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            status INTEGER NOT NULL,
            selected_court INTEGER NULL CHECK(selected_court IS NULL OR selected_court BETWEEN 1 AND 4),
            selected_start_time TEXT NULL,
            error_code TEXT NULL,
            sanitized_message TEXT NULL,
            screenshot_path TEXT NULL,
            trace_path TEXT NULL,
            is_dry_run INTEGER NOT NULL CHECK(is_dry_run IN (0, 1))
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_booking_attempts_target_date
            ON booking_attempts(target_date, status);
        CREATE INDEX IF NOT EXISTS ix_booking_attempts_scheduled
            ON booking_attempts(scheduled_for_utc DESC);

        CREATE TABLE IF NOT EXISTS app_settings (
            id INTEGER PRIMARY KEY NOT NULL CHECK(id = 1),
            external_configuration_path TEXT NULL,
            unattended_submission_enabled INTEGER NOT NULL CHECK(unattended_submission_enabled IN (0, 1)),
            visible_browser_by_default INTEGER NOT NULL CHECK(visible_browser_by_default IN (0, 1)),
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS booking_occurrence_locks (
            target_date TEXT PRIMARY KEY NOT NULL,
            owner_token TEXT NOT NULL,
            acquired_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL
        ) STRICT;

        PRAGMA user_version = 1;
        """;
}
