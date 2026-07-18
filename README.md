# Rockcliffe Court Booker

A Windows 11 desktop application that schedules and submits authorized Rockcliffe Lawn Tennis Club clay-court bookings through Matchpoint.

The application is intentionally fail-closed: it books only courts 1–4, only the configured times and duration, only with the named players, and only when the checkout total is exactly `$0.00`.

## Status

This repository contains the Windows application, background booking worker, shared automation and persistence libraries, automated tests, and MSI packaging. Live Matchpoint selectors must be verified with a real member account using the built-in visible-browser diagnostic and dry-run modes before unattended submission is enabled.

The release pipeline builds an unsigned, per-user Windows 11 x64 MSI containing the .NET runtime, pinned Chromium, and the Windows notification dependencies. The application stores only the external JSON path in SQLite and reloads the original file before every attempt.

## Safety boundaries

- Only clay courts 1–4 and explicitly ordered start times are considered.
- Singles requires one named partner; doubles requires three.
- The exact court, date, time, duration, booking type, roster, accepted terms, and `$0.00` total are revalidated immediately before the sole **Book** click.
- An ambiguous or interrupted submission is retained as a blocking safety record; the app will not try another court or submit again for that date.
- CAPTCHA, MFA, changed markup, missing players, or any non-zero charge stop without bypass or payment.

## Build and test

The canonical release build runs on Windows 11 x64:

```powershell
pwsh ./scripts/build-windows.ps1 -Configuration Release -Version 1.0.0
```

This restores pinned dependencies, runs all unit and real-browser fixture tests, publishes the self-contained app and worker, bundles Chromium and notification prerequisites, and creates the WiX MSI plus SHA-256 checksum.

See [docs/SETUP.md](docs/SETUP.md) for Windows prerequisites and configuration, and [docs/RELEASE.md](docs/RELEASE.md) for the reproducible self-contained MSI build and installer acceptance checklist.
