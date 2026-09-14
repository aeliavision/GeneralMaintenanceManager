# General Maintenance Manager — Developer Guide

**Product:** General Maintenance Manager (GMM)  
**Source version:** 2.2.0  
**Platform:** Windows desktop, WPF, .NET 10  
**Languages:** English and Arabic

This is the permanent developer guide for the current source tree. It describes the application as it exists now; historical implementation plans, phase trackers, handovers, and temporary local-follow-up notes are intentionally not part of the clean source package.

## 1. Source-of-truth rules

- Treat the checked-out source tree as the authority for implementation details.
- Keep `Directory.Build.props` as the central version/framework policy.
- Keep the root documentation set limited to `USER_GUIDE.md` and `DEVELOPER_GUIDE.md`.
- Do not commit generated `bin`, `obj`, `TestResults`, `release`, runtime databases, backup databases, log files, or packaged ZIP files.
- Never claim a Windows build, test, WPF smoke run, scale run, or certification result unless that exact command was executed successfully on the exact source tree being released.

## 2. Repository layout

```text
GeneralMaintenanceManager.slnx
Directory.Build.props
Directory.Packages.props
global.json
.editorconfig

src/
  GeneralMaintenanceManager.App/             WPF shell, views, dialogs, view models, printing
  GeneralMaintenanceManager.Core/            entities, enums, models, service contracts
  GeneralMaintenanceManager.Infrastructure/  EF Core, SQLite, services, backup/import/export

tests/
  GeneralMaintenanceManager.Tests/           MSTest unit/integration-style database tests

tools/
  GeneralMaintenanceManager.ScaleHarness/    optional high-volume SQLite harness

USER_GUIDE.md
DEVELOPER_GUIDE.md
build.ps1
verify-maintenance-first.ps1
publish-windows.ps1
publish-win-x64.ps1
package-source.ps1
certify-production.ps1
stress-test.ps1
run-scale-harness.ps1
run-million-stress-test.ps1
generate-multiyear-stress-databases.ps1
generate-live-test-database.ps1
benchmark-live-test-database.ps1
install-live-test-database.ps1
install-multiyear-stress-fixture.ps1
certify-database-remediation.ps1
```

## 3. Toolchain

The repository pins the .NET 10 SDK family through `global.json`; the current baseline requests SDK `10.0.400` with `latestFeature` roll-forward. The shared build policy in `Directory.Build.props` uses:

- `net10.0` centrally and `net10.0-windows` for the WPF application/tests;
- C# 14;
- nullable reference types;
- implicit usings;
- latest recommended .NET analyzers;
- deterministic builds;
- warnings as errors for Release builds.

The primary packages are centrally managed in `Directory.Packages.props`. Important dependencies include Entity Framework Core SQLite, Microsoft.Data.Sqlite, CommunityToolkit.Mvvm, ClosedXML, Microsoft.Extensions.Hosting, and MSTest.

## 4. Normal local build

From Windows PowerShell in the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

`build.ps1` performs the static source gate, restore, Release build, and automated tests. To build without tests only when explicitly needed:

```powershell
.\build.ps1 -SkipTests
```

For direct SDK commands:

```powershell
dotnet restore .\GeneralMaintenanceManager.slnx
dotnet build .\GeneralMaintenanceManager.slnx -c Release --no-restore
dotnet test .\GeneralMaintenanceManager.slnx -c Release --no-build --logger "console;verbosity=normal"
```

Do not use `-SkipTests` for release certification.

## 5. Static architecture gate

Run:

```powershell
.\verify-maintenance-first.ps1
```

The script checks current architecture contracts, localization parity, source hygiene, required files, dead compatibility boundaries, database/index contracts, and the two-document root policy. It is a static gate; it is not a substitute for compiling or running the application.

## 6. Windows publishing

Publish both supported Windows architectures:

```powershell
.\publish-windows.ps1
```

Publish only x64:

```powershell
.\publish-win-x64.ps1
```

The normal publish is self-contained and creates a portable writable layout containing empty `Data`, `Data\Years`, `Data\ImportLogs`, `Data\SystemLogs`, and `Backups` directories. Fresh databases are created at first run. The publisher rejects development databases in the package.

