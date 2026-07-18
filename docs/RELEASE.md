# Windows release and MSI verification

## Build an unsigned release

Run the release script on Windows 11 x64 with the pinned .NET 10 SDK installed. The build machine needs internet access for NuGet restore and the pinned Playwright Chromium download.

```powershell
pwsh ./scripts/build-windows.ps1 -Configuration Release -Version 1.0.0
```

The script:

1. restores the solution, installs the pinned Chromium build, and runs all automated tests including deterministic Playwright fixtures;
2. publishes `RockcliffeCourtBooker.exe` and `RockcliffeCourtBooker.Worker.exe` as self-contained `win-x64` applications;
3. merges their output only when duplicate files have identical SHA-256 hashes;
4. downloads Chromium with the pinned `Microsoft.Playwright` package into `ms-playwright`;
5. carries Microsoft-signed x64 Windows App Runtime framework and Singleton MSIX packages required by app notifications;
6. verifies stable executable names and that no `account.json` or `config.json` entered staging;
7. creates a per-user WiX 6 MSI; and
8. writes an adjacent `.msi.sha256` checksum.

Release outputs are placed under `artifacts/installer`. Intermediate app, worker, merged, and browser payloads remain under `artifacts` and are ignored by Git.

## Installed layout

The MSI installs to `%LocalAppData%\Programs\Rockcliffe Court Booker` and creates a Start menu shortcut for `RockcliffeCourtBooker.exe`. `RockcliffeCourtBooker.Worker.exe`, `release-manifest.json`, and the bundled `ms-playwright` browser directory use stable relative locations in the same install folder.

The worker must resolve the bundled browser directory relative to its installed executable. It must not depend on a machine-wide browser cache, an Edge installation, or a browser download at booking time.

The MSI registers the pinned Microsoft Windows App Runtime framework and Singleton packages for the current user so unpackaged, self-contained app notifications work on a clean Windows 11 account. These Microsoft-signed shared runtime packages are not removed on uninstall because other applications may also depend on them; all Rockcliffe application files and notification registrations are removed.

## Installer acceptance on Windows

Use a disposable Windows 11 x64 account or VM for installer tests. Capture verbose logs:

```powershell
msiexec.exe /i .\RockcliffeCourtBooker.Installer.msi /l*v .\install.log
```

Verify all of the following before release:

- installation is per-user and does not prompt for elevation;
- the Start menu shortcut opens the UI while the machine has no separately installed .NET runtime;
- `RockcliffeCourtBooker.Worker.exe` launches and the packaged `ms-playwright` tree contains `chrome.exe`;
- `RockcliffeCourtBooker.Worker.exe --check-notifications` exits with code 0 after installation;
- a visible diagnostic run launches that packaged Chromium with network access disabled after installation, proving that no browser download occurs;
- the scheduled task points to the installed worker and continues to work with the UI closed and Windows locked;
- installing a higher three-part MSI version upgrades in place and preserves `court-booker.db`, logs, and diagnostics;
- uninstall unregisters notification activation and deletes the scheduled task, database/WAL files, transient worker request/result JSON, `worker-*.log`, failure screenshots, and traces;
- an unrelated `.json` test file under `%LocalAppData%\RockcliffeCourtBooker` survives uninstall, proving cleanup is targeted; and
- the original external account/player JSON, stored elsewhere, is byte-for-byte unchanged.

Then perform the clean install, uninstall, upgrade, wake-from-sleep, and supervised booking sequence in the main acceptance plan. Version 1 packages are intentionally not code-signed, so Windows may display an unknown-publisher warning.
