# Windows setup

## Prerequisites

- Windows 11 x64 using the **Eastern Standard Time** Windows time zone.
- The Windows user remains signed in at booking time; the screen may be locked and the computer may sleep.
- Automatic Windows clock synchronization is enabled.
- A valid Matchpoint account and explicit authorization to automate bookings.

## Account and player file

Keep the real file outside this repository. The password is plaintext by design, so restrict access to the Windows user and do not place it in OneDrive, email, source control, or a shared folder.

Also keep it outside `%LocalAppData%\RockcliffeCourtBooker`, which is reserved for app-managed database, log, and diagnostic files. The uninstaller targets only known app-generated files there and never reads, edits, moves, or deliberately deletes the external JSON.

```json
{
  "schemaVersion": 1,
  "account": {
    "memberName": "Account Holder",
    "username": "matchpoint-login",
    "password": "plaintext-password"
  },
  "players": [
    {
      "id": "player-one",
      "displayName": "Exact Matchpoint Display Name"
    }
  ]
}
```

Select this file on the Setup screen, run **Validate**, then run the visible-browser **Test login** and **Verify players** actions before creating a schedule.

Each rule records when you authorize Matchpoint's legal conditions. That authorization expires after 30 days; edit and save the rule to renew it before unattended submission resumes.

## Safety rollout

1. Validate the JSON and test login.
2. Run a read-only grid inspection.
3. Run a dry run that stops before the final **Book** button.
4. Perform one supervised live booking.
5. Enable unattended submission only after the selectors and confirmation behavior are verified.

## Install and uninstall

The unsigned version 1 MSI installs per user under `%LocalAppData%\Programs\Rockcliffe Court Booker`; administrator rights are not required. It includes the self-contained x64 .NET runtime, the UI and worker executables, the pinned Playwright Chromium build, and Microsoft-signed Windows App Runtime packages needed for toast delivery and click activation.

Uninstall from **Settings > Apps > Installed apps**. A true uninstall removes the `RockcliffeCourtBooker` scheduled task and known app-managed database, worker-log, screenshot, and trace files. A major-version upgrade preserves that data. The external account/player JSON is never packaged and is not used as an uninstall target.

See [RELEASE.md](RELEASE.md) for reproducible build and Windows acceptance instructions.