Framework-dependent publishing is available when deliberately required:

```powershell
.\publish-windows.ps1 -FrameworkDependent
```

## 7. Clean source package

Create the source package with:

```powershell
.\package-source.ps1
```

The script first runs the static architecture gate, copies only source/repository files into a temporary staging directory, excludes generated/runtime data, and writes the source ZIP under `release\packages` unless another output directory is supplied.

## 8. Production certification

`certify-production.ps1` is the consolidated Windows release evidence runner. Use it only on a machine capable of building and running the WPF application.

Typical command:

```powershell
.\certify-production.ps1 -ManualAcceptanceConfirmed
```

Optional scale flags are intentionally separate because they can be expensive:

```powershell
.\certify-production.ps1 -ScaleRecords 100000 -RunMillionScale -ManualAcceptanceConfirmed
```

`-RunMillionScale` is retained as a compatibility flag name, but the production stress it triggers is now the bounded six-year corpus: exactly 1,000,000 Maintenance rows total across the most recent six years, not one million rows per year.

The script records evidence in `CERTIFICATION_RESULTS.txt`. Treat this file as generated release evidence, not source documentation.

## 9. Runtime architecture

The application follows a WPF + MVVM-style structure:

- `App.xaml.cs` configures dependency injection, startup initialization, startup health validation, durable Work Order completion recovery, and the main window.
- `MainViewModel` is split across feature-specific partial files for Assets, Maintenance, Work Orders, Preventive Maintenance, Reports, Dashboard, Settings, and presentation logic.
- `IDialogService` centralizes modal workflows.
- `ILocalizationService` provides English/Arabic runtime resources.
- Infrastructure services own persistence and business operations; WPF code should not issue direct SQLite commands.

Keep database/business work outside XAML code-behind. Code-behind should remain limited to WPF-specific interaction that cannot be expressed cleanly through bindings/commands.

The main sidebar is presentation-only. Human-facing labels are defined with `Sidebar*` localization keys in `Strings.en.xaml` / `Strings.ar.xaml`. The priority order is Overview → Work Orders → Maintenance Records → Preventive Plans → Assets (when enabled) → Reports & History → Settings. `Work Orders` is the single order workspace and displays `OpenWorkOrderCount`; it defaults to the Open tab and exposes Open / Completed / Cancelled / All as in-view filters. Preventive Plans hides its due badge when the count is zero. Sidebar wording, grouping, icons, badges, and tab selection must not introduce new persistence behavior.

The sidebar also owns the shared year selector used by Maintenance Records. The Maintenance Records screen must not duplicate that selector in its page header. Its filter controls use a compact 32px height. Location, Maintenance Type, and Performed By keep a single editable `Text` binding as the filter source; while a filter is active its own suggestion `ItemsSource` is deliberately kept stable so asynchronous history refresh cannot clear the visible chosen value. The Maintenance grid disables horizontal scrolling in favor of star-sized/truncated columns. Maintenance From/To date filters initialize to the local current date. These are WPF/ViewModel presentation behaviors only and must not alter database schema or query/index architecture.


## 9A. Production baseline: MedEquip Architecture Adoption

Version 2.2.0 is the GMM production-hardening baseline. GMM was still pre-release when this baseline was established, so older development databases are disposable and may be deleted/recreated instead of carrying compatibility baggage into the first production release. Once this baseline is released, future schema changes must use the registered forward migration system.

### O(1) startup contract

Normal startup is deliberately bounded with respect to historical corpus size:

1. host + preferences;
2. single-instance mutex bound to the normalized portable `Data` directory;
3. master + current-year physical schema only;
4. lightweight identity/connectivity startup health only;
5. tiny durable Work Order completion recovery queue;
6. bounded initial workspace;
7. show the MainWindow;
8. load historical detail after first paint.

Normal startup must **not** execute `PRAGMA quick_check`, `ANALYZE`, `PRAGMA optimize`, FTS rebuild/backfill, Review rebuild, archived-year scans, or full semantic integrity checks. These belong to explicit maintenance, backup verification, stress tools, or certification. `Data\SystemLogs\startup-performance.log` records stage and cumulative durations.

