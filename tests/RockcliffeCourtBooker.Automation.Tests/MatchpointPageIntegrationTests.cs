using Microsoft.Playwright;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Matchpoint;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class MatchpointPageIntegrationTests
{
    private static bool BrowserTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS"),
            "1",
            StringComparison.Ordinal);

    [Fact]
    public async Task AvailableCellPlayerSelectionAndZeroPriceReachValidatedCheckout()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Success,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();
            });
    }

    [Fact]
    public async Task OccupiedCellIsRejectedWithoutOpeningCheckout()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Success,
            async matchpoint =>
            {
                var occupied = new BookingCandidate(new TimeOnly(14, 0), 1);
                await Assert.ThrowsAsync<CandidateUnavailableException>(
                    () => matchpoint.OpenDurationAsync(occupied, BookingKind.Doubles, 90));
            });
    }

    [Fact]
    public async Task NonZeroPriceStopsBeforeSubmission()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.NonZeroPrice,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate));
                Assert.Equal(BookingAutomationStatus.NonZeroPrice, exception.Status);
            });
    }

    [Fact]
    public async Task AvailabilityRaceIsDetectedBeforeCheckout()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Race,
            async matchpoint =>
            {
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await Assert.ThrowsAsync<CandidateUnavailableException>(
                    () => matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90));
            });
    }

    [Fact]
    public async Task CheckoutRaceDuringPlayerSelectionIsRetriable()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.CheckoutRace,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);

                await Assert.ThrowsAsync<CandidateUnavailableException>(
                    () => matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate));
            });
    }

    [Fact]
    public async Task CheckoutRosterRejectsAnExtraPlayer()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.ExtraRosterPlayer,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);

                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate));
                Assert.Equal(BookingAutomationStatus.PlayerMismatch, exception.Status);
            });
    }

    [Fact]
    public async Task CheckoutRosterRejectsADuplicatePlayer()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.DuplicateRosterPlayer,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);

                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate));
                Assert.Equal(BookingAutomationStatus.PlayerMismatch, exception.Status);
            });
    }

    [Fact]
    public async Task ConclusiveSubmissionRejectionCanBeDistinguishedFromUncertainResult()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Rejected,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                var outcome = await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None);

                Assert.Equal(SubmissionOutcome.ConclusivelyRejected, outcome);
            });
    }

    [Fact]
    public async Task RejectionMessageWithBusyUnownedGridCellIsUncertain()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.RejectedWithBusyGridCell,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                Assert.Equal(
                    SubmissionOutcome.Uncertain,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task ConfirmedSubmissionRequiresPositiveConfirmation()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Success,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                Assert.Equal(
                    SubmissionOutcome.Confirmed,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task AmbiguousPostClickPageWithGridOwnershipIsConfirmed()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Uncertain,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                Assert.Equal(
                    SubmissionOutcome.Confirmed,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task PositiveMessageWithoutGridOwnershipIsUncertain()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.PositiveWithoutOwnership,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                Assert.Equal(
                    SubmissionOutcome.Uncertain,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task MixedSuccessAndFailureMessageCannotTriggerARetry()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.MixedSubmissionMessage,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                Assert.Equal(
                    SubmissionOutcome.Uncertain,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task CancellationImmediatelyBeforeBookPreventsTheClick()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Success,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => matchpoint.SubmitOnceAsync(request, candidate, cancellation.Token));
                Assert.Equal(
                    SubmissionOutcome.Confirmed,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task UnattendedCutoffPreventsTheFinalClick()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.Success,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SubmitOnceAsync(
                        request with { SubmissionCutoffAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
                        candidate,
                        CancellationToken.None));

                Assert.Equal(BookingAutomationStatus.Cancelled, exception.Status);
                Assert.Equal(
                    SubmissionOutcome.Confirmed,
                    await matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));
            });
    }

    [Fact]
    public async Task UnattendedCutoffBoundsPlaywrightActionabilityWait()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAndPageAsync(
            FixtureBehavior.DelayedBook,
            async (matchpoint, page) =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                await Assert.ThrowsAsync<TimeoutException>(
                    () => matchpoint.SubmitOnceAsync(
                        request with { SubmissionCutoffAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(750) },
                        candidate,
                        CancellationToken.None));

                await page.WaitForTimeoutAsync(1_000);
                Assert.Null(await page.EvaluateAsync<string?>("localStorage.getItem('fixture-booked')"));
            });
    }

    [Theory]
    [InlineData(FixtureBehavior.PriceChangesBeforeSubmit, BookingAutomationStatus.NonZeroPrice)]
    [InlineData(FixtureBehavior.KindChangesBeforeSubmit, BookingAutomationStatus.PageStructureChanged)]
    [InlineData(FixtureBehavior.RosterChangesBeforeSubmit, BookingAutomationStatus.PlayerMismatch)]
    public async Task FinalSubmissionRevalidatesCheckoutAfterTermsAreAccepted(
        FixtureBehavior behavior,
        BookingAutomationStatus expectedStatus)
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            behavior,
            async matchpoint =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();

                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));

                Assert.Equal(expectedStatus, exception.Status);
            });
    }

    [Fact]
    public async Task FinalSubmissionRequiresTermsToRemainAccepted()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAndPageAsync(
            FixtureBehavior.Success,
            async (matchpoint, page) =>
            {
                var request = Request();
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90);
                await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);
                await matchpoint.AcceptTermsAsync();
                await page.GetByRole(AriaRole.Checkbox).UncheckAsync();

                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.SubmitOnceAsync(request, candidate, CancellationToken.None));

                Assert.Equal(BookingAutomationStatus.PageStructureChanged, exception.Status);
            });
    }

    [Fact]
    public async Task ChangedGridMarkupFailsClosed()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        await RunWithFixtureAsync(
            FixtureBehavior.ChangedMarkup,
            async matchpoint =>
            {
                var candidate = new BookingCandidate(new TimeOnly(14, 0), 2);
                var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
                    () => matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 90));
                Assert.Equal(BookingAutomationStatus.PageStructureChanged, exception.Status);
            });
    }

    [Fact]
    public async Task LoginFormToUntrustedOriginNeverReceivesCredentials()
    {
        Assert.SkipUnless(BrowserTestsEnabled, "Set ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS=1 after installing Playwright Chromium.");
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync(
            "**/*",
            route => route.FulfillAsync(new RouteFulfillOptions
                {
                    ContentType = "text/html; charset=utf-8",
                    Body = """
                        <!doctype html><html><body>
                          <form action="https://untrusted.example/collect" method="post">
                            <label>Username <input name="username" autocomplete="username"></label>
                            <label>Password <input name="password" type="password"></label>
                            <button type="submit">Login</button>
                          </form>
                        </body></html>
                        """,
                }));
        var page = await context.NewPageAsync();
        var matchpoint = new MatchpointPage(page);

        var exception = await Assert.ThrowsAsync<MatchpointAutomationException>(
            () => matchpoint.LogInAsync(
                new MatchpointCredentials { Username = "member", Password = "fixture-password" },
                CancellationToken.None));

        Assert.Equal(BookingAutomationStatus.PageStructureChanged, exception.Status);
        Assert.Equal(string.Empty, await page.Locator("input[type='password']").InputValueAsync());
    }

    private static Task RunWithFixtureAsync(
        FixtureBehavior behavior,
        Func<MatchpointPage, Task> test) =>
        RunWithFixtureAndPageAsync(behavior, (matchpoint, _) => test(matchpoint));

    private static async Task RunWithFixtureAndPageAsync(
        FixtureBehavior behavior,
        Func<MatchpointPage, IPage, Task> test)
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync(
            "**/*",
            route => route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "text/html; charset=utf-8",
                Body = route.Request.Url.Contains("Login.aspx", StringComparison.OrdinalIgnoreCase)
                    ? LoginHtml
                    : GridHtml(behavior),
            }));
        var page = await context.NewPageAsync();
        var matchpoint = new MatchpointPage(page);
        await matchpoint.LogInAsync(
            new MatchpointCredentials { Username = "member", Password = "fixture-password" },
            CancellationToken.None);
        await matchpoint.OpenGridAsync(BookingKind.Doubles, new DateOnly(2026, 7, 20));
        await test(matchpoint, page);
    }

    private static MatchpointBookingRequest Request()
    {
        return new MatchpointBookingRequest
        {
            AttemptId = "fixture-attempt",
            Credentials = new MatchpointCredentials
            {
                Username = "member",
                Password = "fixture-password",
            },
            AccountMemberDisplayName = "Account Holder",
            TargetDate = new DateOnly(2026, 7, 20),
            OrderedStartTimes = [new TimeOnly(14, 0)],
            BookingKind = BookingKind.Doubles,
            DurationMinutes = 90,
            PartnerDisplayNames = ["Player One", "Player Two", "Player Three"],
            CourtOrder = [1, 2, 3, 4],
            Mode = AutomationMode.DryRun,
            TermsAuthorized = true,
        };
    }

    private static string GridHtml(FixtureBehavior behavior)
    {
        if (behavior == FixtureBehavior.ChangedMarkup)
        {
            return "<html><body><button>DOUBLES BOOKINGS</button><p>20 July 2026</p><div>Redesigned scheduler</div></body></html>";
        }

        return BookingGridHtml
            .Replace("__FORCE_NON_ZERO__", behavior == FixtureBehavior.NonZeroPrice ? "true" : "false", StringComparison.Ordinal)
            .Replace("__RACE__", behavior == FixtureBehavior.Race ? "true" : "false", StringComparison.Ordinal)
            .Replace("__CHECKOUT_RACE__", behavior == FixtureBehavior.CheckoutRace ? "true" : "false", StringComparison.Ordinal)
            .Replace("__EXTRA_ROSTER__", behavior == FixtureBehavior.ExtraRosterPlayer ? "true" : "false", StringComparison.Ordinal)
            .Replace("__DUPLICATE_ROSTER__", behavior == FixtureBehavior.DuplicateRosterPlayer ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MUTATE_PRICE__", behavior == FixtureBehavior.PriceChangesBeforeSubmit ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MUTATE_KIND__", behavior == FixtureBehavior.KindChangesBeforeSubmit ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MUTATE_ROSTER__", behavior == FixtureBehavior.RosterChangesBeforeSubmit ? "true" : "false", StringComparison.Ordinal)
            .Replace("__DELAY_BOOK__", behavior == FixtureBehavior.DelayedBook ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MIXED_MESSAGE__", behavior == FixtureBehavior.MixedSubmissionMessage ? "true" : "false", StringComparison.Ordinal)
            .Replace("__REJECT__", behavior is FixtureBehavior.Rejected or FixtureBehavior.RejectedWithBusyGridCell ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MARK_BUSY_AFTER_REJECTION__", behavior == FixtureBehavior.RejectedWithBusyGridCell ? "true" : "false", StringComparison.Ordinal)
            .Replace("__UNCERTAIN__", behavior == FixtureBehavior.Uncertain ? "true" : "false", StringComparison.Ordinal)
            .Replace(
                "__MARK_OWNERSHIP__",
                behavior is FixtureBehavior.Success or FixtureBehavior.Uncertain or FixtureBehavior.DelayedBook ? "true" : "false",
                StringComparison.Ordinal);
    }

    private const string LoginHtml = """
        <!doctype html>
        <html><body>
          <form onsubmit="event.preventDefault(); history.pushState({}, '', '/Home.aspx'); document.body.innerHTML='<h1>Member home</h1>';">
            <label>Username <input name="username" autocomplete="username"></label>
            <label>Password <input name="password" type="password"></label>
            <button type="submit">Login</button>
          </form>
        </body></html>
        """;

    private const string BookingGridHtml = """
        <!doctype html>
        <html><body>
          <button>DOUBLES BOOKINGS</button>
          <button>Mon, 20 July 2026</button>
          <table aria-label="Court booking grid">
            <thead><tr><th>Time</th><th>1 Clay</th><th>2 Clay</th><th>3 Clay</th><th>4 Clay</th></tr></thead>
            <tbody>
              <tr>
                <th>14:00</th>
                <td class="booked">14:00-15:30</td>
                <td id="court-two" onclick="openCandidate()">14:00</td>
                <td class="booked">14:00-15:00</td>
                <td class="booked">14:00-16:00</td>
              </tr>
            </tbody>
          </table>
          <div id="dialog"></div>
          <script>
            const forceNonZero = __FORCE_NON_ZERO__;
            const race = __RACE__;
            const checkoutRace = __CHECKOUT_RACE__;
            const extraRoster = __EXTRA_ROSTER__;
            const duplicateRoster = __DUPLICATE_ROSTER__;
            const mutatePrice = __MUTATE_PRICE__;
            const mutateKind = __MUTATE_KIND__;
            const mutateRoster = __MUTATE_ROSTER__;
            const delayBook = __DELAY_BOOK__;
            const mixedMessage = __MIXED_MESSAGE__;
            const reject = __REJECT__;
            const markBusyAfterRejection = __MARK_BUSY_AFTER_REJECTION__;
            const uncertain = __UNCERTAIN__;
            const markOwnership = __MARK_OWNERSHIP__;
            if (localStorage.getItem('fixture-booked') === '1') {
              document.getElementById('court-two').classList.add('your-booking');
            }
            if (localStorage.getItem('fixture-busy') === '1') {
              document.getElementById('court-two').classList.add('booked');
              document.getElementById('court-two').textContent = '14:00-15:30';
            }
            function openCandidate() {
              document.getElementById('dialog').innerHTML = race
                ? '<div role="dialog"><a>let me know if court becomes available</a><button aria-label="Close">x</button></div>'
                : '<div role="dialog"><button onclick="showCheckout([])">90min Double</button></div>';
            }
            function showCheckout(players) {
              const roster = ['Account Holder'].concat(players);
              if (extraRoster) roster.push('Unexpected Player');
              if (duplicateRoster) roster.push('Player One');
              const playerHtml = roster.map(name => '<div class="selected-player">' + name + '</div>').join('');
              const price = forceNonZero ? '62.73' : (players.length === 3 ? '0.00' : '62.73');
              const modifyAction = checkoutRace ? 'loseCheckout()' : 'showPlayers()';
              document.body.innerHTML = `
                <h1>Booking</h1><div id="booking-kind" data-booking-kind="doubles">Doubles</div>
                <div>2 Clay Tennis Clay</div><div>14:00-15:30</div><div>20 July 2026</div>
                <section><h2>PLAYERS</h2>${playerHtml}<button onclick="${modifyAction}">Modify</button></section>
                <div id="total">Price for the full court: $ ${price}</div>
                <label><input type="checkbox" onchange="mutateBeforeSubmit()"> I accept the legal conditions of service</label>
                <button id="book-button" onclick="submitBooking()" ${delayBook ? 'disabled' : ''}>Book</button>`;
              if (delayBook) setTimeout(() => document.getElementById('book-button').disabled = false, 1500);
            }
            function mutateBeforeSubmit() {
              if (mutatePrice) document.getElementById('total').textContent = 'Price for the full court: $ 62.73';
              if (mutateKind) document.getElementById('booking-kind').dataset.bookingKind = 'singles';
              if (mutateRoster) document.querySelector('.selected-player:last-of-type').textContent = 'Unexpected Player';
            }
            function loseCheckout() {
              document.body.innerHTML = '<h1>This court has just been booked and is no longer available</h1>';
            }
            function showPlayers() {
              document.body.innerHTML = `
                <h1>INDICATES THE REST OF THE RESERVE PLAYERS</h1>
                <div><label>Player One <input type="checkbox" value="Player One"></label></div>
                <div><label>Player Two <input type="checkbox" value="Player Two"></label></div>
                <div><label>Player Three <input type="checkbox" value="Player Three"></label></div>
                <button onclick="confirmPlayers()">OK</button>`;
            }
            function confirmPlayers() {
              const selected = Array.from(document.querySelectorAll('input[type=checkbox]:checked')).map(input => input.value);
              showCheckout(selected);
            }
            function submitBooking() {
              if (markOwnership) localStorage.setItem('fixture-booked', '1');
              if (markBusyAfterRejection) localStorage.setItem('fixture-busy', '1');
              document.body.innerHTML = reject
                ? '<h1>Booking failed</h1><p>Reservation was not created.</p>'
                : (mixedMessage
                    ? '<h1>Booking confirmed successfully</h1><p>Reservation email could not be sent.</p>'
                    : (uncertain ? '<h1>Thank you</h1>' : '<h1>Booking confirmed successfully</h1>'));
            }
          </script>
        </body></html>
        """;

    public enum FixtureBehavior
    {
        Success,
        NonZeroPrice,
        Race,
        CheckoutRace,
        ExtraRosterPlayer,
        DuplicateRosterPlayer,
        PriceChangesBeforeSubmit,
        KindChangesBeforeSubmit,
        RosterChangesBeforeSubmit,
        DelayedBook,
        MixedSubmissionMessage,
        Rejected,
        RejectedWithBusyGridCell,
        Uncertain,
        PositiveWithoutOwnership,
        ChangedMarkup,
    }
}
