using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Parsing;

namespace RockcliffeCourtBooker.Automation.Matchpoint;

internal sealed class MatchpointPage
{
    public const string LoginUrl = "https://rockcliffelawntennisclub-cad.matchpoint.com.es/Login.aspx";
    public const string GridUrl = "https://rockcliffelawntennisclub-cad.matchpoint.com.es/Booking/Grid.aspx";

    private const string TrustedHost = "rockcliffelawntennisclub-cad.matchpoint.com.es";
    private static readonly TimeSpan SubmissionCutoffGuard = TimeSpan.FromMilliseconds(250);

    private static readonly Regex CaptchaOrMfaPattern = new(
        @"\b(captcha|verification\s+code|security\s+code|two[- ]factor|multi[- ]factor|authenticator)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LoginPattern = new(
        @"\b(log\s*in|login|sign\s*in)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex BusyPattern = new(
        @"\b(busy|booked|occupied|unavailable|reserved)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NoLongerAvailablePattern = new(
        @"\b(no\s+longer\s+available|already\s+booked|was\s+just\s+booked|has\s+just\s+been\s+booked|slot\s+is\s+unavailable|court\s+is\s+unavailable)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SubmissionSuccessPattern = new(
        @"\b(booking|reservation)\b.{0,80}\b(confirmed|complete(?:d)?|successful(?:ly)?)\b|\b(successfully|confirmed)\b.{0,80}\b(booked|reserved)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex SubmissionRejectedPattern = new(
        @"\b(?:booking|reservation)\b.{0,80}\b(?:was\s+not\s+created|has\s+not\s+been\s+created|could\s+not\s+be\s+created|rejected)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly IPage _page;
    private BookingCandidate? _selectedCandidate;
    private BookingKind? _selectedBookingKind;
    private int? _selectedDurationMinutes;

    public MatchpointPage(IPage page)
    {
        _page = page;
    }

    public async Task LogInAsync(MatchpointCredentials credentials, CancellationToken cancellationToken)
    {
        await _page.GotoAsync(LoginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        EnsureTrustedMatchpointUri(_page.Url);
        await ThrowIfCaptchaOrMfaAsync();

        var username = await FirstVisibleAsync(
            _page.Locator("input[autocomplete='username']"),
            _page.Locator("input[type='email']"),
            _page.Locator("input[name*='user' i]"),
            _page.Locator("input[id*='user' i]"),
            _page.Locator("input[name*='login' i]"));
        var password = await FirstVisibleAsync(_page.Locator("input[type='password']"));

        if (username is null || password is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The Matchpoint login fields could not be identified.");
        }

        await EnsureCredentialSubmissionTargetAsync(password, submit: null);

        await username.FillAsync(credentials.Username);
        await password.FillAsync(credentials.Password);

        var submit = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = LoginPattern }),
            _page.Locator("button[type='submit']"),
            _page.Locator("input[type='submit']"));
        if (submit is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The Matchpoint login button could not be identified.");
        }

        await EnsureCredentialSubmissionTargetAsync(password, submit);

        await submit.ClickAsync();
        await WaitForDomAsync();
        EnsureTrustedMatchpointUri(_page.Url);
        cancellationToken.ThrowIfCancellationRequested();
        await ThrowIfCaptchaOrMfaAsync();