### SQLite runtime contract

`DatabaseContextFactory` caches immutable master/annual EF options and keeps DbContext pooling disabled so portable SQLite files remain easy to replace during backup/restore. `SqlitePerformanceInterceptor` applies every time a connection opens: foreign keys ON, 5000 ms busy timeout, `synchronous=NORMAL`, memory temp store, architecture-sized negative cache, and architecture-sized mmap. WAL remains the persistent journal mode established by database initialization.

### Concurrency and maintenance

`SingleInstanceGuard` prevents two GMM processes from writing the same portable Data directory. `DatabaseActivityGate` is wired into `DatabaseContextFactory`, so ordinary EF contexts automatically hold shared/read leases while backup restore or explicit destructive maintenance can hold an exclusive lease. The exclusive lease is re-entrant for its async flow so maintenance/restore code can safely create DbContexts while the writer gate is held. Heavy operations are exposed in the **Database & Maintenance** Settings tab: refresh status, `PRAGMA optimize`, `ANALYZE + optimize`, full integrity check, Work Order FTS rebuild, Activity History FTS rebuild, and Review detection rebuild.

### Settings tabs

Settings is split into **General**, **Data & Backup**, **Print**, and **Database & Maintenance**. `ModernSettingsTabControlStyle` provides one rounded navigation surface, while `ModernSettingsTabItemStyle` changes only the selected header foreground—not the `TabItem.Foreground`—so white selected-tab text cannot leak into page content. General contains application identity, Optional Features + Maintenance Types, and Language & Layout. Data & Backup contains Inventory Data + Portable Data Folder, Backup + Restore, and Data & Storage. Print persists portable branding plus per-document field visibility and optional default-printer auto-printing. Database & Maintenance exposes status and deliberate heavy maintenance actions; none of those heavy actions belongs on the normal startup path.

## 10. Database model

GMM uses a hybrid SQLite layout.

### Master database

`Data\MaintenanceManager_Master.db` stores cross-year operational state such as:

- Assets;
- Review queue data;
- Work Orders and Work Order audit/activity;
- Preventive Maintenance plans and audit/activity;
- global activity history;
- reference sequences;
- schema identity/migration metadata;
- durable cross-database completion intents.

### Annual canonical Maintenance databases

`Data\Years\Maintenance_YYYY.db` stores canonical completed Maintenance records for that local calendar year plus Maintenance-specific audit rows and search/index structures.

The annual partition year is an invariant. Do not move a canonical Maintenance row between annual databases merely because another timestamp changes.

## 11. Schema and migration policy

Fresh databases are created directly at the current production schema. `SchemaInfo` identifies the GMM format, schema version, and annual partition identity. Unknown/legacy/wrong-version files are rejected rather than silently adopted. The 2.2.0 MedEquip Architecture Adoption occurred before customer release, so any older development Data folder should be reset rather than migrated into this baseline.

`ProductionMigrationCoordinator` and the migration journal are the forward-migration mechanism for **all schema changes after this production baseline**. Any future migration must be resumable, verifiable, backup-protected, and backed by tests. Do not bypass schema identity checks with ad-hoc `EnsureCreated` behavior on customer databases.

## 12. Maintenance records

Completed Maintenance is canonical in annual databases. Important rules:

- Maintenance numbers use the `MNT-YYYY-...` reference scheme.
- Completion year is determined from the local calendar at completion time.
- Invalidating a record preserves the record and creates Maintenance audit activity; it does not physically delete history.
- Editing a canonical record writes a corresponding annual Maintenance activity entry.
- Queries use bounded keyset pages; exhaustive helper methods must retain cancellation and cursor-advance guards.
- FTS is used for the annual Maintenance free-text search path.
- Currency is persisted using exact minor-unit storage rather than SQLite floating-point aggregation.

## 13. Maintenance Order / Work Order lifecycle

New Maintenance starts as a master-database Work Order. Completion converts it into canonical annual Maintenance and closes the same Work Order.

Cross-database completion cannot be one SQLite transaction because master and annual databases are separate files. The implementation therefore persists a durable completion intent and includes startup recovery. Changes to this workflow must preserve idempotence: an interruption must not create duplicate canonical Maintenance or lose a completed Work Order state.

