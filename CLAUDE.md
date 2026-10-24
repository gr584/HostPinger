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

The analyzer, `WebProjectTests` and the reference checks in `HostPinger.csproj` enforce this. When one
of them fires, fix the code rather than the check:

- **RS0030 build error.** The banned-API analyzer reads `HostPinger/BannedSymbols.txt`. Don't
  suppress the error or remove the entry. Move the read or write into Core and call that.
- **`WebProject_ReferencesNoDatabaseType` fails.** The compiled web project references a banned
  type that the analyzer let through: a factory injected and handed to a Core service's
  constructor, a type argument, a caught exception type. The fix is the same: a Core method that
  takes no context. Core's static helpers that take a context, such as `HostSummary.LoadAsync`,
  are internal, so handing them one does not compile at all.
- **`BannedSymbols_EachEntryNamesSomething` fails.** A list entry names nothing, typically
  because a type was renamed or misspelled. Correct the entry.
- **"not on its PermittedPackageReference list" at restore.** A package was added to
  `HostPinger.csproj`.
  - If the package has nothing to do with the database, add it to `PermittedPackageReference` in
    the same file.
  - If it reads or writes data, it belongs in `HostPinger.Core`. Examples are System.Data.SQLite,
    sqlite-net-pcl, Dapper and the QuickGrid EF adapter.
- **"references an assembly by its path" or "a project other than HostPinger.Core" at restore.**
  The same rule for the two other ways a library arrives. Reference it as a permitted package, or
  put the code that needs it in Core.
- **`WebProject_ReferencesOnlyTheFrameworksCoreAndItsPermittedPackages` fails.** The compiled web
  project references an assembly that is neither the shared frameworks', Core, nor a permitted
  package's, usually a data library reached through Core's own references. Same fix.

Don't add `N:` or `T:` entries for packages the web project doesn't reference. The analyzer
silently ignores names the compilation doesn't contain, and the staleness test then fails on them.
The package list is what keeps those packages out.

Known gaps, which neither check covers:

- **Deliberate workarounds.** Reflection by name, `dynamic` and P/Invoke into `e_sqlite3` get past
  both checks. Nobody writes them by accident; don't write them on purpose either.
- **The database file.** Reading, copying or streaming the file at `PingerPaths.DatabasePath`
  isn't guarded. Anything that copies or serves the file goes through `DatabaseBackup` in Core.

## Subagent effort

Workflow agents and subagents run at `high` effort at most, whatever effort the session runs at.
Set `effort` on every `agent()` call in a workflow script and on every Agent tool call. An agent
with no `effort` inherits the session's level.

- `high` for verify, judge and synthesis stages, and for agents that write code.
- `medium` for scouting, searching and other mechanical fan-out.

A prompt that asks for a different level for a particular task overrides this.
