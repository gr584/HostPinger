# HostPinger

## The web project does not touch the database

`HostPinger/`, the Blazor Server app, must not use Entity Framework, Microsoft.Data.Sqlite or
SQLitePCL. That includes going through `HostPingerDbContext` or `IDbContextFactory<HostPingerDbContext>`.
Every read and write lives in `HostPinger.Core`, behind a named method that the pages call:
`HostStore`, `DatabaseReads`, `UserSettingsStore`, `HostPingerDatabase`. A page that needs new
data or a new write gets a new method there.

Why:

- **Page freezes.** Microsoft.Data.Sqlite's async methods run synchronously, so a query in a
  component holds the circuit's dispatcher for as long as it runs. Timer-refreshed pages froze
  outright once a read outlasted the refresh period. Pages read through `DatabaseReads` via
  `PageReads`, which keeps the reads off the circuit; see `HostPinger/Components/PageReads.cs`.
- **Testing.** A write in Core is unit-tested in `HostPinger.Test` (`HostStoreTests`,
  `UserSettingsStoreTests`). The same write in a page could only be tested through a browser.

The analyzer, `WebProjectTests` and a package list in `HostPinger.csproj` enforce this. When one
of them fires, fix the code rather than the check:

- **RS0030 build error.** The banned-API analyzer reads `HostPinger/BannedSymbols.txt`. Don't
  suppress the error or remove the entry. Move the read or write into Core and call that.
- **`WebProject_ReferencesNoDatabaseType` fails.** The compiled web project references a banned
  type that the analyzer let through. Usually a context or factory is injected and handed to a
  Core helper such as `HostSummary.LoadAsync(db, …)`. The fix is the same: a Core method that
  takes no context.
- **`BannedSymbols_EachEntryNamesSomething` fails.** A list entry names nothing, typically
  because a type was renamed or misspelled. Correct the entry.
- **"not on its PermittedPackageReference list" at restore.** A package was added to
  `HostPinger.csproj`.
  - If the package has nothing to do with the database, add it to `PermittedPackageReference` in
    the same file.
  - If it reads or writes data, it belongs in `HostPinger.Core`. Examples are System.Data.SQLite,
    sqlite-net-pcl, Dapper and the QuickGrid EF adapter.

Don't add `N:` or `T:` entries for packages the web project doesn't reference. The analyzer
silently ignores names the compilation doesn't contain, and the staleness test then fails on them.
The package list is what keeps those packages out.

Known gaps, which neither check covers:

- **Deliberate workarounds.** Reflection by name, `dynamic` and P/Invoke into `e_sqlite3` get past
  both checks. Nobody writes them by accident; don't write them on purpose either.
- **The database file.** Reading, copying or streaming the file at `PingerPaths.DatabasePath`
  isn't guarded. Anything that copies or serves the file goes through `DatabaseBackup` in Core.