The `MaintenanceOrderDialogViewModel` has a string property named `MaintenanceType` for XAML binding. Enum references inside that class must stay explicitly type-qualified when the names would otherwise collide.

## 14. Work Order identity, allocation, and queries

The clean production baseline treats the Work Order reference as a database invariant, not merely a formatted UI string. `WorkOrderId` is the immutable GUID primary key. `(ReferenceYear, ReferenceSequence)` is unique, `WorkOrderNumber` is unique/case-insensitive for lookup, and `CK_WorkOrders_ReferenceNumberConsistency` requires the visible reference to equal `WO-YYYY-NNNNNN` for those permanent columns. Work Order references cannot be mutated after insert.

`WorkOrderNumberSequence` is the authoritative allocator high-water table. Manual Maintenance Orders and Preventive Maintenance generation both reserve a number through the same transactional `ReferenceNumberAllocator`. `TR_WorkOrders_SyncSequence_AfterInsert` advances the high-water mark for any direct insert path (including stress/import tooling), while sequence rows cannot be decreased or deleted. This prevents a direct fixture/import insert from leaving the allocator behind the real table and later causing `UNIQUE constraint failed: WorkOrders.WorkOrderNumber`. Do not repair sequence drift by scanning Work Orders during normal startup.

Normal Work Order grids use bounded keyset paging. The ordering fields and supporting indexes must stay aligned. When changing sort rules, update all of the following together:

- persisted sort projection fields;
- EF indexes in `MasterDbContext`;
- cursor comparison logic;
- `OrderBy` sequence;
- regression tests.

Avoid offset paging for large Work Order tables. The unified Work Orders workspace uses bounded keyset paging. The Open tab uses the `(IsClosed, Priority, SortDueDateOrdinal, ReportedAtUtcTicks, ReferenceSequence)` composite index; Completed and Cancelled use the existing status-led composite index; All retains the common business ordering. The schema already includes both the open-order and status-led composite indexes needed by these tab filters. The bounded SQLite page read is dispatched off the WPF UI thread, and only the finished page (maximum 200 rows) is applied to the observable collection.

Free-text search uses an **external-content** `WorkOrderSearch` FTS5 table (`content='WorkOrders'`, `content_rowid='rowid'`) maintained by insert/update/delete triggers. Normal startup never scans/backfills Work Orders. If an index rebuild is ever needed, run the explicit **Database & Maintenance → Rebuild Work Order search** action. Entry suggestion catalogs are cached and built from a bounded recent sample instead of full-table `DISTINCT` scans. Dashboard counts use index-backed scalar queries over the existing `(IsClosed, Priority, SortDueDateOrdinal, ...)` index instead of scanning the full Work Order table. Dashboard/operation refresh executes those bounded SQLite reads away from the WPF dispatcher as well.

## 15. Preventive Maintenance

Preventive Maintenance plans live in the master database. Plan operations write plan-specific audit rows and global activity history. Generation into a Work Order is expected to be idempotent for the same plan/due occurrence.

`GetAllAsync` is a pure read. Due-event creation is explicit through `ProcessDueActivitiesAsync`. The additive `PreventiveDueOccurrence` table has a composite primary key `(MaintenancePlanId, OccurrenceKey)`. Due processing reserves that occurrence and writes the activity event in one SQLite transaction, so concurrent refreshes converge without retrofitting a uniqueness rule onto historical audit rows. Existing due history is backfilled into the occurrence table during startup infrastructure validation. Keep the occurrence marker format stable unless this contract is deliberately migrated.

## 16. Assets and Review

Asset Tracking is optional in the product UI and is not loaded during normal startup while disabled. Enabling/opening the Asset workspace performs a targeted lazy load. The master database keeps the asset model available for integrations and linked workflows. The persistent identity is a GUID. Asset Number is a user-facing identifier and active Asset Number uniqueness is case-insensitive.

The Review subsystem detects possible duplicates and supports keep-both, merge, defer-for-visit, and edit actions. Candidate discovery is narrowed in SQLite so the application materializes only records that can participate in a review case rather than the complete active asset registry. Merge logic spans master state and annual history, so transactional/recovery behavior must be reviewed carefully whenever merge behavior changes.

