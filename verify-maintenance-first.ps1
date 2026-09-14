param(
    [switch]$AllowGeneratedArtifacts
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Join-Path $root "src\GeneralMaintenanceManager.App"
$coreRoot = Join-Path $root "src\GeneralMaintenanceManager.Core"
$infraRoot = Join-Path $root "src\GeneralMaintenanceManager.Infrastructure"
$testsRoot = Join-Path $root "tests\GeneralMaintenanceManager.Tests"

# Fail fast with a clear source-completeness error before reading architecture files.
$requiredSourceFiles = @(
    (Join-Path $infraRoot "Data\DatabaseInitializer.cs"),
    (Join-Path $infraRoot "Data\MasterDbContext.cs"),
    (Join-Path $infraRoot "Data\AnnualDbContext.cs"),
    (Join-Path $infraRoot "Data\DatabaseContextFactory.cs"),
    (Join-Path $infraRoot "Data\DataPaths.cs")
)
$missingRequiredSourceFiles = @($requiredSourceFiles | Where-Object { -not (Test-Path $_) })
if ($missingRequiredSourceFiles.Count -ne 0) {
    throw "Critical source files are missing from the project tree: $($missingRequiredSourceFiles -join ', ')"
}

function Get-XamlKeys([string]$text) {
    @([regex]::Matches($text, 'x:Key="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
}

function Assert-Contains([string]$Text, [string]$Token, [string]$Message) {
    if ($Text -notmatch [regex]::Escape($Token)) { throw $Message }
}

function Assert-NotContains([string]$Text, [string]$Token, [string]$Message) {
    if ($Text -match [regex]::Escape($Token)) { throw $Message }
}

# 1. XAML/resource integrity.
$allXaml = @(Get-ChildItem -Path $appRoot -Recurse -Filter *.xaml -File)
if ($allXaml.Count -eq 0) { throw "No WPF XAML files were found." }
$allXamlText = New-Object System.Text.StringBuilder
$definedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($file in $allXaml) {
    $text = Get-Content -Path $file.FullName -Raw
    [xml]$null = $text
    [void]$allXamlText.AppendLine($text)
    foreach ($key in Get-XamlKeys $text) { [void]$definedKeys.Add($key) }
}
[void]$definedKeys.Add("AppFlowDirection")

$languageKeys = @{}
foreach ($lang in @("en", "ar")) {
    $path = Join-Path $appRoot "Localization\Strings.$lang.xaml"
    $keys = @(Get-XamlKeys (Get-Content -Path $path -Raw))
    if ($keys.Count -ne (@($keys | Sort-Object -Unique)).Count) { throw "Duplicate localization key found in $lang." }
    $languageKeys[$lang] = @($keys | Sort-Object)
}
$languageKeyDiff = @(Compare-Object -ReferenceObject $languageKeys["en"] -DifferenceObject $languageKeys["ar"])
if ($languageKeyDiff.Count -ne 0) { throw "English/Arabic localization key sets differ." }

$dynamicRefs = @([regex]::Matches($allXamlText.ToString(), '\{DynamicResource\s+([^}\s]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$missingDynamic = @($dynamicRefs | Where-Object { -not $definedKeys.Contains($_) })
if ($missingDynamic.Count -ne 0) { throw "Unresolved DynamicResource keys: $($missingDynamic -join ', ')" }

$staticRefs = @([regex]::Matches($allXamlText.ToString(), '\{StaticResource\s+([^},\s]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$missingStatic = @($staticRefs | Where-Object { -not $definedKeys.Contains($_) })
if ($missingStatic.Count -ne 0) { throw "Unresolved StaticResource keys: $($missingStatic -join ', ')" }

$palette = Join-Path $appRoot "Themes\MaintenancePalette.xaml"
$hexOutsidePalette = @()
foreach ($file in $allXaml | Where-Object { $_.FullName -ne $palette }) {
    $lineNumber = 0
    foreach ($line in Get-Content -Path $file.FullName) {
        $lineNumber++
        if ($line -match '#[0-9A-Fa-f]{6,8}') { $hexOutsidePalette += "$($file.FullName):$lineNumber" }
    }
}
if ($hexOutsidePalette.Count -ne 0) { throw "Hex colors outside MaintenancePalette.xaml: $($hexOutsidePalette -join ', ')" }

# 2. Hybrid database architecture: clean schemas, registered GMM forward migrations only, no legacy event architecture.
$initializerPath = Join-Path $infraRoot "Data\DatabaseInitializer.cs"
$masterContextPath = Join-Path $infraRoot "Data\MasterDbContext.cs"
$annualContextPath = Join-Path $infraRoot "Data\AnnualDbContext.cs"
$dataPathsPath = Join-Path $infraRoot "Data\DataPaths.cs"
$workOrderServicePath = Join-Path $infraRoot "Services\WorkOrderService.cs"
$sqliteMaintenancePath = Join-Path $infraRoot "Data\SqliteMaintenanceService.cs"
$productionSchemaPath = Join-Path $infraRoot "Data\ProductionSchemaPolicy.cs"
$productionMigrationPath = Join-Path $infraRoot "Data\ProductionMigrationCoordinator.cs"
$migrationJournalPath = Join-Path $infraRoot "Data\SchemaMigrationJournalRow.cs"
$initializer = Get-Content -Path $initializerPath -Raw
$masterContext = Get-Content -Path $masterContextPath -Raw
$annualContext = Get-Content -Path $annualContextPath -Raw
$dataPaths = Get-Content -Path $dataPathsPath -Raw
$workOrderService = Get-Content -Path $workOrderServicePath -Raw
$sqliteMaintenance = Get-Content -Path $sqliteMaintenancePath -Raw
$productionSchema = Get-Content -Path $productionSchemaPath -Raw
$productionMigration = Get-Content -Path $productionMigrationPath -Raw
$migrationJournal = Get-Content -Path $migrationJournalPath -Raw

foreach ($forbiddenDbSet in @(
    'DbSet<MaintenanceRecord>',
    'DbSet<MaintenanceEvent>',
    'DbSet<MachineEvent>'
)) {
    if ($masterContext -match [regex]::Escape($forbiddenDbSet)) {
        throw "MasterDbContext exposes forbidden canonical/legacy Maintenance storage: $forbiddenDbSet"
    }
}
Assert-Contains $annualContext 'DbSet<MaintenanceRecord> MaintenanceRecords' "AnnualDbContext must own canonical MaintenanceRecords."
Assert-Contains $annualContext 'DbSet<MaintenanceActivity> MaintenanceActivity' "AnnualDbContext must own MaintenanceActivity."
Assert-Contains $annualContext 'HasIndex(item => new { item.OccurredAtUtcTicks, item.MaintenanceActivityId })' "Annual MaintenanceActivity timeline index is missing."
Assert-Contains $dataPaths 'MaintenanceManager_Master.db' "Master database filename is incorrect."
Assert-Contains $dataPaths 'Maintenance_' "Annual database filename prefix is incorrect."
Assert-Contains $initializer 'It never adopts unknown files' "DatabaseInitializer unknown-file rejection contract is missing."
Assert-Contains $initializer 'MigrateRecognizedProductionDatabaseIfRequiredAsync' "DB-9 supported production migration orchestration is not wired before strict validation."
Assert-Contains $initializer 'ProductionMigrationCatalog.MasterSteps' "DB-9 master startup migration catalog is not wired."
Assert-Contains $initializer 'ProductionMigrationCatalog.AnnualSteps' "DB-9 annual startup migration catalog is not wired."
Assert-Contains $initializer 'TryReadPendingMigrationBackupPathAsync' "DB-9 startup does not reuse the journal-pinned pre-upgrade backup when resuming a migration."
Assert-Contains $initializer 'legacy/canonical Maintenance tables are not allowed in the master database' "Master schema validation does not reject duplicate Maintenance storage."
Assert-Contains $initializer 'unsupported legacy annual tables were detected' "Annual schema validation does not reject legacy annual tables."
Assert-Contains $initializer 'TR_MaintenanceRecords_ReferenceFormat_Insert' "Permanent annual MNT format trigger is missing."
Assert-Contains $initializer 'TR_MaintenanceRecords_ReferenceYear_Update' "Annual partition immutability trigger is missing."
Assert-Contains $workOrderService 'WorkOrderCompletionIntentStates.Reserved' "WO durable completion reservation state is missing."
Assert-Contains $workOrderService 'RecoverPendingCompletionsAsync' "WO completion recovery path is missing."
Assert-Contains $workOrderService 'EnsureWorkOrderMaintenanceAsync' "WO completion does not target canonical annual Maintenance."
Assert-Contains $masterContext 'HasIndex(item => item.MaintenancePlanId)' "PM one-open-WO database index is missing."
Assert-Contains $masterContext '\"MaintenancePlanId\" IS NOT NULL AND \"Status\" NOT IN (5, 6)' "PM one-open-WO filtered uniqueness contract is missing."

$maintenanceRecordService = Get-Content -Path (Join-Path $infraRoot "Services\MaintenanceRecordService.cs") -Raw
Assert-Contains $maintenanceRecordService 'SELECT * FROM MaintenanceRecords WHERE IsInvalid = 0' "Maintenance paging must anchor valid-row filtering as SQL equality so SQLite can consume the IsInvalid key in composite indexes."
Assert-Contains $maintenanceRecordService 'AND mr.IsInvalid = 0' "Maintenance FTS paging must anchor valid-row filtering as SQL equality before structured filters are composed."
Assert-NotContains $maintenanceRecordService 'rows.Where(item => !item.IsInvalid)' "Maintenance validity filter regressed to NOT(IsInvalid), which forces large filtered pages to sort instead of using the full composite index."
Assert-NotContains $maintenanceRecordService 'rows.Where(item => item.IsInvalid == false)' "Maintenance paging must not rely on EF boolean translation; use an explicit SQL IsInvalid = 0 anchor."
Assert-Contains $maintenanceRecordService 'Parameter("$assetId", query.AssetId.Value)' "Asset aggregate filtering must bind Guid values through Microsoft.Data.Sqlite instead of lowercase Guid strings."
Assert-NotContains $maintenanceRecordService 'Parameter("$assetId", query.AssetId.Value.ToString())' "Asset aggregate filtering regressed to case-sensitive lowercase Guid text."
Assert-Contains $maintenanceRecordService 'item.Record.Site == value' "Database redesign: annual activity Site dropdown filter must use equality."
Assert-Contains $maintenanceRecordService 'item.Record.Location == value' "Database redesign: annual activity Location dropdown filter must use equality."
Assert-Contains $maintenanceRecordService 'item.Record.MaintenanceType == value' "Database redesign: annual activity Maintenance Type dropdown filter must use equality."
Assert-Contains $annualContext 'HasIndex(item => new { item.ActivityType, item.OccurredAtUtcTicks, item.MaintenanceActivityId })' "Database redesign: annual activity-type timeline index is missing."
Assert-Contains $initializer 'IX_MaintenanceActivity_ActivityType_OccurredAtUtcTicks_MaintenanceActivityId' "Database redesign: annual activity-type timeline index is not required by schema validation."
# Database remediation DB-1 through DB-7 contracts.
Assert-Contains $initializer 'PartitionYear' "DB-1 annual partition metadata validation is missing."
Assert-Contains $annualContext 'record.HasIndex(item => new { item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence })' "DB-4 unfiltered Maintenance keyset index mapping is missing from AnnualDbContext."
Assert-Contains $annualContext 'CostMinorUnits' "DB-2 exact annual cost storage is missing."
Assert-Contains $masterContext 'CostMinorUnits' "DB-2 exact master cost storage is missing."
Assert-Contains $masterContext 'DowntimeMinutes' "DB-2 exact downtime storage is missing."
$backupService = Get-Content -Path (Join-Path $infraRoot "Services\BackupService.cs") -Raw
Assert-Contains $backupService 'RestoreSafetyPolicy' "DB-3 capacity-aware restore safety policy is missing."
if ($backupService -match 'MaxRestoreBytes\s*=\s*4L') { throw "DB-3 fixed 4 GiB restore ceiling is still present." }
Assert-Contains $initializer 'MaintenanceSearch' "DB-5 FTS5 Maintenance search infrastructure is missing."
Assert-Contains $workOrderService 'GetPageAsync' "DB-6 bounded Work Order paging is missing."
Assert-Contains $workOrderService 'GetDashboardMetricsAsync' "DB-6 SQL Work Order dashboard metrics are missing."
Assert-Contains $initializer 'MaintenanceLocationSummary' "DB-7 Location dashboard summary table is missing."
Assert-Contains $initializer 'MaintenanceTypeSummary' "DB-7 Maintenance Type dashboard summary table is missing."
Assert-Contains $initializer 'TR_MaintenanceDashboardSummary_Insert' "DB-7 dashboard summary insert trigger is missing."
Assert-Contains $initializer 'TR_MaintenanceDashboardSummary_Update' "DB-7 dashboard summary update trigger is missing."
Assert-Contains $maintenanceRecordService 'VerifyDashboardSummariesAsync' "DB-7 dashboard summary verification path is missing."
Assert-Contains $maintenanceRecordService 'RebuildDashboardSummariesAsync' "DB-7 dashboard summary rebuild path is missing."
# DB-8 SQLite maintenance/statistics policy.
Assert-Contains $sqliteMaintenance 'PRAGMA optimize;' "DB-8 explicit PRAGMA optimize policy is missing."
Assert-Contains $sqliteMaintenance 'ANALYZE;' "DB-8 explicit ANALYZE path is missing."
Assert-Contains $sqliteMaintenance 'AnalyzeAfterBulkChangeThreshold' "DB-8 bulk-change ANALYZE threshold is missing."
Assert-Contains $sqliteMaintenance 'sqlite_stat1' "DB-8 statistics inspection is missing."
if ($sqliteMaintenance -match '(?im)CommandText\s*=\s*"VACUUM|ExecuteNonQueryAsync\([^,]+,\s*"VACUUM') { throw "DB-8 automatic VACUUM execution is not allowed." }
if ($initializer -match 'PRAGMA optimize;' -or $initializer -match 'ANALYZE;') { throw "DB-8 optimizer/statistics maintenance must not run automatically during DatabaseInitializer startup." }
$excelImport = Get-Content -Path (Join-Path $infraRoot "Services\ExcelImportService.cs") -Raw
Assert-Contains $excelImport 'OptimizeAfterBulkChangeThreshold' "DB-8 bulk import threshold hook is missing."
Assert-Contains $excelImport 'OptimizeMasterAsync' "DB-8 post-import master optimization hook is missing."

# DB-9 first-production-schema and forward-only migration policy.
Assert-Contains $productionSchema 'BaselineVersion = 1' "DB-9 first production schema baseline is not v1."
Assert-Contains $productionSchema 'CurrentVersion = 1' "DB-9 current frozen production schema is not v1."
Assert-Contains $masterContext 'SchemaMigrationJournal' "DB-9 master migration journal is missing."
Assert-Contains $annualContext 'SchemaMigrationJournal' "DB-9 annual migration journal is missing."
Assert-Contains $initializer 'TR_SchemaMigrationJournal_BlockDelete' "DB-9 permanent migration-journal trigger is missing."
Assert-Contains $productionMigration 'pre-upgrade backup' "DB-9 pre-upgrade backup gate is missing."
Assert-Contains $productionMigration 'ComputeSha256Async' "DB-9 backup hash pinning is missing."
Assert-Contains $productionMigration 'ReadyToFinalize' "DB-9 restart-safe finalize state is missing."
Assert-Contains $productionMigration 'Only expose progress after the transaction' "DB-9 committed-cursor safety contract is missing."
Assert-Contains $productionMigration 'newer schema' "DB-9 newer-schema refusal is missing."
Assert-Contains $productionMigration 'ValidatePreUpgradeBackupAsync' "DB-9 migration backup identity/quick-check validation is missing."
Assert-Contains $productionMigration 'does not correspond to the current live GMM database snapshot' "DB-9 migration backup correspondence validation is missing."
Assert-Contains $productionMigration 'did not advance its progress cursor' "DB-9 migration non-progress loop protection is missing."
Assert-Contains $productionMigration 'public ProductionMigrationCoordinator() : this(10_000)' "DB-9 migration batch safety ceiling default is not hardened."
Assert-Contains $productionMigration '_maxMigrationBatchesPerStep' "DB-9 migration batch safety ceiling is not instance-configured."
Assert-Contains $productionMigration 'requireLiveSnapshotCorrespondence: resumeJournal is null' "DB-9 migration restart path does not distinguish first-run live-snapshot verification from journal-pinned resume verification."
foreach ($legacyMigrationToken in @('MedEquip','MaintenanceEvent','MachineEvent')) { if ($productionMigration -match [regex]::Escape($legacyMigrationToken)) { throw "DB-9 forward migration coordinator references legacy token: $legacyMigrationToken" } }
Assert-Contains $migrationJournal 'PreUpgradeBackupSha256' "DB-9 migration journal does not retain backup hash evidence."
Assert-Contains $migrationJournal 'PreUpgradeBackupPath' "DB-9 migration journal does not retain the original pre-upgrade backup path for restart-safe resume."
$appViewModels = @(Get-ChildItem -Path (Join-Path $appRoot "ViewModels") -Recurse -Filter *.cs -File)
foreach ($file in $appViewModels) {
    $text = Get-Content -Path $file.FullName -Raw
    if ($text -match '_workOrderService\.GetAllAsync\s*\(') { throw "DB-6 unbounded Work Order call remains in app ViewModel: $($file.FullName)" }
}

$migrationDirs = @(Get-ChildItem -Path (Join-Path $root "src") -Directory -Recurse | Where-Object { $_.Name -eq "Migrations" })
if ($migrationDirs.Count -ne 0) { throw "EF migration directories are not allowed in the clean development architecture: $($migrationDirs.FullName -join ', ')" }
$sourceFiles = @(Get-ChildItem -Path (Join-Path $root "src") -Recurse -Filter *.cs -File)
$legacyRuntimeHits = @()
foreach ($file in $sourceFiles) {
    $text = Get-Content -Path $file.FullName -Raw
    $isInitializer = [System.IO.Path]::GetFullPath($file.FullName) -eq [System.IO.Path]::GetFullPath($initializerPath)
    if ($text -match '\b(?:class|record|DbSet<)\s*(?:MaintenanceEvent|MachineEvent)\b' -or
        (-not $isInitializer -and $text -match '\b(?:MaintenanceEvents|MachineEvents|MaintenanceEventAudit)\b')) {
        $legacyRuntimeHits += $file.FullName
    }
    if ($text -match '\.Database\.Migrate(?:Async)?\s*\(') {
        throw "Runtime EF migration call found: $($file.FullName)"
    }
    if ($text -match 'ExecuteSqlRaw(?:Async)?\s*\(\s*\$"') {
        throw "Unsafe interpolated ExecuteSqlRaw usage found: $($file.FullName)"
    }
}
if ($legacyRuntimeHits.Count -ne 0) { throw "Legacy MaintenanceEvent/MachineEvent runtime architecture found: $(($legacyRuntimeHits | Sort-Object -Unique) -join ', ')" }

# Seven-digit permanent MNT contract is validated by source + tests.
Assert-Contains $maintenanceRecordService 'ReferenceSequence' "Maintenance service does not preserve reference sequence identity."
$maintenanceTests = Get-Content -Path (Join-Path $testsRoot "MaintenanceArchitectureTests.cs") -Raw
Assert-Contains $maintenanceTests '[0-9]{{7}}' "Seven-digit MNT regression test is missing."
Assert-Contains $maintenanceTests 'SpecificMntLookupDoesNotOpenUnrelatedAnnualDatabase' "Direct MNT-to-year routing regression test is missing."
$operationalTests = Get-Content -Path (Join-Path $testsRoot "OperationalIntegrityTests.cs") -Raw
Assert-Contains $operationalTests 'AnnualWriteBeforeMasterFinalizationIsRecoveredUsingReservedIdentity' "Annual-written completion recovery regression test is missing."
Assert-Contains $operationalTests 'ConcurrentPreventiveGenerationConvergesToOneOpenWorkOrderAndOnePlanAudit' "Concurrent PM generation regression test is missing."

# 2b. Phase 18 audit remediation contracts (25-finding pass).
$activityHistoryService = Get-Content -Path (Join-Path $infraRoot "Services\ActivityHistoryService.cs") -Raw
$globalHistoryService = Get-Content -Path (Join-Path $infraRoot "Services\GlobalHistoryQueryService.cs") -Raw
$diagnosticLogger = Get-Content -Path (Join-Path $infraRoot "Services\DiagnosticLogger.cs") -Raw
$assetService = Get-Content -Path (Join-Path $infraRoot "Services\AssetService.cs") -Raw
$reviewService = Get-Content -Path (Join-Path $infraRoot "Services\ReviewService.cs") -Raw
$maintenancePlanService = Get-Content -Path (Join-Path $infraRoot "Services\MaintenancePlanService.cs") -Raw
$maintenanceHistoryService = Get-Content -Path (Join-Path $infraRoot "Services\MaintenanceHistoryService.cs") -Raw
Assert-Contains $maintenanceHistoryService 'ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecords, 1);' "Maintenance history print bound must use the analyzer-compliant ThrowIfLessThan guard."
Assert-Contains $workOrderService 'private static void AddSuggestion(HashSet<string> values, string? value)' "Work Order suggestion helper must keep the concrete HashSet parameter required by warnings-as-errors CA1859."
$assetExport = Get-Content -Path (Join-Path $infraRoot "Services\AssetExportService.cs") -Raw
$reportsVm = Get-Content -Path (Join-Path $appRoot "ViewModels\Features\MainViewModel.Reports.cs") -Raw
$workOrdersVm = Get-Content -Path (Join-Path $appRoot "ViewModels\Features\MainViewModel.WorkOrders.cs") -Raw
$dashboardVm = Get-Content -Path (Join-Path $appRoot "ViewModels\Features\MainViewModel.Dashboard.cs") -Raw
$databaseArchitectureTests = Get-Content -Path (Join-Path $testsRoot "DatabaseArchitectureRedesignTests.cs") -Raw
$settingsVmAudit = Get-Content -Path (Join-Path $appRoot "ViewModels\Features\MainViewModel.Settings.cs") -Raw
$globalHistoryItemVm = Get-Content -Path (Join-Path $appRoot "ViewModels\GlobalHistoryItemViewModel.cs") -Raw
$mainVmAudit = Get-Content -Path (Join-Path $appRoot "ViewModels\MainViewModel.cs") -Raw
$exactNumeric = Get-Content -Path (Join-Path $infraRoot "Data\ExactNumericStorage.cs") -Raw

Assert-Contains $maintenanceRecordService 'GetAllMatchingAsync' "Audit fix: full single-asset Maintenance history helper is missing."
Assert-Contains $maintenanceHistoryService 'GetAllMatchingAsync' "Audit fix: full asset history no longer traverses all bounded Maintenance pages."
Assert-Contains $globalHistoryService 'GetPageAsync' "Audit remediation: bounded global history query service is missing."
if ($globalHistoryService -match 'GetAllMatchingAsync') { throw "Audit remediation regression: interactive global history calls an exhaustive helper." }
Assert-Contains $reportsVm 'BuildGlobalHistoryQuery' "Audit remediation: report filters are not pushed into the bounded query model."
Assert-Contains $reportsVm 'LoadMoreGlobalHistoryAsync' "Audit remediation: report Load More paging is missing."
Assert-Contains $activityHistoryService 'ActivityHistoryPageCursor' "Audit remediation: Activity History continuation paging is missing."
Assert-Contains $maintenanceRecordService 'GetActivityPageAsync' "Audit remediation: annual Maintenance audit paging is missing."
Assert-Contains $globalHistoryService 'MaintenanceMarkedInvalid' "Audit remediation: annual Maintenance invalidation is missing from unified activity."
Assert-Contains $activityHistoryService 'OrderByDescending(item => item.OccurredAtUtcTicks)' "Audit fix: newest-first activity record selection is missing."
Assert-Contains $activityHistoryService '.Take(500)' "Audit fix: per-record activity safety window is missing."
Assert-Contains $assetService 'live Work Order' "Audit fix: asset archive does not guard live Work Orders."
Assert-Contains $assetService 'active Preventive Maintenance plan' "Audit fix: asset archive does not guard active PM plans."
Assert-Contains $reviewService 'dependentWorkOrders' "Audit fix: duplicate merge does not remap Work Orders."
Assert-Contains $reviewService 'dependentPlans' "Audit fix: duplicate merge does not remap PM plans."
Assert-Contains $globalHistoryItemVm 'SourceWorkOrder?.AssetId' "Audit fix: Global History omits Work Order asset links."
Assert-Contains $globalHistoryItemVm 'SourceMaintenancePlan?.AssetId' "Audit fix: Global History omits PM-plan asset links."
Assert-Contains $globalHistoryItemVm 'HasAsset => AssetId.HasValue' "Audit fix: Global History Open Asset remains disabled for linked records."
Assert-Contains $workOrdersVm 'RunOperationAsync' "Audit fix: Work Order paging is not serialized by the common busy gate."

# Database architecture redesign: Work Order identity, allocator high-water, indexed bounded paging,
# and SQLite reads kept off the WPF dispatcher.
Assert-Contains $masterContext 'CK_WorkOrders_ReferenceNumberConsistency' "Database redesign: Work Order reference-number consistency constraint is missing."
Assert-Contains $masterContext 'HasIndex(item => new { item.ReferenceYear, item.ReferenceSequence }).IsUnique()' "Database redesign: Work Order year/sequence uniqueness is missing."
Assert-Contains $initializer 'TR_WorkOrders_SyncSequence_AfterInsert' "Database redesign: direct Work Order inserts do not synchronize the allocator high-water mark."
Assert-Contains $initializer 'TR_WorkOrderNumberSequence_BlockDecrease' "Database redesign: Work Order sequence can move backwards."
Assert-Contains $initializer 'TR_WorkOrderNumberSequence_BlockDelete' "Database redesign: Work Order sequence state can be deleted."
Assert-Contains $initializer 'IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: Activity History timeline index is missing."
Assert-Contains $initializer 'IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: Activity History record-type timeline index is missing."
Assert-Contains $initializer 'IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: Activity History Site timeline index is missing."
Assert-Contains $initializer 'IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: Activity History Location timeline index is missing."
Assert-Contains $initializer 'IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: Activity History ChangedBy timeline index is missing."
$siteFilterBlock = [regex]::Match($activityHistoryService, '(?s)if \(!string\.IsNullOrWhiteSpace\(query\.Site\)\)\s*\{.*?\}')
if (-not $siteFilterBlock.Success) { throw "Database redesign: could not locate the structured Site filter block." }
Assert-Contains $siteFilterBlock.Value 'item.Site == value' "Database redesign: Site dropdown filter must use indexed equality, not substring matching."
Assert-NotContains $siteFilterBlock.Value 'item.Site.Contains(value)' "Database redesign regression: Site exact filter uses a non-sargable substring predicate."

$locationFilterBlock = [regex]::Match($activityHistoryService, '(?s)if \(!string\.IsNullOrWhiteSpace\(query\.Location\)\)\s*\{.*?\}')
if (-not $locationFilterBlock.Success) { throw "Database redesign: could not locate the structured Location filter block." }
Assert-Contains $locationFilterBlock.Value 'item.Location == value' "Database redesign: Location dropdown filter must use indexed equality, not substring matching."
Assert-NotContains $locationFilterBlock.Value 'item.Location.Contains(value)' "Database redesign regression: Location exact filter uses a non-sargable substring predicate."

$changedByFilterBlock = [regex]::Match($activityHistoryService, '(?s)if \(!string\.IsNullOrWhiteSpace\(query\.ChangedBy\)\)\s*\{.*?\}')
if (-not $changedByFilterBlock.Success) { throw "Database redesign: could not locate the structured Performed-by filter block." }
Assert-Contains $changedByFilterBlock.Value 'item.ChangedBy == value' "Database redesign: Performed-by dropdown filter must use indexed equality, not substring matching."
Assert-NotContains $changedByFilterBlock.Value 'item.ChangedBy.Contains(value)' "Database redesign regression: Performed-by exact filter uses a non-sargable substring predicate."
Assert-Contains $workOrdersVm 'Task.Run(() => _workOrderService.GetPageAsync(query))' "Database redesign: bounded Pending/Work Order page reads must stay off the WPF dispatcher."
Assert-Contains $reportsVm 'Task.Run(' "Database redesign: bounded Report database reads must stay off the WPF dispatcher."
Assert-Contains $reportsVm '_globalHistoryQueryService.GetPageAsync(query, cancellationToken)' "Database redesign: Report worker-thread path must execute the bounded global history query."
Assert-Contains $dashboardVm 'Task.Run(() => _workOrderService.GetPageAsync(workOrderQuery))' "Database redesign: dashboard/operation Work Order page read must stay off the WPF dispatcher."
Assert-Contains $dashboardVm 'Task.Run(() => _workOrderService.GetDashboardMetricsAsync())' "Database redesign: Work Order dashboard metrics read must stay off the WPF dispatcher."
Assert-Contains $databaseArchitectureTests 'DirectWorkOrderInsertAdvancesAllocatorHighWaterMark' "Database redesign regression test for Work Order high-water synchronization is missing."
Assert-Contains $databaseArchitectureTests 'PreventiveGenerationUsesSameSynchronizedWorkOrderSequence' "Database redesign regression test for Preventive Work Order allocation is missing."
Assert-Contains $databaseArchitectureTests 'ManualAndPreventiveWorkOrderCreationShareAtomicAllocator' "Database redesign concurrency regression test is missing."
Assert-Contains $databaseArchitectureTests 'WorkOrderSequenceHighWaterCannotMoveBackwardOrBeDeleted' "Database redesign sequence monotonicity regression test is missing."
Assert-Contains $databaseArchitectureTests 'DatabaseRejectsMismatchedWorkOrderReferenceProjection' "Database redesign Work Order identity-constraint regression test is missing."
Assert-Contains $databaseArchitectureTests 'OperationalPagingPlansUseIndexesWithoutTemporarySorts' "Database redesign query-plan regression test is missing."
Assert-Contains $maintenancePlanService 'INSERT OR IGNORE INTO PreventiveDueOccurrence' "Audit fix: PM due processing does not use atomic occurrence markers."
Assert-Contains $maintenancePlanService 'ToArrayAsync(cancellationToken)' "Audit fix: PM due plans are not loaded with one bounded set query before occurrence writes."
if ($maintenancePlanService -match 'ActivityHistory\s*\.\s*AnyAsync[\s\S]{0,600}PreventivePlanDue') { throw "Audit fix regression: PM due history uses per-plan ActivityHistory existence queries." }
Assert-Contains $reviewService 'items.Skip(1)' "Audit fix: duplicate review detection still emits quadratic pair combinations."
Assert-Contains $excelImport 'ParseCsv' "Audit fix: direct CSV parsing path is missing."
if ($excelImport -match 'new XLWorkbook\(\).*CSV Import' -or $excelImport -match 'WorksheetRows') { throw "Audit fix regression: spreadsheet import still materializes CSV/worksheet copies unnecessarily." }
Assert-Contains $excelImport 'is not a valid number; the value was not imported' "Audit fix: malformed numeric import values are not reported."
Assert-Contains $excelImport 'is not a valid date; the value was not imported' "Audit fix: malformed date import values are not reported."
Assert-Contains $excelImport 'Asset changes were committed, but' "Audit fix: post-commit import failures can still be misreported as transaction failures."
Assert-Contains $excelImport 'BuildStableBlankImportKey' "Audit fix: blank-number imports still rely only on sheet/row identity."
Assert-Contains $productionMigration 'ValidatePreUpgradeBackupAsync' "Audit fix: migration accepts unverified backup files."
Assert-Contains $productionMigration 'did not advance its progress cursor' "Audit fix: migration can spin on a non-advancing cursor."
Assert-Contains $settingsVmAudit '_historyReadGate.WaitAsync' "Audit fix: restore does not wait for in-flight history readers."
Assert-Contains $exactNumeric 'MaximumMoneyMinorUnitsPerRecord' "Audit fix: exact integer aggregate overflow guard is missing."
Assert-Contains $maintenanceRecordService 'GetEntrySuggestionCatalogAsync' "Audit fix: Maintenance suggestions still depend on a bounded record window."
Assert-Contains $workOrderService 'GetEntrySuggestionCatalogAsync' "Audit fix: Work Order suggestions still depend on a bounded record window."
if ($maintenancePlanService -match 'string\.IsNullOrWhiteSpace\(draft\.Location\).*Location is required') { throw "Audit fix regression: PM location fallback is still blocked by validation." }
if ($mainVmAudit -match '_globalHistoryGeneralWorkOrders') { throw "Audit fix regression: abandoned _globalHistoryGeneralWorkOrders state remains." }
if (Test-Path (Join-Path $appRoot 'ViewModels\Features\MainViewModel.WorkOrders.cs.tmp')) { throw "Audit fix regression: empty Work Orders temporary source file remains packaged." }
if ($excelImport -match '\beventDate\b') { throw "Audit fix regression: unused Excel import eventDate local remains." }

Assert-Contains $maintenancePlanService 'ProcessDueActivitiesAsync' "Audit remediation: explicit Preventive due processing is missing."
if ($maintenancePlanService -match 'GetAllAsync[\s\S]{0,1600}PreventivePlanDue') { throw "Audit remediation regression: Preventive GetAllAsync appears to create due activity." }
Assert-Contains $initializer 'PreventiveDueOccurrence' "Audit remediation: Preventive due occurrence uniqueness table is missing."
Assert-Contains $initializer 'WorkOrderSearch' "Audit remediation: Work Order FTS infrastructure is missing."
Assert-Contains $workOrderService 'BuildFtsQuery' "Audit remediation: Work Order FTS query normalization is missing."
Assert-Contains $workOrderService 'GetDashboardMetricsAsync' "Audit remediation: Work Order dashboard aggregate query is missing."
Assert-Contains $reviewService 'FromSqlRaw' "Audit remediation: Review duplicate candidate narrowing is not SQL-side."
Assert-Contains $diagnosticLogger 'SystemLogsDirectory' "Audit remediation: durable SystemLogs diagnostics are missing."
Assert-Contains $assetExport 'NeutralizeSpreadsheetFormula' "Audit remediation: CSV formula hardening is missing."
Assert-Contains $mainVmAudit 'IsAssetTrackingEnabled' "Audit remediation: optional Asset Tracking startup gate is missing."
Assert-Contains $mainVmAudit 'EnsureEntrySuggestionsLoadedAsync' "Audit remediation: lazy entry suggestions are missing."

# 2c. Phase 19 maintenance-order workflow contracts.
$maintenanceVm = Get-Content -Path (Join-Path $appRoot "ViewModels\Features\MainViewModel.Maintenance.cs") -Raw
$dialogService = Get-Content -Path (Join-Path $appRoot "Services\DialogService.cs") -Raw
$dialogInterface = Get-Content -Path (Join-Path $appRoot "Services\IDialogService.cs") -Raw
$maintenanceOrderVmPath = Join-Path $appRoot "ViewModels\MaintenanceOrderDialogViewModel.cs"
$pendingOrdersViewPath = Join-Path $appRoot "Views\PendingMaintenanceOrdersView.xaml"
Assert-Contains $maintenanceVm 'ShowMaintenanceOrderDialog' "Phase 19: Add Maintenance is not routed through the maintenance-order dialog."
Assert-Contains $maintenanceVm '_workOrderService.CreateAsync' "Phase 19: Add Maintenance does not create a master Work Order."
if ($maintenanceVm -match '_maintenanceRecordService\.CreateAsync') { throw "Phase 19 regression: Add Maintenance still writes a canonical Maintenance record before completion." }
Assert-Contains $dialogInterface 'ShowMaintenanceOrderDialog' "Phase 19 maintenance-order dialog contract is missing."
Assert-Contains $dialogService 'MaintenanceOrderDialogViewModel' "Phase 19 maintenance-order dialog implementation is missing."
if ($dialogInterface -match '\bShowMaintenanceRecordDialog\b' -or $dialogService -match '\bShowMaintenanceRecordDialog\b') { throw "Phase 19 regression: direct Maintenance-create dialog API remains after the order-first workflow cutover." }
if (-not (Test-Path $maintenanceOrderVmPath)) { throw "Phase 19 maintenance-order ViewModel is missing." }
if (Test-Path $pendingOrdersViewPath) { throw "Unified Work Orders regression: obsolete Pending Maintenance Orders view still exists." }
Assert-Contains $maintenanceVm 'SetWorkOrderListMode("Open")' "Unified Work Orders: new Maintenance Orders are not routed to the Open tab."
Assert-Contains $maintenanceVm 'SetActiveView("WorkOrders")' "Unified Work Orders: new Maintenance Orders are not routed to Work Orders."
Assert-Contains $workOrdersVm 'IsClosed: IsWorkOrderListOpen ? false : null' "Unified Work Orders: Open tab is not constrained to open orders."
Assert-Contains $workOrdersVm 'WorkOrderStatus.Completed' "Unified Work Orders: Completed tab filter is missing."
Assert-Contains $workOrdersVm 'WorkOrderStatus.Cancelled' "Unified Work Orders: Cancelled tab filter is missing."
Assert-Contains $workOrdersVm 'SetWorkOrderListModeAsync' "Unified Work Orders: tab switching command is missing."
Assert-Contains $workOrderService 'GetGeneratedMaintenanceTypeName' "Phase 19 generated Maintenance type preservation is missing."
Assert-Contains $workOrderService 'var occurredAt = completionUtc.ToLocalTime();' "Phase 19 completion does not use the local calendar year for the permanent Maintenance partition."
if ($workOrderService -match 'Assign the Work Order before completing it') { throw "Phase 19 regression: a newly created Maintenance Order still requires assignment before completion." }
Assert-Contains $operationalTests 'PendingMaintenanceOrderCanCompleteWithoutAssignmentAndCreatesCanonicalMaintenance' "Phase 19 direct-completion regression test is missing."
Assert-Contains $operationalTests 'PriorYearPendingOrderRemainsSingleMasterOrderAndCompletesIntoCurrentAnnualPartition' "Phase 19 cross-year pending-order regression test is missing."

# 3. Phase 13 shell/view-model structure.
$mainWindowPath = Join-Path $appRoot "MainWindow.xaml"
$mainWindow = Get-Content -Path $mainWindowPath -Raw
if ($mainWindow -match 'ShowPendingMaintenanceOrdersCommand' -or $mainWindow -match 'PendingMaintenanceOrdersView') { throw "Unified Work Orders regression: duplicate pending-order navigation/view returned." }
Assert-Contains $mainWindow 'OpenWorkOrderCount' "Unified Work Orders: Work Orders sidebar badge is missing."
$maintenanceViewContract = Get-Content -Path (Join-Path $appRoot 'Views\MaintenanceView.xaml') -Raw
Assert-Contains $mainWindow 'x:Name="SidebarYearSelector"' "Maintenance Records UI: year selector is not hosted in the sidebar."
Assert-NotContains $maintenanceViewContract 'ItemsSource="{Binding Years}"' "Maintenance Records UI: duplicate page-local year selector returned."
Assert-Contains $maintenanceViewContract 'ScrollViewer.HorizontalScrollBarVisibility="Disabled"' "Maintenance Records UI: horizontal maintenance-grid scrollbar returned."
Assert-Contains $maintenanceViewContract 'Text="{Binding MaintenanceLocationFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"' "Maintenance Records UI: Location editable text binding is missing."
Assert-Contains $maintenanceViewContract 'Text="{Binding MaintenanceTypeFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"' "Maintenance Records UI: Maintenance Type editable text binding is missing."
Assert-Contains $maintenanceViewContract 'Text="{Binding MaintenancePerformedByFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"' "Maintenance Records UI: Performed By editable text binding is missing."
Assert-NotContains $maintenanceViewContract 'SelectedValue="{Binding MaintenanceLocationFilter' "Maintenance Records UI: conflicting Location SelectedValue binding returned."
Assert-NotContains $maintenanceViewContract 'SelectedValue="{Binding MaintenanceTypeFilter' "Maintenance Records UI: conflicting Maintenance Type SelectedValue binding returned."
Assert-NotContains $maintenanceViewContract 'SelectedValue="{Binding MaintenancePerformedByFilter' "Maintenance Records UI: conflicting Performed By SelectedValue binding returned."
$reportsView = Get-Content -Path (Join-Path $appRoot 'Views\ReportsView.xaml') -Raw
Assert-Contains $reportsView 'Padding="12,8"' 'Reports UI regression: filter card is no longer compact.'
Assert-Contains $reportsView 'MinHeight="60"' 'Reports UI regression: KPI cards are no longer compact.'
Assert-Contains $reportsView 'ScrollViewer.HorizontalScrollBarVisibility="Disabled"' 'Reports UI regression: bottom horizontal scrollbar is enabled.'
Assert-NotContains $reportsView 'ScrollViewer.HorizontalScrollBarVisibility="Auto"' 'Reports UI regression: ReportGrid still allows a horizontal scrollbar.'

foreach ($view in @('DashboardView','MaintenanceView','WorkOrdersView','PreventiveMaintenanceView','AssetsView','ReviewView','ReportsView','SettingsView')) {
    Assert-Contains $mainWindow "views:$view" "MainWindow shell is missing $view."
}
if ($mainWindow -match 'ShowHistoryCommand' -or $mainWindow -match 'IsHistoryView') { throw "Obsolete History navigation alias is still present in MainWindow." }
foreach ($viewFile in @('AssetsView.xaml','ReviewView.xaml','SettingsView.xaml')) {
    if (-not (Test-Path (Join-Path $appRoot "Views\$viewFile"))) { throw "Extracted feature view is missing: $viewFile" }
}
$featureVmDir = Join-Path $appRoot "ViewModels\Features"
foreach ($slice in @('Dashboard','Maintenance','WorkOrders','PreventiveMaintenance','Reports','Assets','Settings','Presentation')) {
    if (-not (Test-Path (Join-Path $featureVmDir "MainViewModel.$slice.cs"))) { throw "Feature-focused ViewModel slice is missing: $slice" }
}
$presentationVm = Get-Content -Path (Join-Path $featureVmDir "MainViewModel.Presentation.cs") -Raw
Assert-Contains $presentationVm 'if (MaintenanceLocationFilter.Trim().Length == 0)' "Maintenance Records UI: active Location filter can still lose its suggestion ItemsSource/text during reload."
Assert-Contains $presentationVm 'if (MaintenanceTypeFilter.Trim().Length == 0)' "Maintenance Records UI: active Maintenance Type filter can still lose its suggestion ItemsSource/text during reload."
Assert-Contains $presentationVm 'if (MaintenancePerformedByFilter.Trim().Length == 0)' "Maintenance Records UI: active Performed By filter can still lose its suggestion ItemsSource/text during reload."
$mainVm = Get-Content -Path (Join-Path $appRoot "ViewModels\MainViewModel.cs") -Raw
Assert-Contains $mainVm 'private DateTime? _maintenanceFromDate = DateTime.Today;' "Maintenance Records UI: From Date no longer defaults to today."
Assert-Contains $mainVm 'private DateTime? _maintenanceToDate = DateTime.Today;' "Maintenance Records UI: To Date no longer defaults to today."
if ($mainVm -notmatch 'public sealed partial class MainViewModel') { throw "MainViewModel is not split into feature-focused partials." }
if ($mainVm -match 'ShowHistoryCommand' -or $mainVm -match 'IsHistoryView') { throw "Obsolete History navigation alias is still present in MainViewModel." }
$settingsVm = Get-Content -Path (Join-Path $featureVmDir "MainViewModel.Settings.cs") -Raw
foreach ($restoreToken in @('LoadLanguageCode()', 'LoadWorkOrdersEnabled()', 'LoadPreventiveMaintenanceEnabled()', 'LoadAssetTrackingEnabled()', 'LoadCustomMaintenanceTypes()')) {
    Assert-Contains $settingsVm $restoreToken "Settings restore does not refresh '$restoreToken' immediately."
}

# 4. Phase 14 test architecture and optional scale harness.
foreach ($testFile in @(
    'MaintenanceArchitectureTests.cs',
    'OperationalIntegrityTests.cs',
    'ActivityHistoryIntegrityTests.cs',
    'SettingsIntegrityTests.cs',
    'SchemaAndLocalizationIntegrityTests.cs',
    'DatabaseHardeningCompletionTests.cs',
    'MedEquipArchitectureAdoptionTests.cs',
    'RemediationRegressionTests.cs',
    'MaintenanceRecordsUiTests.cs'
)) {
    if (-not (Test-Path (Join-Path $testsRoot $testFile))) { throw "Required current-architecture test file is missing: $testFile" }
}
$oldPhaseTests = @(Get-ChildItem -Path $testsRoot -File -Filter '*Phase*Tests.cs')
if ($oldPhaseTests.Count -ne 0) { throw "Phase-numbered legacy test files remain: $($oldPhaseTests.Name -join ', ')" }
if (-not (Test-Path (Join-Path $root 'tools\GeneralMaintenanceManager.ScaleHarness\GeneralMaintenanceManager.ScaleHarness.csproj'))) { throw "Optional scale harness project is missing." }
if (-not (Test-Path (Join-Path $root 'run-scale-harness.ps1'))) { throw "Scale harness runner is missing." }
if (-not (Test-Path (Join-Path $root 'run-million-stress-test.ps1'))) { throw "Dedicated million-record stress runner is missing." }
if (-not (Test-Path (Join-Path $root 'generate-multiyear-stress-databases.ps1'))) { throw "DB-10 multi-year stress fixture runner is missing." }
if (-not (Test-Path (Join-Path $root 'certify-database-remediation.ps1'))) { throw "DB-10 database remediation certification runner is missing." }
foreach ($tool in @('stress-test.ps1','generate-live-test-database.ps1','benchmark-live-test-database.ps1','install-live-test-database.ps1')) {
    if (-not (Test-Path (Join-Path $root $tool))) { throw "MedEquip Architecture Adoption tool is missing: $tool" }
}
$scaleHarness = Get-Content -Path (Join-Path $root 'tools\GeneralMaintenanceManager.ScaleHarness\Program.cs') -Raw
$solutionText = Get-Content -Path (Join-Path $root 'GeneralMaintenanceManager.slnx') -Raw
Assert-Contains $solutionText 'tools/GeneralMaintenanceManager.ScaleHarness/GeneralMaintenanceManager.ScaleHarness.csproj' "Scale harness project is not part of the normal solution build gate."
$sequenceHelperMatch = [regex]::Match($scaleHarness, '(?s)private static async Task SynchronizeMaintenanceSequenceAsync\(.*?(?=private static async Task CheckpointMasterAsync)')
if (-not $sequenceHelperMatch.Success) { throw "Could not locate SynchronizeMaintenanceSequenceAsync for scale-harness contract verification." }
Assert-NotContains $sequenceHelperMatch.Value 'options.' "Maintenance-sequence synchronization illegally captures scale-harness options."
Assert-NotContains $sequenceHelperMatch.Value 'paths' "Maintenance-sequence synchronization illegally captures DataPaths."
Assert-Contains $scaleHarness '=== Generating master Work Orders' "Multi-year fixture no longer generates requested master Work Orders."
Assert-Contains $scaleHarness 'NormalizeLegacySyntheticAssetGuidStorageAsync' "Scale harness cannot repair pre-buildfix13 lowercase synthetic AssetId values in an existing project-local corpus."
Assert-Contains $scaleHarness 'CreateDeterministicAssetGuid((sequence % 100) + 1) : (object)DBNull.Value' "Scale harness must bind synthetic AssetId values as Guid objects rather than lowercase strings."
Assert-NotContains $scaleHarness 'CreateDeterministicAssetGuid((sequence % 100) + 1).ToString()' "Scale harness synthetic AssetId seeding regressed to case-sensitive lowercase Guid text."
$millionStressRunner = Get-Content -Path (Join-Path $root 'run-million-stress-test.ps1') -Raw
$multiYearStressRunner = Get-Content -Path (Join-Path $root 'generate-multiyear-stress-databases.ps1') -Raw
$dbCertificationRunner = Get-Content -Path (Join-Path $root 'certify-database-remediation.ps1') -Raw
$liveStressGenerator = Get-Content -Path (Join-Path $root 'generate-live-test-database.ps1') -Raw
$benchmarkLiveStress = Get-Content -Path (Join-Path $root 'benchmark-live-test-database.ps1') -Raw
Assert-Contains $multiYearStressRunner '[long]$TotalRecords = 1000000' "Multi-year fixture runner must default to 1,000,000 Maintenance rows total."
Assert-Contains $multiYearStressRunner '--total-records=$TotalRecords' "Multi-year fixture runner does not pass the total-record contract to the ScaleHarness."
Assert-NotContains $multiYearStressRunner '[long]$RecordsPerYear = 1000000' "Multi-year fixture runner regressed to 1,000,000 rows per year."
Assert-Contains $dbCertificationRunner '[long]$TotalRecords = 1000000' "DB-10 certification must use 1,000,000 Maintenance rows total."
Assert-Contains $dbCertificationRunner 'six-year corpus' "DB-10 certification no longer enforces the six-year production corpus."
Assert-NotContains $dbCertificationRunner '7,000,000' "DB-10 certification still contains the obsolete 7,000,000-row requirement."
Assert-Contains $scaleHarness 'ReportRowProgress' "Stress harness live row-progress reporting is missing."
Assert-Contains $scaleHarness 'PHASE {phase}/{totalPhases}' "Stress harness phase banners are missing."
Assert-Contains $scaleHarness 'rows/s' "Stress harness throughput reporting is missing."
Assert-Contains $scaleHarness 'ETA' "Stress harness ETA reporting is missing."
Assert-Contains $millionStressRunner 'Write-Host $line' "Million-record wrapper no longer streams native harness output live."
Assert-Contains $millionStressRunner '$reportWriter.WriteLine($line)' "Million-record wrapper no longer writes streamed output to the certification report."
Assert-Contains $millionStressRunner 'release\stress-runs' "Persistent single-year stress roots must stay inside the current project."
Assert-Contains $liveStressGenerator '.\release\stress-runs\GMM-Live-Test' "Live-test stress generator default path is not project-local."
Assert-Contains $benchmarkLiveStress 'release\stress-runs' "Live-test benchmark does not enforce a project-local stress corpus."
Assert-Contains $scaleHarness 'Million stress test: PASS' "Million-record stress PASS marker is missing."
Assert-Contains $scaleHarness '50 keyset pages / 10,000 rows' "Million-record keyset sweep evidence is missing."
Assert-Contains $scaleHarness 'FTS text search x25' "DB-10 FTS percentile stress evidence is missing."
Assert-Contains $scaleHarness 'Dashboard current-month aggregate p95' "DB-7 current-month dashboard latency gate is missing from the million-record harness."
Assert-Contains $scaleHarness 'Dashboard full summary warm p95' "DB-7 full-dashboard latency gate is missing from the million-record harness."
Assert-Contains $maintenanceTests 'DashboardSummariesTrackCreateEditAndInvalidationExactly' "DB-7 summary synchronization regression test is missing."
Assert-Contains $maintenanceTests 'DashboardSummaryVerificationDetectsCorruptionAndRebuildRepairsIt' "DB-7 summary rebuild regression test is missing."
$remediationTests = Get-Content -Path (Join-Path $testsRoot "RemediationRegressionTests.cs") -Raw
Assert-Contains $remediationTests 'AssetFilteredAggregateUsesCanonicalSqliteGuidBinding' "Buildfix13+ GUID binding regression test is missing; a partial source overlay may have left the old test tree in place."
Assert-Contains $remediationTests 'AllActivityMaintenanceTimelinePlanUsesTemporalIndexWithoutTempSort' "All Activity temporal-index regression test is missing."
$hardeningTests = Get-Content -Path (Join-Path $testsRoot "DatabaseHardeningCompletionTests.cs") -Raw
Assert-Contains $hardeningTests 'ProductionBaselineIsVersionOneAndIncludesMigrationJournal' "DB-9 production baseline regression test is missing."
Assert-Contains $hardeningTests 'SyntheticV1ToV2MigrationIsBatchedJournaledAndRestartSafe' "DB-9 forward migration regression test is missing."
Assert-Contains $hardeningTests 'CommitFailureDoesNotAdvanceMigrationCursorPastCommittedWork' "DB-9 commit-boundary cursor regression test is missing."
Assert-Contains $hardeningTests 'MigrationRejectsDifferentGmmDatabaseAsCorrespondingBackup' "DB-9 backup-correspondence regression test is missing."
Assert-Contains $hardeningTests 'MigrationStopsImmediatelyWhenBatchCursorDoesNotAdvance' "DB-9 non-progress loop regression test is missing."
Assert-Contains $scaleHarness 'Maintenance-type filtered page:' "DB-10 Maintenance Type filter timing is missing."
Assert-Contains $scaleHarness 'Asset-filtered page:' "DB-10 Asset filter timing is missing."
Assert-Contains $scaleHarness 'Work Orders:' "DB-10 Work Order scale evidence is missing."
Assert-Contains $scaleHarness 'Backup creation:' "DB-10 backup timing evidence is missing."
Assert-Contains $scaleHarness 'Healthy backup restore:' "DB-10 healthy restore evidence is missing."
Assert-Contains $scaleHarness 'Tampered backup rejection:' "DB-10 tampered-backup rejection evidence is missing."
Assert-Contains $scaleHarness 'Corrupt-current + healthy-backup restore:' "DB-10 corrupt-current recovery evidence is missing."
Assert-Contains $scaleHarness 'All-years FTS sentinel search:' "DB-10 multi-year FTS evidence is missing."
Assert-Contains $scaleHarness 'Annual partition open/first-row:' "DB-10 annual partition open timing evidence is missing."
Assert-Contains $scaleHarness 'VerifyActivityCountAsync' "DB-10 activity-heavy row-count certification is missing."
Assert-Contains $scaleHarness 'RequirePlan(metrics.MaintenanceLookupPlan' "DB-10 query-plan enforcement is missing."
Assert-Contains $scaleHarness 'VerifyWorkOrderSequenceHighWaterAsync' "Database redesign: stress seeding does not certify the Work Order allocator high-water mark."
Assert-Contains $scaleHarness 'Query plan Maintenance Activity type report:' "Database redesign: annual Maintenance Activity query-plan evidence is missing from stress certification."
Assert-Contains $scaleHarness 'Query plan Pending Work Order page:' "Database redesign: Pending Work Order query-plan evidence is missing from stress certification."
Assert-Contains $scaleHarness 'Query plan Activity timeline:' "Database redesign: Activity History timeline query-plan evidence is missing."
Assert-Contains $scaleHarness 'Query plan Activity record-type report:' "Database redesign: Activity History record-type query-plan evidence is missing."
Assert-Contains $scaleHarness 'Query plan Activity Site report:' "Database redesign: Activity History Site query-plan evidence is missing."
Assert-Contains $scaleHarness 'Query plan Activity Location report:' "Database redesign: Activity History Location query-plan evidence is missing."
Assert-Contains $scaleHarness 'Query plan Activity type report:' "Database redesign: Activity History type query-plan evidence is missing."
Assert-Contains $scaleHarness 'Query plan Activity ChangedBy report:' "Database redesign: Activity History ChangedBy query-plan evidence is missing."
Assert-Contains $scaleHarness 'IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence' "Database redesign: stress plan gate is not tied to the Pending/Work Order composite index."
Assert-Contains $scaleHarness 'IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId' "Database redesign: stress plan gate is not tied to the Activity History record-type timeline index."


# 4b. MedEquip Architecture Adoption production-baseline contracts.
$appStartup = Get-Content -Path (Join-Path $appRoot 'App.xaml.cs') -Raw
$contextFactory = Get-Content -Path (Join-Path $infraRoot 'Data\DatabaseContextFactory.cs') -Raw
$sqliteInterceptor = Get-Content -Path (Join-Path $infraRoot 'Data\SqlitePerformanceInterceptor.cs') -Raw
$databaseHealth = Get-Content -Path (Join-Path $infraRoot 'Services\DatabaseHealthService.cs') -Raw
$singleInstance = Get-Content -Path (Join-Path $infraRoot 'Services\SingleInstanceGuard.cs') -Raw
$databaseGate = Get-Content -Path (Join-Path $infraRoot 'Services\DatabaseActivityGate.cs') -Raw
$startupPerformance = Get-Content -Path (Join-Path $infraRoot 'Services\StartupPerformanceLogger.cs') -Raw
$databaseMaintenance = Get-Content -Path (Join-Path $infraRoot 'Services\DatabaseMaintenanceService.cs') -Raw
$settingsView = Get-Content -Path (Join-Path $appRoot 'Views\SettingsView.xaml') -Raw
$productionSettingsVm = Get-Content -Path (Join-Path $featureVmDir 'MainViewModel.ProductionSettings.cs') -Raw
$printSettings = Get-Content -Path (Join-Path $appRoot 'Services\PrintSettings.cs') -Raw
$printService = Get-Content -Path (Join-Path $appRoot 'Services\PrintService.cs') -Raw
$humanStressRunner = Get-Content -Path (Join-Path $root 'stress-test.ps1') -Raw
$adoptionTests = Get-Content -Path (Join-Path $testsRoot 'MedEquipArchitectureAdoptionTests.cs') -Raw

Assert-Contains $contextFactory '_masterOptions' "Production baseline: master EF options are not cached."
Assert-Contains $contextFactory '_annualOptions' "Production baseline: annual EF options are not cached."
Assert-Contains $contextFactory 'SqlitePerformanceInterceptor.Instance' "Production baseline: SQLite connection interceptor is not wired."
foreach ($pragma in @('busy_timeout=5000','synchronous=NORMAL','temp_store=MEMORY','cache_size','mmap_size')) {
    Assert-Contains $sqliteInterceptor $pragma "Production baseline: SQLite interceptor is missing $pragma."
}
Assert-Contains $singleInstance 'Mutex' "Production baseline: portable-data single-instance mutex is missing."
Assert-Contains $singleInstance 'paths.DataDirectory' "Production baseline: single-instance identity is not tied to the portable Data folder."
Assert-Contains $databaseGate 'EnterReadAsync' "Production baseline: database read lease is missing."
Assert-Contains $databaseGate 'EnterExclusiveAsync' "Production baseline: database exclusive lease is missing."
Assert-Contains $startupPerformance 'startup-performance.log' "Production baseline: startup performance log is missing."
Assert-Contains $appStartup 'SingleInstanceGuard' "Production baseline: startup does not enforce single instance."
Assert-Contains $appStartup 'StartupPerformanceLogger' "Production baseline: startup timing is not wired."
Assert-Contains $appStartup '--ui-smoke-test' "Production baseline: packaged WPF smoke-test mode is missing."
Assert-Contains $appStartup 'Post-paint initial history' "Production baseline: post-first-paint historical load is missing."

$startupHealthStart = $databaseHealth.IndexOf('ValidateStartupDataAsync', [System.StringComparison]::Ordinal)
$currentHealthStart = $databaseHealth.IndexOf('ValidateCurrentDataAsync', [System.StringComparison]::Ordinal)
if ($startupHealthStart -lt 0 -or $currentHealthStart -le $startupHealthStart) { throw "Could not isolate startup health method." }
$startupHealthBody = $databaseHealth.Substring($startupHealthStart, $currentHealthStart - $startupHealthStart)
if ($startupHealthBody -match 'quick_check|ANALYZE|DiscoverExistingYears') { throw "Production baseline regression: startup health contains heavy database work." }
Assert-Contains $databaseHealth 'PRAGMA quick_check' "Production baseline: explicit full integrity check no longer performs SQLite quick_check."

Assert-Contains $initializer "content='WorkOrders'" "Production baseline: Work Order FTS is not external-content FTS5."
Assert-Contains $initializer "content_rowid='rowid'" "Production baseline: Work Order FTS content rowid contract is missing."
if ($initializer -match 'INSERT\s+INTO\s+WorkOrderSearch\s*\([^)]*\)\s*SELECT') { throw "Production baseline regression: startup performs a full Work Order FTS backfill." }
if ($initializer -match 'EnsureKnownAnnualAdditiveInfrastructureAsync') { throw "Production baseline regression: startup can rebuild a missing annual index instead of rejecting/resetting pre-release Data." }
Assert-Contains $databaseMaintenance "VALUES('rebuild')" "Production baseline: explicit Work Order FTS rebuild action is missing."
Assert-Contains $settingsVmAudit 'EnterExclusiveAsync' "Production baseline: restore is not protected by the database activity gate."
Assert-Contains $reportsVm 'EnterReadAsync' "Production baseline: report history does not use a database read lease."
Assert-Contains $mainVmAudit 'EnterReadAsync' "Production baseline: Maintenance/history read path does not use a database read lease."

foreach ($tabKey in @('GeneralSettingsTab','DataBackupSettingsTab','PrintSettingsTab','DatabaseMaintenanceTab')) {
    Assert-Contains $settingsView $tabKey "Production baseline Settings tab is missing: $tabKey"
}
Assert-Contains $settingsView 'ModernSettingsTabControlStyle' "Settings UI regression: modern Settings tab container style is missing."
Assert-Contains $settingsView 'ModernSettingsTabItemStyle' "Settings UI regression: modern segmented tab item style is missing."
Assert-Contains $settingsView 'SettingsHeaderSurface' "Settings UI regression: unified Settings tab navigation surface is missing."
Assert-Contains $settingsView 'TabHeaderText' "Settings UI regression: selected-tab foreground is no longer scoped to the header."
Assert-NotContains $settingsView '<Setter Property="Foreground" Value="{StaticResource OnPrimaryBrush}"/>' "Settings UI regression: selected TabItem foreground can leak white text into page content."
Assert-Contains $settingsView 'OptionalFeaturesMaintenanceTypesRow' "Settings UI regression: Optional Features and Maintenance Types are no longer paired."
Assert-Contains $settingsView 'GeneralLanguageCard' "Settings UI regression: Language & Layout is missing from General Settings."
Assert-Contains $settingsView 'DataBackupSettingsRoot' "Settings UI regression: Data & Backup tab content is missing."
Assert-Contains $settingsView 'InventoryPortableRow' "Settings UI regression: Inventory and Portable Data are no longer grouped."
Assert-Contains $settingsView 'BackupRestoreRow' "Settings UI regression: Backup and Restore are no longer paired."
Assert-Contains $settingsView 'DataStorageCard' "Settings UI regression: Data & Storage card is missing from Data & Backup."
Assert-Contains $settingsView '<Style.Triggers>' "Settings UI regression: Settings styles are missing Style.Triggers collections."
if ($settingsView -match '<Setter Property="Grid\.Column" Value="2"\s*/>\s*<DataTrigger Binding="\{Binding IsAssetTrackingEnabled\}" Value="False">') {
    throw "Settings UI regression: DataTrigger is a direct Style child; WPF requires it inside Style.Triggers."
}
foreach ($command in @('SavePrintSettingsCommand','ResetPrintSettingsCommand','RefreshDatabaseStatusCommand','OptimizeDatabaseCommand','AnalyzeDatabaseCommand','FullIntegrityCheckCommand','RebuildWorkOrderSearchCommand','RebuildReviewDatabaseCommand')) {
    Assert-Contains $productionSettingsVm $command "Production baseline Settings command is missing: $command"
}
Assert-Contains $printSettings 'WorkOrderPrintLayout' "Production baseline Work Order print-layout settings are missing."
Assert-Contains $printSettings 'MaintenancePrintLayout' "Production baseline Maintenance print-layout settings are missing."
Assert-Contains $printSettings 'ListReportPrintLayout' "Production baseline report print-layout settings are missing."
Assert-Contains $settingsView 'PrintBrandingComboBoxStyle' "Print UI regression: branding selector is not using the modern fixed-choice ComboBox style."
Assert-Contains $settingsView 'PrintSettingsEditor.Maintenance.ShowProblem' "Print UI regression: Single Maintenance Event problem-details visibility setting is missing."
Assert-Contains $printSettings 'bool ShowProblem' "Print settings regression: problem-details visibility is not persisted in the Maintenance print layout."
foreach ($token in @('AddDocumentHeader(','AddMetadataTable(','AddSection(','AddSignatureBlock(','TableCell')) {
    Assert-Contains $printService $token "Print layout regression: professional structured print primitive is missing: $token"
}
Assert-NotContains $printService '────────────────' "Print layout regression: Unicode text divider returned; use real borders/tables for printed design."
Assert-Contains $printService 'private static void AddMetadataTable(FlowDocument document, List<(string Label, string Value)> fields)' "Print build regression: AddMetadataTable widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'MaintenanceActivity[] activities' "Print build regression: Maintenance activity helper widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'ActivityHistoryEntry[] activities' "Print build regression: activity-history helper widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'MaintenanceRecord[] records' "Print build regression: maintenance-list helper widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'List<string> headers,' "Print build regression: print table headers widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'List<double> weights)' "Print build regression: print table weights widened back to IReadOnlyList and triggers CA1859."
Assert-Contains $printService 'private static SolidColorBrush CreateFrozenBrush' "Print build regression: CreateFrozenBrush return type widened to Brush and triggers CA1859."
Assert-Contains $printService 'if (activities.Length == 0) return;' "Print compile regression: array-backed activity helpers must use Length, not Count."
Assert-Contains $printService 'if (records.Length == 0) return;' "Print compile regression: array-backed maintenance helpers must use Length, not Count."
foreach ($arrayHelperHeader in @(
    'private void AddMaintenanceActivityTable(FlowDocument document, MaintenanceActivity[] activities)',
    'private void AddActivityHistoryTable(FlowDocument document, ActivityHistoryEntry[] activities)',
    'private void AddMaintenanceListTable(FlowDocument document, MaintenanceRecord[] records, ListReportPrintLayout layout)'
)) {
    $helperStart = $printService.IndexOf($arrayHelperHeader, [System.StringComparison]::Ordinal)
    if ($helperStart -lt 0) { throw "Print compile regression: expected array helper is missing: $arrayHelperHeader" }
    $helperWindowLength = [Math]::Min(320, $printService.Length - $helperStart)
    $helperWindow = $printService.Substring($helperStart, $helperWindowLength)
    if ($helperWindow -match '(activities|records)\.Count\s*==\s*0') {
        throw "Print compile regression: array-backed helper uses Count instead of Length: $arrayHelperHeader"
    }
}

Assert-Contains $scaleHarness '.gmm-stress-run' "Production baseline persistent stress-corpus marker is missing."
Assert-Contains $scaleHarness '--generate-only' "Production baseline persistent corpus generation mode is missing."
Assert-Contains $scaleHarness '--benchmark-only' "Production baseline reusable benchmark mode is missing."
Assert-Contains $humanStressRunner '[switch]$BenchmarkOnly' "Human-friendly stress runner is missing BenchmarkOnly mode."
Assert-Contains $humanStressRunner '[switch]$GenerateOnly' "Human-friendly stress runner is missing GenerateOnly mode."
Assert-Contains $humanStressRunner '[switch]$Reset' "Human-friendly stress runner is missing safe Reset mode."
Assert-Contains $humanStressRunner '[switch]$IncludeBackup' "Human-friendly stress runner is missing opt-in backup/restore stress."
Assert-Contains $humanStressRunner '[switch]$IncludeActivity' "Human-friendly stress runner is missing activity-heavy corpus generation."
Assert-Contains $humanStressRunner '.gmm-stress-run' "Human-friendly stress runner is missing marked-directory deletion safety."
Assert-Contains $humanStressRunner 'release\stress-runs' "Human-friendly stress runner default data path must stay under release so generated databases cannot contaminate clean source packaging."
Assert-Contains $humanStressRunner '--generate-fixture' "Human-friendly stress runner does not reuse the existing multi-year ScaleHarness generator."
Assert-Contains $humanStressRunner '--benchmark-only' "Human-friendly stress runner does not reuse the existing benchmark-only ScaleHarness mode."
Assert-Contains $humanStressRunner 'Resolve-ProjectLocalStressDataPath' "Human-friendly stress runner does not enforce project-local stress data paths."
Assert-Contains $humanStressRunner 'GMM-Full-Production\Data' "Human-friendly stress runner does not use the stable project-local production stress corpus by default."
Assert-Contains $humanStressRunner '[long]$TotalRecords = 1000000' "Human-friendly stress runner must default to exactly 1,000,000 Maintenance rows total."
Assert-Contains $humanStressRunner '[int]$FromYear = ((Get-Date).Year - 5)' "Human-friendly stress runner must default to the most recent six-year range."
Assert-Contains $humanStressRunner '--total-records=$TotalRecords' "Human-friendly stress runner does not pass the total-record contract to the ScaleHarness."
Assert-Contains $scaleHarness 'GetRecordCountForYear' "ScaleHarness is missing exact multi-year total-record distribution."
Assert-Contains $scaleHarness '--total-records=' "ScaleHarness does not accept the total-record stress contract."
Assert-Contains $scaleHarness 'Production stress test: PASS' "ScaleHarness production stress PASS marker is missing."
Assert-Contains $humanStressRunner 'Stress DataPath must stay inside the current project' "Human-friendly stress runner does not reject stress paths outside the current project."
Assert-Contains $humanStressRunner 'Assert-BenchmarkCorpusExists' "Human-friendly stress runner does not preflight benchmark-only corpus existence before invoking the harness."
Assert-NotContains $humanStressRunner 'Get-SiblingStressCorpusCandidates' "Human-friendly stress runner must not search sibling projects for stress corpora."
Assert-NotContains $humanStressRunner 'Resolve-BenchmarkDataPath' "Human-friendly stress runner still contains obsolete sibling-project benchmark path recovery."
Assert-Contains $humanStressRunner 'Multi-year copyable database fixture: PASS' "Human-friendly stress runner does not require the multi-year generation PASS marker."
Assert-Contains $humanStressRunner 'Production stress test: PASS' "Human-friendly stress runner does not require the production benchmark PASS marker."
Assert-Contains $liveStressGenerator '-GenerateOnly' "Live-test database generator does not use persistent corpus generation mode."
Assert-Contains $benchmarkLiveStress '-BenchmarkOnly' "Live-test benchmark wrapper does not use corpus reuse mode."
Assert-Contains $adoptionTests 'StartupHealthDoesNotOpenArchivedAnnualDatabase' "O(1) startup archived-year regression test is missing."
Assert-Contains $adoptionTests 'DatabaseActivityGateExclusiveLeaseWaitsForActiveReader' "Database activity gate regression test is missing."
Assert-Contains $adoptionTests 'SingleInstanceGuardAllowsOnlyOneOwnerForPortableDataDirectory' "Single-instance regression test is missing."


# 5. Phase 17 dead-code and terminology boundary.
$allSourceText = ($sourceFiles | ForEach-Object { Get-Content -Path $_.FullName -Raw }) -join "`n"
foreach ($obsoleteSymbol in @(
    'MaintenanceEventType',
    'IYearRolloverService',
    'YearRolloverService',
    'AuditFieldChange',
    'CustomEventTypeStorage',
    'EventTypeOption',
    'PrintAssetReport',
    'PrintEventCommand',
    'SourceEvent',
    'EventDetailsDialog',
    'SearchAssetReportCommand',
    'ReportEventNumber',
    'FoundReportEvent'
)) {
    if ($allSourceText -match "\b$([regex]::Escape($obsoleteSymbol))\b") {
        throw "Obsolete Phase 17 symbol remains in runtime source: $obsoleteSymbol"
    }
}
foreach ($legacyBrand in @('MedEquip','MedicalEquipment','MedicalAsset')) {
    if ($allSourceText -match [regex]::Escape($legacyBrand)) { throw "Legacy product/database branding remains in runtime source: $legacyBrand" }
}
if (Test-Path (Join-Path $root 'SampleData\Legacy MedEquip Inventory.xlsx')) { throw "Legacy branded sample workbook remains in the source package." }

# 5b. Phase 18 final dead-code/redundancy cleanup.
$workOrderInterface = Get-Content -Path (Join-Path $coreRoot 'Abstractions\IWorkOrderService.cs') -Raw
$maintenanceInterface = Get-Content -Path (Join-Path $coreRoot 'Abstractions\IMaintenanceRecordService.cs') -Raw
$assetInterface = Get-Content -Path (Join-Path $coreRoot 'Abstractions\IAssetService.cs') -Raw
$historyInterface = Get-Content -Path (Join-Path $coreRoot 'Abstractions\IMaintenanceHistoryService.cs') -Raw
$dataLocationInterface = Get-Content -Path (Join-Path $coreRoot 'Abstractions\IDataLocationService.cs') -Raw
$appStartup = Get-Content -Path (Join-Path $appRoot 'App.xaml.cs') -Raw
foreach ($deadApi in @('GetForAssetAsync','RebuildSearchIndexAsync')) {
    if ($maintenanceInterface -match "\b$deadApi\b" -or $maintenanceRecordService -match "\b$deadApi\b") { throw "Phase 18 cleanup regression: dead Maintenance API remains: $deadApi" }
}
if ($workOrderInterface -match 'Task<IReadOnlyList<WorkOrder>>\s+GetAllAsync' -or $workOrderService -match 'Task<IReadOnlyList<WorkOrder>>\s+GetAllAsync') { throw 'Phase 18 cleanup regression: unused bounded Work Order GetAllAsync compatibility API remains.' }
if ($assetInterface -match '\bGetByAssetNumberAsync\b' -or $assetService -match '\bGetByAssetNumberAsync\b') { throw 'Phase 18 cleanup regression: unused Asset GetByAssetNumberAsync API remains.' }
if ($historyInterface -match '\bGetEventByNumberAsync\b' -or $maintenanceHistoryService -match '\bGetEventByNumberAsync\b') { throw 'Phase 18 cleanup regression: unused Maintenance history GetEventByNumberAsync adapter remains.' }
if (Test-Path (Join-Path $infraRoot 'Services\DataLocationService.cs')) { throw 'Phase 18 cleanup regression: redundant DataLocationService wrapper remains.' }
Assert-Contains $appStartup 'GetRequiredService<DataPaths>()' 'Phase 18 cleanup: IDataLocationService is not mapped to the existing DataPaths singleton.'
if ($dataLocationInterface -match '\b(?:MasterDatabasePath|YearsDirectory|ImportLogsDirectory|BackupsDirectory)\b') { throw 'Phase 18 cleanup regression: IDataLocationService still exposes unused path members.' }
foreach ($deadInfrastructureApi in @('ParseYear','IsSupportedProductionVersion','OptimizeAllAsync','InspectDatabaseAsync')) {
    if ($allSourceText -match "\b$deadInfrastructureApi\b") { throw "Phase 18 cleanup regression: unused infrastructure API remains: $deadInfrastructureApi" }
}

# 6. Source hygiene relevant to build/package gates.
# Runtime databases created inside generated build/test/output folders are not source artifacts.
# Pristine-source mode still rejects generated bin/obj/TestResults directories below, while
# -AllowGeneratedArtifacts lets normal build/package/certification workflows operate after local runs.
$generatedArtifactPathPattern = '[\\/](?:bin|obj|TestResults|release)[\\/]'
$forbiddenArtifacts = @(Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $_.FullName -notmatch $generatedArtifactPathPattern -and
    ($_.Extension -in @('.db','.sqlite','.sqlite3','.user','.suo') -or $_.Name -match '\.db-(?:wal|shm)$')
})
if ($forbiddenArtifacts.Count -ne 0) { throw "Runtime/development database artifact found in a source-owned location: $($forbiddenArtifacts.FullName -join ', ')" }
$buildDirs = @(Get-ChildItem -Path $root -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('bin','obj','TestResults') })
if (-not $AllowGeneratedArtifacts.IsPresent -and $buildDirs.Count -ne 0) {
    throw "Generated build/test directories are present in the clean source tree: $($buildDirs.FullName -join ', ')"
}

$requiredDocs = @('USER_GUIDE.md','DEVELOPER_GUIDE.md')
foreach ($doc in $requiredDocs) {
    if (-not (Test-Path (Join-Path $root $doc))) { throw "Required project document is missing: $doc" }
}
$unexpectedRootDocs = @(Get-ChildItem -Path $root -Filter *.md -File | Where-Object { $_.Name -notin $requiredDocs })
if ($unexpectedRootDocs.Count -ne 0) { throw "Unexpected root Markdown documentation: $($unexpectedRootDocs.Name -join ', ')" }

Write-Host "Maintenance-first static verification: PASS" -ForegroundColor Green
Write-Host "Hybrid schema: master operational + annual canonical Maintenance" -ForegroundColor Green
Write-Host "Registered GMM forward migrations: startup-wired; legacy mirror architecture: absent" -ForegroundColor Green
Write-Host "Obsolete runtime symbols/branding: absent" -ForegroundColor Green
Write-Host "Maintenance Order workflow: present" -ForegroundColor Green
Write-Host "EN/AR keys: $($languageKeys['en'].Count) each" -ForegroundColor Green
Write-Host "MainWindow: shell + feature views" -ForegroundColor Green
Write-Host "Current-architecture tests + DB-8/DB-9 contracts + DB-10 1M-total/six-year certification tooling: present" -ForegroundColor Green