        var passwordStillVisible = await IsAnyVisibleAsync(_page.Locator("input[type='password']"));
        if (passwordStillVisible || _page.Url.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.AuthenticationFailed,
                "Matchpoint did not accept the supplied account credentials.");
        }
    }

    public async Task OpenGridAsync(BookingKind bookingKind, DateOnly targetDate)
    {
        ClearCheckoutSelection();
        await _page.GotoAsync(GridUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        EnsureTrustedMatchpointUri(_page.Url);
        await ThrowIfCaptchaOrMfaAsync();
        if (_page.Url.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.AuthenticationFailed,
                "The Matchpoint session ended before the booking grid opened.");
        }

        await SelectBookingKindAsync(bookingKind);
        await SelectDateAsync(targetDate);
        EnsureTrustedMatchpointUri(_page.Url);
    }

    public async Task RefreshGridAsync(BookingKind bookingKind, DateOnly targetDate)
    {
        ClearCheckoutSelection();
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        EnsureTrustedMatchpointUri(_page.Url);
        await ThrowIfCaptchaOrMfaAsync();
        await SelectBookingKindAsync(bookingKind);
        await SelectDateAsync(targetDate);
    }

    public async Task<bool> IsGridCandidateOpenAsync(BookingCandidate candidate)
    {
        var cell = await FindGridCellAsync(candidate);
        return cell is not null && await IsOpenCellAsync(cell);
    }

    public async Task OpenDurationAsync(BookingCandidate candidate, BookingKind bookingKind, int durationMinutes)
    {
        var cell = await FindGridCellAsync(candidate);
        if (cell is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                $"Could not identify court {candidate.CourtNumber} at {candidate.StartTime:HH\\:mm} in the booking grid.");
        }

        if (!await IsOpenCellAsync(cell))
        {
            throw new CandidateUnavailableException(
                $"Court {candidate.CourtNumber} at {candidate.StartTime:HH\\:mm} is occupied.");
        }

        await cell.ClickAsync();
        await _page.WaitForTimeoutAsync(150);
        var visibleText = await VisibleBodyTextAsync();
        if (NoLongerAvailablePattern.IsMatch(visibleText) ||
            visibleText.Contains("let me know if court becomes available", StringComparison.OrdinalIgnoreCase))
        {
            await CloseDialogIfPresentAsync();
            throw new CandidateUnavailableException(
                $"Court {candidate.CourtNumber} at {candidate.StartTime:HH\\:mm} became unavailable.");
        }

        var kindPattern = bookingKind == BookingKind.Singles ? "Singles?" : "Doubles?";
        var durationPattern = new Regex(
            $@"^\s*{durationMinutes}\s*min(?:ute)?s?\s+{kindPattern}\s*$",
            RegexOptions.IgnoreCase);
        var duration = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = durationPattern }),
            _page.GetByRole(AriaRole.Link, new() { NameRegex = durationPattern }));

        if (duration is null)
        {
            await CloseDialogIfPresentAsync();
            throw new CandidateUnavailableException(
                $"The requested {durationMinutes}-minute duration is not available on court {candidate.CourtNumber} at {candidate.StartTime:HH\\:mm}.");
        }

        await duration.ClickAsync();
        await WaitForDomAsync();
        EnsureTrustedMatchpointUri(_page.Url);
        visibleText = await VisibleBodyTextAsync();
        if (NoLongerAvailablePattern.IsMatch(visibleText))
        {
            throw new CandidateUnavailableException(
                $"Court {candidate.CourtNumber} at {candidate.StartTime:HH\\:mm} was lost before checkout.");
        }

        _selectedCandidate = candidate;
        _selectedBookingKind = bookingKind;
        _selectedDurationMinutes = durationMinutes;
    }

    public async Task SelectPlayersAndValidateCheckoutAsync(
        MatchpointBookingRequest request,
        BookingCandidate candidate)
    {
        await OpenPlayerSelectionAsync();

        foreach (var playerName in request.PartnerDisplayNames)
        {
            await SelectExactPlayerAsync(playerName);
        }

        var ok = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^\\s*OK\\s*$", RegexOptions.IgnoreCase) }));
        if (ok is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The player selection OK button could not be identified.");
        }

        await ok.ClickAsync();
        await WaitForDomAsync();
        EnsureTrustedMatchpointUri(_page.Url);
        await ThrowIfCandidateLostAsync();

        await ValidateCheckoutAsync(request, candidate, requireAcceptedTerms: false);
    }

    public async Task OpenPlayerSelectionAsync()
    {
        await ThrowIfCandidateLostAsync();
        var modify = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^\\s*Modify\\s*$", RegexOptions.IgnoreCase) }),
            _page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^\\s*Modify\\s*$", RegexOptions.IgnoreCase) }));
        if (modify is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The player Modify control could not be identified on checkout.");
        }

        await modify.ClickAsync();
        await WaitForDomAsync();
        EnsureTrustedMatchpointUri(_page.Url);
        await ThrowIfCandidateLostAsync();
    }

    public async Task VerifyExactPlayersPresentAsync(IReadOnlyList<string> displayNames)
    {
        foreach (var displayName in displayNames)
        {
            _ = await FindExactPlayerTextAsync(displayName);
        }
    }

    public async Task AcceptTermsAsync()
    {
        var checkbox = await FindTermsCheckboxAsync();
        if (checkbox is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The legal-conditions checkbox could not be identified.");
        }

        if (!await checkbox.IsCheckedAsync())
        {
            await checkbox.CheckAsync();
        }

        if (!await checkbox.IsCheckedAsync())
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The legal-conditions checkbox could not be selected.");
        }
    }

    public async Task<SubmissionOutcome> SubmitOnceAsync(
        MatchpointBookingRequest request,
        BookingCandidate candidate,
        CancellationToken cancellationToken,
        Action? onSubmissionStarted = null)
    {
        // Re-read every safety-critical checkout value immediately before locating
        // and invoking the one final submission control. The earlier checkout
        // validation is not trusted because the page may have changed since then.
        await ValidateCheckoutAsync(request, candidate, requireAcceptedTerms: true);

        var bookPattern = new Regex("^\\s*Book\\s*$", RegexOptions.IgnoreCase);
        var book = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = bookPattern }),
            _page.GetByRole(AriaRole.Link, new() { NameRegex = bookPattern }));
        if (book is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The final Book button could not be identified.");
        }

        // This is deliberately adjacent to the sole final-submission click. Nothing in this class retries it.
        EnsureTrustedMatchpointUri(_page.Url);
        cancellationToken.ThrowIfCancellationRequested();
        LocatorClickOptions? clickOptions = null;
        if (request.SubmissionCutoffAtUtc is { } cutoff)
        {
            var clickBudget = cutoff - DateTimeOffset.UtcNow - SubmissionCutoffGuard;
            if (clickBudget <= TimeSpan.Zero)
            {
                throw new MatchpointAutomationException(
                    BookingAutomationStatus.Cancelled,
                    "The unattended booking cutoff was reached before final submission began.");
            }

            // Playwright can auto-wait for an actionable control. Bound that wait
            // just before the cutoff so a button enabled later cannot be clicked.
            clickOptions = new LocatorClickOptions
            {
                Timeout = (float)Math.Max(1, Math.Min(10_000, clickBudget.TotalMilliseconds)),
            };
        }

        onSubmissionStarted?.Invoke();
        await book.ClickAsync(clickOptions);
        await WaitForDomAsync();
        var body = await VisibleBodyTextAsync();

        var hasSuccessEvidence = SubmissionSuccessPattern.IsMatch(body);
        var hasRejectionEvidence = NoLongerAvailablePattern.IsMatch(body) ||
                                   SubmissionRejectedPattern.IsMatch(body);

        try
        {
            await OpenGridAsync(request.BookingKind, request.TargetDate);
            var gridState = await GetPostSubmissionGridStateAsync(candidate);
            if (gridState == PostSubmissionGridState.Owned)
            {
                return SubmissionOutcome.Confirmed;
            }

            // Only an unambiguous rejection plus an independently refreshed grid
            // showing no ownership permits a retry. Any success or mixed message
            // without ownership is uncertain and stops the worker.
            return hasRejectionEvidence &&
                   !hasSuccessEvidence &&
                   gridState == PostSubmissionGridState.Open
                ? SubmissionOutcome.ConclusivelyRejected
                : SubmissionOutcome.Uncertain;
        }
        catch (Exception exception) when (exception is MatchpointAutomationException or PlaywrightException)
        {
            // The final click already occurred. Failure to independently prove ownership is uncertain, never success.
            return SubmissionOutcome.Uncertain;
        }
    }

    public async Task NavigateBackToGridAsync(BookingKind bookingKind, DateOnly targetDate)
    {
        await OpenGridAsync(bookingKind, targetDate);
    }

    public async Task ClearSensitiveInputsAsync()
    {
        var passwordInputs = _page.Locator("input[type='password']");
        var count = await passwordInputs.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var input = passwordInputs.Nth(index);
            if (await input.IsVisibleAsync())
            {
                await input.FillAsync(string.Empty);
            }
        }
    }

    private async Task SelectBookingKindAsync(BookingKind bookingKind)
    {
        var expected = bookingKind == BookingKind.Singles ? "SINGLES" : "DOUBLES";
        var other = bookingKind == BookingKind.Singles ? "DOUBLES" : "SINGLES";
        var body = await VisibleBodyTextAsync();
        if (Regex.IsMatch(body, $@"\b{expected}\s+BOOKINGS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return;
        }

        var selectorPattern = new Regex(
            $@"\b({expected}|{other})\s+BOOKINGS\b",
            RegexOptions.IgnoreCase);
        var selector = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = selectorPattern }),
            _page.GetByRole(AriaRole.Link, new() { NameRegex = selectorPattern }));
        if (selector is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The singles/doubles timetable selector could not be identified.");
        }

        await selector.ClickAsync();
        var expectedPattern = new Regex(
            $@"\b{expected}(?:\s+BOOKINGS)?\b",
            RegexOptions.IgnoreCase);
        var expectedOption = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Option, new() { NameRegex = expectedPattern }),
            _page.GetByRole(AriaRole.Menuitem, new() { NameRegex = expectedPattern }),
            _page.GetByText(expectedPattern));
        if (expectedOption is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                $"The {expected.ToLowerInvariant()} timetable option could not be identified.");
        }

        await expectedOption.ClickAsync();
        await WaitForDomAsync();
    }

    private async Task SelectDateAsync(DateOnly targetDate)
    {
        if (await DateSelectionMatchesAsync(targetDate))
        {
            return;
        }

        var isoDate = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dateInput = await FirstVisibleAsync(_page.Locator("input[type='date']"));
        if (dateInput is not null)
        {
            await dateInput.FillAsync(isoDate);
            await dateInput.DispatchEventAsync("change");
            await WaitForDomAsync();
            if (await DateSelectionMatchesAsync(targetDate))
            {
                return;
            }
        }

        var select = await FirstVisibleAsync(_page.Locator("select"));
        if (select is not null && await TrySelectDateOptionAsync(select, targetDate))
        {
            await WaitForDomAsync();
            if (await DateSelectionMatchesAsync(targetDate))
            {
                return;
            }
        }

        var dateTriggerPattern = new Regex(
            @"\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\b.*\b\d{4}\b",
            RegexOptions.IgnoreCase);
        var dateTrigger = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new() { NameRegex = dateTriggerPattern }),
            _page.GetByRole(AriaRole.Combobox, new() { NameRegex = dateTriggerPattern }));
        if (dateTrigger is not null)
        {
            await dateTrigger.ClickAsync();
            var exactDatePattern = BuildDatePattern(targetDate);
            var dateOption = await FirstVisibleAsync(
                _page.GetByRole(AriaRole.Button, new() { NameRegex = exactDatePattern }),
                _page.GetByRole(AriaRole.Gridcell, new() { NameRegex = exactDatePattern }),
                _page.GetByRole(AriaRole.Option, new() { NameRegex = exactDatePattern }),
                _page.GetByText(exactDatePattern));
            if (dateOption is not null)
            {
                await dateOption.ClickAsync();
                await WaitForDomAsync();
                if (await DateSelectionMatchesAsync(targetDate))
                {
                    return;
                }
            }
        }

        throw new MatchpointAutomationException(
            BookingAutomationStatus.PageStructureChanged,
            $"The booking date {targetDate:yyyy-MM-dd} could not be selected or verified.");
    }

    private async Task<bool> DateSelectionMatchesAsync(DateOnly targetDate)
    {
        var isoDate = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var inputs = _page.Locator("input[type='date']");
        var inputCount = await inputs.CountAsync();
        for (var index = 0; index < inputCount; index++)
        {
            var input = inputs.Nth(index);
            if (await input.IsVisibleAsync() &&
                string.Equals(await input.InputValueAsync(), isoDate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return BuildDatePattern(targetDate).IsMatch(await VisibleBodyTextAsync());
    }

    private static Regex BuildDatePattern(DateOnly date)
    {
        var month = Regex.Escape(date.ToString("MMMM", CultureInfo.InvariantCulture));
        var abbreviatedMonth = Regex.Escape(date.ToString("MMM", CultureInfo.InvariantCulture));
        return new Regex(
            $@"(?:\b{date.Day}\s+(?:{month}|{abbreviatedMonth})\s+{date.Year}\b|\b(?:{month}|{abbreviatedMonth})\s+{date.Day},?\s+{date.Year}\b|\b{date.Day:00}[/-]{date.Month:00}[/-]{date.Year}\b)",
            RegexOptions.IgnoreCase);
    }

    private static async Task<bool> TrySelectDateOptionAsync(ILocator select, DateOnly targetDate)
    {
        var options = select.Locator("option");
        var count = await options.CountAsync();
        var pattern = BuildDatePattern(targetDate);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            if (pattern.IsMatch(await option.InnerTextAsync()))
            {
                var value = await option.GetAttributeAsync("value");
                if (!string.IsNullOrEmpty(value))
                {
                    await select.SelectOptionAsync(value);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<ILocator?> FindGridCellAsync(BookingCandidate candidate)
    {
        var time = candidate.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        var attributeSelectors = new[]
        {
            $"[data-court='{candidate.CourtNumber}'][data-time='{time}']",
            $"[data-court-number='{candidate.CourtNumber}'][data-start-time='{time}']",
            $"[data-resource='{candidate.CourtNumber}'][data-time='{time}']",
        };
        foreach (var selector in attributeSelectors)
        {
            var cell = await FirstVisibleAsync(_page.Locator(selector));
            if (cell is not null)
            {
                return cell;
            }
        }

        var tables = _page.Locator("table");
        var tableCount = await tables.CountAsync();
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            var result = await FindCellInTabularContainerAsync(
                tables.Nth(tableIndex),
                "th",
                "tr",
                "th, td",
                candidate,
                time);
            if (result is not null)
            {
                return result;
            }
        }

        var grids = _page.Locator("[role='grid']");
        var gridCount = await grids.CountAsync();
        for (var gridIndex = 0; gridIndex < gridCount; gridIndex++)
        {
            var result = await FindCellInTabularContainerAsync(
                grids.Nth(gridIndex),
                "[role='columnheader']",
                "[role='row']",
                "[role='rowheader'], [role='gridcell']",
                candidate,
                time);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static async Task<ILocator?> FindCellInTabularContainerAsync(
        ILocator container,
        string headerSelector,
        string rowSelector,
        string cellSelector,
        BookingCandidate candidate,
        string time)
    {
        var headers = container.Locator(headerSelector);
        var headerCount = await headers.CountAsync();
        var columnIndex = -1;
        for (var index = 0; index < headerCount; index++)
        {
            var header = NormalizeWhitespace(await headers.Nth(index).InnerTextAsync());
            if (Regex.IsMatch(
                    header,
                    $@"^\s*{candidate.CourtNumber}\s*Clay\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                columnIndex = index;
                break;
            }
        }

        if (columnIndex < 0)
        {
            return null;
        }

        var rows = container.Locator(rowSelector);
        var rowCount = await rows.CountAsync();
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows.Nth(rowIndex);
            var cells = row.Locator(cellSelector);
            var cellCount = await cells.CountAsync();
            if (cellCount <= columnIndex)
            {
                continue;
            }

            var rowText = NormalizeWhitespace(await cells.Nth(0).InnerTextAsync());
            if (string.Equals(rowText, time, StringComparison.Ordinal) ||
                rowText.StartsWith(time + " ", StringComparison.Ordinal))
            {
                var cell = cells.Nth(columnIndex);
                if (await cell.IsVisibleAsync())
                {
                    return cell;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsOpenCellAsync(ILocator cell)
    {
        if (!await cell.IsVisibleAsync() || !await cell.IsEnabledAsync())
        {
            return false;
        }

        var className = await cell.GetAttributeAsync("class") ?? string.Empty;
        var status = await cell.GetAttributeAsync("data-status") ?? string.Empty;
        var ariaDisabled = await cell.GetAttributeAsync("aria-disabled") ?? string.Empty;
        var text = NormalizeWhitespace(await cell.InnerTextAsync());
        var hasBusyDescendant = await cell.Locator(
            ".busy, .booked, .occupied, .unavailable, [data-status='busy'], [data-status='booked']")
            .CountAsync() > 0;

        return !hasBusyDescendant &&
               !BusyPattern.IsMatch(className) &&
               !BusyPattern.IsMatch(status) &&
               !string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase) &&
               !BusyPattern.IsMatch(text) &&
               !Regex.IsMatch(text, @"\b\d{1,2}:\d{2}\s*[-–]\s*\d{1,2}:\d{2}\b", RegexOptions.CultureInvariant);
    }

    private async Task SelectExactPlayerAsync(string displayName)
    {
        var playerText = await FindExactPlayerTextAsync(displayName);
        var container = playerText.Locator("xpath=ancestor::*[.//input[@type='checkbox']][1]");
        var checkbox = await FirstVisibleAsync(
            container.Locator("input[type='checkbox']"),
            playerText.Locator("xpath=following::input[@type='checkbox'][1]"));
        if (checkbox is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PlayerMismatch,
                $"The checkbox for configured player '{displayName}' could not be identified.");
        }

        if (!await checkbox.IsCheckedAsync())
        {
            await checkbox.CheckAsync();
        }
    }

    private async Task<ILocator> FindExactPlayerTextAsync(string displayName)
    {
        await ThrowIfCandidateLostAsync();
        var matches = _page.GetByText(displayName, new() { Exact = true });
        var visibleMatches = new List<ILocator>();
        var matchCount = await matches.CountAsync();
        for (var index = 0; index < matchCount; index++)
        {
            var match = matches.Nth(index);
            if (await match.IsVisibleAsync())
            {
                visibleMatches.Add(match);
            }
        }

        if (visibleMatches.Count != 1)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PlayerMismatch,
                visibleMatches.Count == 0
                    ? $"Configured player '{displayName}' was not found."
                    : $"Configured player '{displayName}' was ambiguous on the player screen.");
        }

        return visibleMatches[0];
    }

    private static void ValidateCheckoutDetails(
        string body,
        MatchpointBookingRequest request,
        BookingCandidate candidate,
        IReadOnlyList<string> actualRoster)
    {
        if (!Regex.IsMatch(
                body,
                $@"\b{candidate.CourtNumber}\s+Clay\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout court does not match the selected clay court.");
        }

        if (!BuildDatePattern(request.TargetDate).IsMatch(body))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout date does not match the requested date.");
        }

        var endTime = candidate.StartTime.AddMinutes(request.DurationMinutes);
        var timeRangePattern = new Regex(
            $@"\b{candidate.StartTime:HH\\:mm}\s*[-–]\s*{endTime:HH\\:mm}\b",
            RegexOptions.CultureInvariant);
        if (!timeRangePattern.IsMatch(body))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout time or duration does not match the request.");
        }

        if (!CheckoutRosterValidator.IsExactMatch(
                request.AccountMemberDisplayName,
                request.PartnerDisplayNames,
                actualRoster))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PlayerMismatch,
                "The checkout roster does not exactly match the account holder and requested partners.");
        }
    }

    private async Task ValidateCheckoutAsync(
        MatchpointBookingRequest request,
        BookingCandidate candidate,
        bool requireAcceptedTerms)
    {
        await ThrowIfCandidateLostAsync();
        ValidateCheckoutSelection(request, candidate);

        var body = await VisibleBodyTextAsync();
        var checkoutRoster = await ReadCheckoutRosterAsync();
        ValidateCheckoutDetails(body, request, candidate, checkoutRoster);
        await ValidateStructuredBookingKindIfPresentAsync(request.BookingKind);

        if (!PriceTextParser.TryParseDisplayedTotal(body, out var total))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The full-court price could not be read unambiguously.");
        }

        if (total != decimal.Zero)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.NonZeroPrice,
                "The booking total is not $0.00; no booking was submitted.");
        }

        if (requireAcceptedTerms)
        {
            var terms = await FindTermsCheckboxAsync();
            if (terms is null || !await terms.IsCheckedAsync())
            {
                throw new MatchpointAutomationException(
                    BookingAutomationStatus.PageStructureChanged,
                    "The legal conditions were not still accepted immediately before submission.");
            }
        }
    }

    private void ValidateCheckoutSelection(
        MatchpointBookingRequest request,
        BookingCandidate candidate)
    {
        if (_selectedCandidate != candidate ||
            _selectedBookingKind != request.BookingKind ||
            _selectedDurationMinutes != request.DurationMinutes)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The selected booking type, court, start time, or duration no longer matches the checkout request.");
        }
    }

    private async Task ValidateStructuredBookingKindIfPresentAsync(BookingKind expected)
    {
        var evidence = _page.Locator(
            "[data-booking-kind], [data-booking-type], [data-reservation-kind], " +
            "input[type='hidden'][name*='bookingtype' i], input[type='hidden'][name*='reservationtype' i]");
        var detected = new HashSet<BookingKind>();
        var count = await evidence.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var item = evidence.Nth(index);
            var value = await item.GetAttributeAsync("data-booking-kind") ??
                        await item.GetAttributeAsync("data-booking-type") ??
                        await item.GetAttributeAsync("data-reservation-kind") ??
                        await item.GetAttributeAsync("value") ??
                        await item.InnerTextAsync();
            if (Regex.IsMatch(value, @"^\s*singles?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                detected.Add(BookingKind.Singles);
            }
            else if (Regex.IsMatch(value, @"^\s*doubles?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                detected.Add(BookingKind.Doubles);
            }
        }

        if (detected.Count > 0 && (detected.Count != 1 || !detected.Contains(expected)))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout booking type does not match the requested singles/doubles timetable.");
        }
    }

    private async Task<ILocator?> FindTermsCheckboxAsync()
    {
        var termsPattern = new Regex(
            @"accept\s+the\s+legal\s+conditions",
            RegexOptions.IgnoreCase);
        return await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Checkbox, new() { NameRegex = termsPattern }),
            _page.GetByLabel(termsPattern));
    }

    private void ClearCheckoutSelection()
    {
        _selectedCandidate = null;
        _selectedBookingKind = null;
        _selectedDurationMinutes = null;
    }

    private async Task<IReadOnlyList<string>> ReadCheckoutRosterAsync()
    {
        var playersHeadingPattern = new Regex(
            "^\\s*PLAYERS\\s*$",
            RegexOptions.IgnoreCase);
        var heading = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Heading, new() { NameRegex = playersHeadingPattern }),
            _page.GetByText(playersHeadingPattern));
        if (heading is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout player roster heading could not be identified.");
        }

        var roster = await FirstVisibleAsync(
            heading.Locator("xpath=ancestor::section[1]"),
            heading.Locator("xpath=ancestor::*[@data-section='players'][1]"),
            heading.Locator("xpath=parent::*"));
        if (roster is null)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The checkout player roster container could not be identified.");
        }

        var entrySelectors = new[]
        {
            "[data-player-name]",
            "[data-testid='player-name']",
            ".selected-player",
            ".booking-player-name",
            ".booking-player .player-name",
        };
        foreach (var selector in entrySelectors)
        {
            var entries = roster.Locator(selector);
            var names = new List<string>();
            var count = await entries.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var entry = entries.Nth(index);
                if (!await entry.IsVisibleAsync())
                {
                    continue;
                }

                var name = await entry.GetAttributeAsync("data-player-name") ?? await entry.InnerTextAsync();
                name = NormalizeWhitespace(name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            if (names.Count > 0)
            {
                return names;
            }
        }

        throw new MatchpointAutomationException(
            BookingAutomationStatus.PageStructureChanged,
            "The checkout roster could not be read as structured player entries.");
    }

    private async Task ThrowIfCandidateLostAsync()
    {
        if (NoLongerAvailablePattern.IsMatch(await VisibleBodyTextAsync()))
        {
            throw new CandidateUnavailableException(
                "The selected court became unavailable before final submission.");
        }
    }

    private async Task<PostSubmissionGridState> GetPostSubmissionGridStateAsync(BookingCandidate candidate)
    {
        if (!_page.Url.Contains("Grid", StringComparison.OrdinalIgnoreCase))
        {
            return PostSubmissionGridState.UnknownOrBusy;
        }

        var cell = await FindGridCellAsync(candidate);
        if (cell is null)
        {
            return PostSubmissionGridState.UnknownOrBusy;
        }

        var ownBooking = cell.Locator(
            ".your-booking, .my-booking, [data-status='mine'], [data-booking-owner='self']");
        if (await ownBooking.CountAsync() > 0)
        {
            return PostSubmissionGridState.Owned;
        }

        var className = await cell.GetAttributeAsync("class") ?? string.Empty;
        if (Regex.IsMatch(
                className,
                @"\b(your-booking|my-booking|mine)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return PostSubmissionGridState.Owned;
        }

        return await IsOpenCellAsync(cell)
            ? PostSubmissionGridState.Open
            : PostSubmissionGridState.UnknownOrBusy;
    }

    private enum PostSubmissionGridState
    {
        UnknownOrBusy,
        Open,
        Owned,
    }

    private async Task ThrowIfCaptchaOrMfaAsync()
    {
        if (CaptchaOrMfaPattern.IsMatch(await VisibleBodyTextAsync()))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.CaptchaOrMfaRequired,
                "Matchpoint requires CAPTCHA or additional authentication; automation stopped.");
        }
    }

    private async Task EnsureCredentialSubmissionTargetAsync(ILocator password, ILocator? submit)
    {
        EnsureTrustedMatchpointUri(_page.Url);

        var form = password.Locator("xpath=ancestor::form[1]");
        if (await form.CountAsync() > 0)
        {
            EnsureTrustedMatchpointTarget(await form.GetAttributeAsync("action"));
        }

        if (submit is not null)
        {
            EnsureTrustedMatchpointTarget(await submit.GetAttributeAsync("formaction"));
        }
    }

    private void EnsureTrustedMatchpointTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (!Uri.TryCreate(new Uri(_page.Url), target, out var resolved))
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "The Matchpoint login submission target is invalid; credentials were not submitted.");
        }

        EnsureTrustedMatchpointUri(resolved.AbsoluteUri);
    }

    private static void EnsureTrustedMatchpointUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, TrustedHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443)
        {
            throw new MatchpointAutomationException(
                BookingAutomationStatus.PageStructureChanged,
                "Browser navigation left the trusted HTTPS Matchpoint origin; automation stopped safely.");
        }
    }

    private async Task CloseDialogIfPresentAsync()
    {
        var close = await FirstVisibleAsync(
            _page.GetByRole(AriaRole.Button, new()
            {
                NameRegex = new Regex(@"^\s*(close|×|x)\s*$", RegexOptions.IgnoreCase),
            }));
        if (close is not null)
        {
            await close.ClickAsync();
            return;
        }

        await _page.Keyboard.PressAsync("Escape");
    }

    private async Task<string> VisibleBodyTextAsync()
    {
        return await _page.Locator("body").InnerTextAsync();
    }

    private async Task WaitForDomAsync()
    {
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10_000 });
        }
        catch (PlaywrightException)
        {
            // Some Matchpoint actions update the current page without a navigation.
        }
    }

    private static async Task<ILocator?> FirstVisibleAsync(params ILocator[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var count = await candidate.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var item = candidate.Nth(index);
                if (await item.IsVisibleAsync())
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsAnyVisibleAsync(ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var index = 0; index < count; index++)
        {
            if (await locator.Nth(index).IsVisibleAsync())
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeWhitespace(string value)
    {
        return Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }
}