## 17. Reports and global history

The Reports and Maintenance workspaces use `IGlobalHistoryQueryService` as a bounded read model. Filters are pushed to SQLite before materialization, the UI loads at most 100 rows per request, and **Load more** advances an opaque continuation cursor instead of draining the entire multi-year corpus. Every Report mode dispatches its bounded SQLite page read off the WPF UI thread so cold-disk/cache I/O cannot block tab switching. Printing from these workspaces uses only the currently loaded visible window and labels that scope when more rows remain.

Master `ActivityHistory` dropdown filters are exact predicates, not wildcard text searches. Site, Location, Activity Type, Changed By, and Record Type query paths use composite B-tree indexes that put equality columns first and `(OccurredAtUtcTicks, ActivityHistoryEntryId)` last to match newest-first keyset paging. Annual Maintenance activity uses exact structured record filters as well, and has a dedicated `(ActivityType, OccurredAtUtcTicks, MaintenanceActivityId)` index for activity-type timelines. Free text remains an FTS5 concern. Query-plan tests and the ScaleHarness require the global, RecordType, Site, Location, Activity Type, Changed By, and annual Maintenance Activity Type timelines to use their intended indexes without a temporary B-tree; add indexes only for demonstrated access paths to avoid write-amplification from over-indexing.

Work Order and Preventive report-year semantics are event based: the selected year filters `ActivityHistory.OccurredAtUtc`, not mutable current snapshot fields. All Activity merges bounded master activity pages with bounded annual `MaintenanceActivity` pages and normalizes Maintenance Created/Edited/MarkedInvalid events without duplicating canonical Maintenance snapshot rows. Annual `MaintenanceActivity` uses the `(OccurredAtUtcTicks, MaintenanceActivityId)` timeline index so the newest bounded page does not require a full annual scan plus temporary sort.

Direct annual `MaintenanceActivity` rows remain physically separate from master `ActivityHistory`; keep the merge deterministic and bounded when changing unified audit/report behavior.

## 18. Backup and restore

Backup/restore spans the master database and annual databases. The implementation includes manifest/hash validation, SQLite validation, archive path validation, archive expansion limits, free-space checks, staging, and recovery behavior.

Never restore archive entries by trusting ZIP paths directly. Preserve the canonical destination validation and restore safety policy. A backup/restore change requires tamper/corruption tests, not only a happy-path test.

## 19. Data location and portable deployment

The default data directory is beside the executable:

```text
<Data beside application>\Data
<Data beside application>\Data\Years
<Data beside application>\Data\ImportLogs
<Data beside application>\Data\SystemLogs
<application>\Backups
```

This is a portable deployment model. The application requires its folder to be writable. Installing the portable build under a protected directory such as `Program Files` without changing the storage policy will fail the writable-directory check. `DiagnosticLogger` writes best-effort rolling logs to `Data\SystemLogs` (1 MiB rotation target, newest 10 files retained); logging failures never replace the original application outcome. If the product later moves to a conventional installer, change data storage deliberately (for example to an application-data location) and provide a migration strategy.

## 20. Localization

English and Arabic resource dictionaries must keep identical string-key sets. New UI strings must be added to both languages in the same change. The static verifier checks key parity and XAML resource references.

Do not hard-code user-visible English strings in new views/view models when a localization resource is appropriate.

## 21. Printing

Printing is a WPF/UI concern. Keep document construction bounded and avoid unbounded database work on the print/layout path. Full asset-history printing uses a 2,000-record safety ceiling; larger histories must be narrowed through Reports before printing. A completed Work Order remains the structured source for Problem Details, Order Notes, Materials/Parts Notes, and Repair Notes when printing its permanently linked Maintenance record, so those fields are not flattened into one generic notes block. Single-record printing also loads the real audit trail when the print profile requests it.

## 22. Import/export

Asset import/export supports spreadsheet/CSV workflows. Imports must preserve immutable Asset IDs and current identity rules. CSV export neutralizes cells whose first non-whitespace character is `=`, `+`, `-`, or `@` by prefixing an apostrophe before normal CSV quoting, preventing spreadsheet formula execution while preserving the displayed value.

## 23. High-volume tooling

Optional scale tools include:

```powershell
.\stress-test.ps1
.\run-scale-harness.ps1
.\run-scale-harness.ps1 -Million
.\run-million-stress-test.ps1
.\generate-multiyear-stress-databases.ps1
```

`stress-test.ps1` is the recommended human-friendly front door. It delegates generation and benchmarking to `GeneralMaintenanceManager.ScaleHarness` so there is still one performance-test implementation. Stress data is **always project-local**: the default persistent corpus is `release\stress-runs\GMM-Full-Production\Data` inside the current source tree, and custom `-DataPath` values are accepted only when they resolve beneath this project's `release\stress-runs` directory. The runner never scans sibling projects and never redirects stress data outside the current project.

The default production corpus is intentionally bounded to **1,000,000 Maintenance rows total across the most recent six years**. The rows are distributed as evenly as possible while preserving the exact total. For a six-year range, four partitions receive 166,667 rows and two receive 166,666 rows. This replaces the old multi-million-per-year workflow; a 6M/7M Maintenance corpus is no longer required for the normal production stress gate. `-TotalRecords` controls the total across the selected year range. The newest annual partition is used for the per-year query benchmark after the full corpus shape has been verified.

A normal run with no `-BenchmarkOnly` switch creates the project-local corpus when missing, reuses it when its marker matches, and then benchmarks it. Use `-GenerateOnly` to create the reusable corpus without benchmarking, `-BenchmarkOnly` to rerun the existing project-local corpus without reseeding, `-Reset` to recreate only a directory containing `.gmm-stress-run`, `-IncludeActivity` for one MaintenanceActivity row per generated Maintenance row, and `-IncludeBackup` to opt into the expensive backup/restore phase. Every run writes `StressReport.txt` beside the selected Data directory and streams progress live to the terminal. If a project-local copied/reused corpus has lost only its `.gmm-stress-run` file, `-BenchmarkOnly` performs a read-only identity/count verification of the master database and every requested annual partition before recreating the Ready marker. If the project-local corpus does not exist, benchmark-only mode stops without creating an empty directory and prints the generation command to create it inside the current project.

Common workflows:

```powershell
# Recommended production stress run: exactly 1,000,000 Maintenance rows across six years.
.\stress-test.ps1 -TotalRecords 1000000 -IncludeActivity -IncludeBackup

# Generate once and stop.
.\stress-test.ps1 -GenerateOnly -TotalRecords 1000000

# Reuse the exact same project-local files without reseeding.
.\stress-test.ps1 -BenchmarkOnly -TotalRecords 1000000

# Safely recreate the marked corpus.
.\stress-test.ps1 -Reset -TotalRecords 1000000 -IncludeActivity -IncludeBackup

# Smaller development-only corpus across the same six-year range.
.\stress-test.ps1 -GenerateOnly -TotalRecords 300000 -DataPath ".\release\stress-runs\GMM-300K\Data"
```

### Persistent reusable stress corpus

Generate a reusable real application Data directory once:

```powershell
.\generate-live-test-database.ps1 -OutputDirectory ".\release\stress-runs\GMM-Live-Test" -WorkOrders 100000 -Reset
```

This creates a marked `.gmm-stress-run` corpus with 1,000,000 Maintenance rows and the requested Work Orders. Benchmark the same files repeatedly without reseeding:

```powershell
.\benchmark-live-test-database.ps1 -DataDirectory ".\release\stress-runs\GMM-Live-Test\Data" -WorkOrders 100000
```

Install it into a disposable published app with `install-live-test-database.ps1`. Reset protection refuses to delete an arbitrary unmarked directory. Multi-year fixture generation also supports master Work Orders through `-WorkOrders`.

The scale tooling prints live phase banners during long runs. Seed phases report row count, percentage, throughput, elapsed time, and approximate ETA at bounded batch intervals. The dedicated million-record wrapper streams output to the terminal while simultaneously appending the same lines to `release\certification\MILLION_RECORD_STRESS_RESULTS.txt`, so a long backup, integrity, or query phase does not look like a frozen terminal.

These tools are database evidence, not proof that every WPF report path is bounded. Large-data acceptance must include opening and filtering the actual Maintenance/Reports UI against representative multi-year data while observing responsiveness and process memory.

Database-performance changes follow the project database design gate: start from real access patterns, enforce data integrity with primary/foreign/unique/check constraints, use B-tree composite indexes with equality predicates first and range/order columns last, keep pagination bounded at the database, reserve FTS5 for free text, inspect SQLite `EXPLAIN QUERY PLAN`, and reject unnecessary duplicate indexes. `ANALYZE`, `PRAGMA optimize`, full integrity scans, and search rebuilds remain explicit maintenance/certification operations rather than startup work.

## 24. Test expectations

At minimum, changes that touch persistence or lifecycle behavior should include tests for:

- schema/index contracts and schema identity;
- annual routing/reference allocation;
- Maintenance create/edit/invalidate behavior;
- keyset paging and cursor stability;
- Work Order lifecycle and cross-database completion recovery;
- Preventive Maintenance generation/idempotence;
- backup/restore/tamper/corruption behavior;
- localization parity and settings persistence where relevant.

For report/global-history changes, add tests for historical year semantics, bounded retrieval, direct Maintenance audit inclusion, cancellation, and large-result behavior.

## 25. Remediated scale/operability architecture

The audit remediation in this source establishes these contracts:

1. Interactive Maintenance/Reports history is bounded and cursor-paged; it does not exhaust all matching rows before first paint.
2. Cross-year entry suggestions are lazy and capped; archived Maintenance databases are not scanned during ordinary startup.
3. Unified All Activity includes annual Maintenance Created/Edited/MarkedInvalid audit rows.
4. Work Order and Preventive historical year filtering is event based.
5. Preventive plan reads are pure; due-event processing is explicit and protected by database uniqueness.
6. Work Order free-text search uses maintained FTS5 infrastructure; dashboard counts use index-backed scalar counts; suggestion catalogs are cached, bounded, and invalidated after writes.
7. Activity History free-text search uses trigger-synchronized external-content FTS5. Existing pre-release master databases fall back to the legacy search until **Database & Maintenance → Rebuild Activity History search** marks the index complete; startup never backfills historical activity.
8. `IncludeInvalid=true` filtered Maintenance paging queries valid and invalid equality branches separately and merges bounded windows, preserving use of the existing composite indexes without adding large duplicate indexes.
7. Asset/Review loading is gated by the optional feature, and Review candidate discovery is SQL-side.
8. Best-effort rolling diagnostics are written under `Data\SystemLogs` and retained to a bounded file count.
9. CSV export neutralizes spreadsheet formula prefixes.

These source contracts still require the normal Windows build/test/WPF/scale acceptance commands before a production certification claim. Do not confuse source remediation with evidence from a local customer-scale run.

## 26. Release checklist

Before distributing a customer build:

1. Confirm the intended version in `Directory.Build.props`.
2. Run `git diff`/source review and make sure no runtime data or temporary files are present.
3. Run `.\verify-maintenance-first.ps1`.
4. Run `.\build.ps1` without `-SkipTests`.
5. Run the relevant scale/database checks for the release scope.
6. Publish supported Windows architectures with `.\publish-windows.ps1`.
7. Launch the packaged build on Windows 10 and Windows 11 target environments and exercise the critical workflows.
8. For production certification, run `.\certify-production.ps1` with the required flags and retain its generated evidence outside the clean source tree.
9. Verify the packaged source contains only `USER_GUIDE.md` and `DEVELOPER_GUIDE.md` as root Markdown documentation.
10. Hash the final packages and record the hashes with the release evidence.

## 27. Critical manual acceptance flow

A practical smoke pass should cover:

- first launch creates only master + current-year annual databases;
- create a Maintenance Order;
- find it in Work Orders → Open;
- complete it and verify one canonical MNT record appears in the correct annual database;
- edit the Maintenance record and inspect its audit details;
- create, assign, start, hold/resume, complete, and print a normal Work Order;
- create/generate/complete Preventive Maintenance where enabled;
- search and filter Maintenance/Reports;
- create a backup, restore a known-good backup, and confirm data integrity;
- switch English/Arabic and verify layout/labels;
- enable Asset Tracking and exercise add/edit/review if that feature is in release scope.

## 28. Documentation policy

The clean repository intentionally carries only two root Markdown documents:

- `USER_GUIDE.md` — end-user behavior and workflows;
- `DEVELOPER_GUIDE.md` — current architecture, build, release, and engineering guidance.

Temporary phase plans, development trackers, handovers, chat prompts, local-follow-up logs, audit working notes, and generated certification results belong outside the clean source package.

## Build artifacts and verification

`verify-maintenance-first.ps1` enforces pristine-source hygiene when run directly. Normal operational scripts (`build.ps1`, publishing, certification, and source packaging) invoke it with `-AllowGeneratedArtifacts` so previously generated `bin`, `obj`, or `TestResults` directories—and runtime databases created inside those generated folders—do not block legitimate workflows. Runtime database files in source-owned locations remain forbidden. `package-source.ps1` still excludes generated directories and database artifacts and explicitly verifies the staged source package before creating the ZIP.



### Stress fixture GUID compatibility (buildfix13)

Synthetic `AssetId` values must be bound to SQLite as actual `Guid` values, not via lowercase `Guid.ToString()`. SQLite text equality is case-sensitive, while Microsoft.Data.Sqlite uses its canonical GUID text representation for typed GUID parameters. `BenchmarkOnly` therefore performs a one-time in-place normalization of legacy lowercase synthetic `AssetId` values in the current benchmark year before Phase 7. This changes only disposable stress-fixture rows and does not reseed the corpus. New fixtures are seeded with typed GUID parameters and require no normalization.

### Buildfix14 overlay integrity guard

When applying a repair over an existing project-local stress corpus, all source files from the repair must be replaced together. The static verifier now requires the Asset GUID binding regression test in `RemediationRegressionTests.cs` in addition to the ScaleHarness normalization contracts. This intentionally catches partial overlays before a long million-row benchmark is started. The project-local `release\stress-runs` corpus is not part of the source package and must remain untouched during an overlay.


### Buildfix25 report layout polish
The Report & Audit filter card uses compact 32 px controls and reduced vertical padding. KPI cards are reduced to a 60 px minimum height. `ReportGrid` explicitly disables horizontal scrolling; its star-sized columns are expected to trim content rather than introduce a bottom scrollbar. This is an App/UI-only change and does not modify Core, Infrastructure, SQLite schema, indexes, or queries.


### Buildfix27 Settings navigation and readability repair
Settings now uses four purpose-built tabs: General, Data & Backup, Print, and Database & Maintenance. Data/import/backup/storage controls moved out of General into Data & Backup, while Language & Layout stays with everyday General configuration. The tab template scopes `OnPrimaryBrush` to `TabHeaderText` only, preventing selected-tab white text from being inherited by white page content. All pre-existing Settings command bindings are preserved. Core, Infrastructure, SQLite schema/indexes, backup/restore implementation, and persistence behavior remain unchanged. `SettingsUiTests` and `verify-maintenance-first.ps1` guard the four-tab structure and foreground-scope contract.

## Buildfix28 Settings XAML compile repair

The modern Settings layout keeps responsive card placement triggers inside `<Style.Triggers>`. Direct `Trigger`/`DataTrigger` children of a WPF `Style` are invalid and cause MC4004 during markup compilation. The source verifier and Settings UI regression contract now guard this rule.

## Professional print documents

Printing uses structured WPF FlowDocument tables/cells rather than text-character dividers. Print Settings remain the single source of truth for field visibility, branding, print date, signatures, and list/report columns. The Single Maintenance Event layout includes an explicit Problem details switch. The Center branding selector uses the modern fixed-choice ComboBox style. Do not reintroduce raw Unicode divider lines into PrintService.

### Buildfix30 - print analyzer compile fix

The professional print layout keeps warnings-as-errors clean by using concrete private-helper collection types (`List<T>` / arrays) at the exact call sites and returning `SolidColorBrush` from the frozen-brush factory. `verify-maintenance-first.ps1` guards these CA1859-sensitive signatures before MSBuild. No print behavior or database contract changed in this fix.
