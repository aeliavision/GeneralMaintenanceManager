using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

var options = Options.Parse(args);
var exitCode = await ScaleHarnessRunner.RunAsync(options).ConfigureAwait(false);
return exitCode;

internal static class ScaleHarnessRunner
{
    private static long GetTotalRecordCount(Options options)
    {
        if (options.TotalRecordCount > 0) return options.TotalRecordCount;
        if (!options.GenerateFixture && !options.VerifyExistingCorpus) return options.RecordCount;
        var yearCount = options.EndYear - options.StartYear + 1L;
        return checked(options.RecordCount * yearCount);
    }

    private static long GetRecordCountForYear(Options options, int year)
    {
        if (year < options.StartYear || year > options.EndYear)
            throw new ArgumentOutOfRangeException(nameof(year), year, "Year is outside the configured stress range.");
        if (options.TotalRecordCount <= 0) return options.RecordCount;

        var yearCount = options.EndYear - options.StartYear + 1L;
        var baseCount = options.TotalRecordCount / yearCount;
        var remainder = options.TotalRecordCount % yearCount;
        var yearIndex = year - options.StartYear;
        return baseCount + (yearIndex < remainder ? 1L : 0L);
    }

    private static long GetSentinelInterval(long recordCount) => Math.Max(1L, recordCount / 4L);

    private static long GetSentinelCount(long recordCount) => recordCount / GetSentinelInterval(recordCount);

    private static decimal SumModulo100(long recordCount)
    {
        var completeCycles = recordCount / 100L;
        var remainder = recordCount % 100L;
        return completeCycles * 4_950m + (remainder * (remainder + 1L) / 2m);
    }

    private static decimal SumEvenModulo100(long recordCount)
    {
        var completeCycles = recordCount / 100L;
        var remainder = recordCount % 100L;
        var evenTerms = remainder / 2L;
        return completeCycles * 2_450m + evenTerms * (evenTerms + 1L);
    }

    public static async Task<int> RunAsync(Options options)
    {
        var root = options.Root ?? Path.Combine(
            Path.GetTempPath(),
            options.GenerateFixture
                ? "GeneralMaintenanceManager.MultiYearFixture"
                : options.Stress ? "GeneralMaintenanceManager.MillionStress" : "GeneralMaintenanceManager.ScaleHarness",
            Guid.NewGuid().ToString("N"));
        root = Path.GetFullPath(root);
        var success = false;

        PreparePersistentStressRoot(options, root);

        if (options.VerifyExistingCorpus)
        {
            await VerifyAdoptableStressCorpusAsync(options, root).ConfigureAwait(false);
            Console.WriteLine("Existing stress corpus verification: PASS");
            return 0;
        }

        try
        {
            var paths = new DataPaths(root);
            var factory = new DatabaseContextFactory(paths);
            using var initializer = new DatabaseInitializer(factory);
            await initializer.InitializeAsync().ConfigureAwait(false);

            if (options.GenerateFixture)
            {
                Console.WriteLine($"Fixture Data root: {root}");
                Console.WriteLine($"Years: {options.StartYear}-{options.EndYear}");
                Console.WriteLine($"Total Maintenance records: {GetTotalRecordCount(options):N0}");
                Console.WriteLine("Annual distribution: " + string.Join(", ", Enumerable.Range(options.StartYear, options.EndYear - options.StartYear + 1)
                    .Select(year => $"{year}={GetRecordCountForYear(options, year):N0}")));
                Console.WriteLine($"Creation activity rows: {(options.IncludeActivity ? "one per Maintenance record" : "not generated")}");
                Console.WriteLine("Mode: MULTI-YEAR COPYABLE DATABASE FIXTURE");

                var fixtureResult = await GenerateMultiYearFixtureAsync(options, root, paths, factory, initializer).ConfigureAwait(false);
                success = true;
                return fixtureResult;
            }

            await initializer.EnsureYearAsync(options.Year).ConfigureAwait(false);

            Console.WriteLine($"Scale harness root: {root}");
            Console.WriteLine($"Year: {options.Year}");
            Console.WriteLine($"Target records: {options.RecordCount:N0}");
            Console.WriteLine($"Mode: {(options.Stress ? "PRODUCTION STRESS" : "BOUNDED SCALE")}");

            var result = options.Stress
                ? await RunMillionStressAsync(options, paths, factory, initializer).ConfigureAwait(false)
                : await RunScaleAsync(options, paths, factory, initializer).ConfigureAwait(false);

            success = true;
            if (options.KeepData && options.Stress && !options.BenchmarkOnly)
                WriteStressMarker(root, options);
            return result;
        }
        finally
        {
            if (!options.KeepData && !options.GenerateFixture && success)
            {
                TryDelete(root);
                TryDelete(Path.Combine(root, "Backups"));
                TryDelete(root + ".Backups");
            }
            else if (!success)
            {
                Console.WriteLine($"Stress/scale data preserved for diagnosis: {root}");
            }
        }
    }


    private static void PreparePersistentStressRoot(Options options, string root)
    {
        if (options.VerifyExistingCorpus)
        {
            if (string.IsNullOrWhiteSpace(options.Root))
                throw new InvalidOperationException("Existing-corpus verification requires an explicit --root path.");
            if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException($"Existing-corpus verification requires a non-empty Data root: {root}");
            return;
        }

        if (!options.GenerateOnly && !options.BenchmarkOnly && !options.Reset) return;
        if (string.IsNullOrWhiteSpace(options.Root))
            throw new InvalidOperationException("Persistent stress modes require an explicit --root path.");

        var marker = Path.Combine(root, ".gmm-stress-run");
        if (options.Reset && Directory.Exists(root))
        {
            var hasContent = Directory.EnumerateFileSystemEntries(root).Any();
            if (hasContent && !File.Exists(marker))
                throw new InvalidOperationException($"Refusing to reset unmarked directory '{root}'. Only GMM stress roots containing .gmm-stress-run may be reset.");
            Directory.Delete(root, recursive: true);
            TryDelete(root + ".Backups");
        }

        if (options.BenchmarkOnly && !File.Exists(marker))
            throw new InvalidOperationException($"Benchmark-only mode requires a GMM stress marker: {marker}");

        Directory.CreateDirectory(root);
        if (options.GenerateOnly && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(marker))
            throw new InvalidOperationException($"Generate-only target is not an empty/marked GMM stress root: {root}");
    }

    private static void WriteStressMarker(string root, Options options)
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(Path.Combine(root, ".gmm-stress-run"),
        [
            "General Maintenance Manager persistent stress corpus",
            $"GeneratedUtc={DateTimeOffset.UtcNow:O}",
            $"Year={options.Year}",
            $"FromYear={options.StartYear}",
            $"ToYear={options.EndYear}",
            $"MaintenanceRecords={options.RecordCount}",
            $"TotalMaintenanceRecords={GetTotalRecordCount(options)}",
            $"WorkOrders={options.WorkOrderCount}"
        ]);
    }

    private static async Task VerifyExistingStressCorpusAsync(Options options, DataPaths paths, DatabaseContextFactory factory)
    {
        await NormalizeLegacySyntheticAssetGuidStorageAsync(options, paths, factory).ConfigureAwait(false);

        for (var year = options.StartYear; year <= options.EndYear; year++)
        {
            await using var annual = factory.CreateAnnualDbContext(year);
            var maintenance = await annual.MaintenanceRecords.LongCountAsync().ConfigureAwait(false);
            var expected = GetRecordCountForYear(options, year);
            if (maintenance != expected)
                throw new InvalidOperationException($"Benchmark corpus {year} has {maintenance:N0} Maintenance rows; expected {expected:N0}.");
        }

        await using var master = factory.CreateMasterDbContext();
        var workOrders = await master.WorkOrders.LongCountAsync().ConfigureAwait(false);
        if (workOrders != options.WorkOrderCount)
            throw new InvalidOperationException($"Benchmark corpus has {workOrders:N0} Work Orders, expected {options.WorkOrderCount:N0}.");
    }

    private static async Task NormalizeLegacySyntheticAssetGuidStorageAsync(Options options, DataPaths paths, DatabaseContextFactory factory)
    {
        var annualPath = paths.GetYearDatabasePath(options.Year);
        if (!File.Exists(annualPath)) return;

        await using var annual = factory.CreateAnnualDbContext(options.Year);
        await annual.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = annual.Database.GetDbConnection();

        await using var detect = connection.CreateCommand();
        detect.CommandText = "SELECT COUNT(*) FROM MaintenanceRecords WHERE AssetId IS NOT NULL AND AssetId <> UPPER(AssetId);";
        var legacyRows = Convert.ToInt64(await detect.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (legacyRows == 0) return;

        Console.WriteLine($"  Legacy synthetic AssetId GUID text detected for {options.Year}: {legacyRows:N0} rows.");
        Console.WriteLine("  Normalizing GUID text in-place so the disposable stress fixture matches Microsoft.Data.Sqlite Guid storage.");
        await using var normalize = connection.CreateCommand();
        normalize.CommandText = "UPDATE MaintenanceRecords SET AssetId = UPPER(AssetId) WHERE AssetId IS NOT NULL AND AssetId <> UPPER(AssetId);";
        var changed = await normalize.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (changed != legacyRows)
            throw new InvalidOperationException($"Legacy synthetic AssetId normalization changed {changed:N0} rows; expected {legacyRows:N0}.");

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM MaintenanceRecords WHERE AssetId IS NOT NULL AND AssetId <> UPPER(AssetId);";
        var remaining = Convert.ToInt64(await verify.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (remaining != 0)
            throw new InvalidOperationException($"Legacy synthetic AssetId normalization left {remaining:N0} non-canonical GUID values.");

        Console.WriteLine($"  Legacy synthetic AssetId normalization: PASS ({changed:N0} rows repaired).");
    }

    private static async Task VerifyAdoptableStressCorpusAsync(Options options, string root)
    {
        var paths = new DataPaths(root);
        if (!File.Exists(paths.MasterDatabasePath))
            throw new InvalidOperationException($"Existing stress corpus is missing master database '{paths.MasterDatabasePath}'.");

        await VerifyDatabaseIdentityAsync(
            paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            DatabaseInitializer.MasterSchemaVersion,
            partitionYear: null).ConfigureAwait(false);
        var workOrders = await ScalarReadOnlyAsync(paths.MasterDatabasePath, "SELECT COUNT(*) FROM WorkOrders;").ConfigureAwait(false);
        if (workOrders != options.WorkOrderCount)
            throw new InvalidOperationException($"Existing stress corpus has {workOrders:N0} Work Orders; expected {options.WorkOrderCount:N0}.");

        for (var year = options.StartYear; year <= options.EndYear; year++)
        {
            var annualPath = paths.GetYearDatabasePath(year);
            if (!File.Exists(annualPath))
                throw new InvalidOperationException($"Existing stress corpus is missing annual database '{annualPath}'.");
            await VerifyDatabaseIdentityAsync(
                annualPath,
                DatabaseInitializer.AnnualFormatId,
                DatabaseInitializer.AnnualSchemaVersion,
                year).ConfigureAwait(false);
            var expected = GetRecordCountForYear(options, year);
            var maintenance = await ScalarReadOnlyAsync(annualPath, "SELECT COUNT(*) FROM MaintenanceRecords;").ConfigureAwait(false);
            if (maintenance != expected)
                throw new InvalidOperationException($"Existing stress corpus {year} database has {maintenance:N0} Maintenance rows; expected {expected:N0}.");
            if (options.IncludeActivity)
            {
                var activity = await ScalarReadOnlyAsync(annualPath, "SELECT COUNT(*) FROM MaintenanceActivity;").ConfigureAwait(false);
                if (activity != expected)
                    throw new InvalidOperationException($"Existing stress corpus {year} database has {activity:N0} Maintenance activity rows; expected {expected:N0}.");
            }
        }
    }

    private static async Task VerifyDatabaseIdentityAsync(
        string databasePath,
        string expectedFormatId,
        int expectedSchemaVersion,
        int? partitionYear)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FormatId, SchemaVersion, PartitionYear FROM SchemaInfo WHERE Id = 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            throw new InvalidOperationException($"Existing stress database '{databasePath}' has no SchemaInfo identity row.");
        var formatId = reader.GetString(0);
        var schemaVersion = reader.GetInt32(1);
        var actualPartition = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        if (!string.Equals(formatId, expectedFormatId, StringComparison.Ordinal)
            || schemaVersion != expectedSchemaVersion
            || actualPartition != partitionYear)
        {
            throw new InvalidOperationException(
                $"Existing stress database '{databasePath}' has identity {formatId}/v{schemaVersion}/partition {actualPartition?.ToString(CultureInfo.InvariantCulture) ?? "master"}; " +
                $"expected {expectedFormatId}/v{expectedSchemaVersion}/partition {partitionYear?.ToString(CultureInfo.InvariantCulture) ?? "master"}.");
        }
    }

    private static async Task<long> ScalarReadOnlyAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }


    private static async Task<int> GenerateMultiYearFixtureAsync(
        Options options,
        string root,
        DataPaths paths,
        DatabaseContextFactory factory,
        DatabaseInitializer initializer)
    {
        if (options.StartYear > options.EndYear)
            throw new InvalidOperationException("Fixture start year must be less than or equal to the end year.");

        var yearCount = options.EndYear - options.StartYear + 1;
        var totalWatch = Stopwatch.StartNew();
        var summaries = new List<string>(yearCount);
        var annualManager = new AnnualMaintenanceDatabaseManager(paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annualManager);

        for (var year = options.StartYear; year <= options.EndYear; year++)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Generating {year} ({year - options.StartYear + 1}/{yearCount}) ===");
            await initializer.EnsureYearAsync(year).ConfigureAwait(false);

            var yearRecordCount = GetRecordCountForYear(options, year);
            var yearOptions = options with { Year = year, RecordCount = yearRecordCount, Stress = true };
            var yearWatch = Stopwatch.StartNew();
            await SeedStressRowsAsync(yearOptions, factory).ConfigureAwait(false);
            if (options.IncludeActivity)
            {
                await SeedCreationActivitiesAsync(year, yearRecordCount, factory).ConfigureAwait(false);
                await VerifyActivityCountAsync(year, yearRecordCount, factory).ConfigureAwait(false);
            }
            var sqliteMaintenance = new SqliteMaintenanceService(paths);
            var maintenanceResult = await sqliteMaintenance.OptimizeYearAsync(
                year,
                changedRows: yearRecordCount + (options.IncludeActivity ? yearRecordCount : 0),
                forceAnalyze: true).ConfigureAwait(false);
            await CheckpointAsync(year, factory).ConfigureAwait(false);
            var integrity = await VerifyMillionRowIntegrityAsync(yearOptions, factory).ConfigureAwait(false);
            var summaryVerification = await maintenance.VerifyDashboardSummariesAsync(year).ConfigureAwait(false);
            if (!summaryVerification.IsConsistent)
                throw new InvalidOperationException($"{year} dashboard summaries are inconsistent: location mismatches={summaryVerification.LocationMismatchCount:N0}, type mismatches={summaryVerification.MaintenanceTypeMismatchCount:N0}.");
            await SynchronizeMaintenanceSequenceAsync(year, yearRecordCount, factory).ConfigureAwait(false);
            yearWatch.Stop();

            var path = paths.GetYearDatabasePath(year);
            var sizeMb = ToMb(new FileInfo(path).Length);
            var activityText = options.IncludeActivity ? $", activity={yearRecordCount:N0}" : string.Empty;
            var summary = $"{year}: maintenance={integrity.RowCount:N0}{activityText}, invalid={integrity.InvalidCount:N0}, dashboard-summaries=verified, stats={maintenanceResult.After.StatisticsRowCount:N0}, pages={maintenanceResult.After.PageCount:N0}, freelist={maintenanceResult.After.FreelistCount:N0}, size={sizeMb:N1} MB, time={yearWatch.Elapsed}";
            summaries.Add(summary);
            Console.WriteLine(summary);
        }

        if (options.WorkOrderCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Generating master Work Orders ({options.WorkOrderCount:N0}) ===");
            await SeedWorkOrdersAsync(options with { Year = options.EndYear }, factory).ConfigureAwait(false);
            var workOrderMaintenance = new SqliteMaintenanceService(paths);
            await workOrderMaintenance.OptimizeMasterAsync(options.WorkOrderCount, forceAnalyze: true).ConfigureAwait(false);
        }

        var masterMaintenance = new SqliteMaintenanceService(paths);
        await masterMaintenance.OptimizeMasterAsync(yearCount, forceAnalyze: true).ConfigureAwait(false);
        await CheckpointMasterAsync(factory).ConfigureAwait(false);
        await VerifyMaintenanceSequencesAsync(options, factory).ConfigureAwait(false);
        var masterCheck = await QuickCheckMasterAsync(factory).ConfigureAwait(false);
        if (!string.Equals(masterCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Generated master database quick_check returned '{masterCheck}'.");

        var yearOpenTimings = new List<double>(yearCount);
        for (var year = options.StartYear; year <= options.EndYear; year++)
        {
            var openWatch = Stopwatch.StartNew();
            var firstRow = await maintenance.GetPageAsync(new MaintenanceQuery(
                Year: year, IncludeInvalid: true, PageSize: 1)).ConfigureAwait(false);
            openWatch.Stop();
            if (firstRow.Items.Count != 1)
                throw new InvalidOperationException($"{year} existing annual partition did not return its first row.");
            yearOpenTimings.Add(openWatch.Elapsed.TotalMilliseconds);
        }
        var yearOpenAverageMs = yearOpenTimings.Average();
        var yearOpenMaxMs = yearOpenTimings.Max();

        var allYearsPageWatch = Stopwatch.StartNew();
        var allYearsPage = await maintenance.GetPageAsync(new MaintenanceQuery(IncludeInvalid: true, PageSize: 200)).ConfigureAwait(false);
        allYearsPageWatch.Stop();
        var expectedAllYearsPageCount = (int)Math.Min(200L, GetTotalRecordCount(options));
        if (allYearsPage.Items.Count != expectedAllYearsPageCount)
            throw new InvalidOperationException($"All-years first page returned {allYearsPage.Items.Count}, expected {expectedAllYearsPageCount}.");

        var allYearsSearchWatch = Stopwatch.StartNew();
        var allYearsSearch = await maintenance.GetPageAsync(new MaintenanceQuery(
            SearchText: "stress-sentinel", IncludeInvalid: true, PageSize: 200)).ConfigureAwait(false);
        allYearsSearchWatch.Stop();
        var expectedSentinelCount = (int)Math.Min(200L, Enumerable.Range(options.StartYear, yearCount).Sum(year => GetSentinelCount(GetRecordCountForYear(options, year))));
        if (allYearsSearch.Items.Count != expectedSentinelCount)
            throw new InvalidOperationException($"All-years FTS sentinel search returned {allYearsSearch.Items.Count}, expected {expectedSentinelCount}.");

        totalWatch.Stop();
        var infoPath = Path.Combine(root, "STRESS_FIXTURE_INFO.txt");
        var info = new List<string>
        {
            "General Maintenance Manager - Multi-Year Stress Fixture",
            $"Generated UTC: {DateTimeOffset.UtcNow:u}",
            $"Years: {options.StartYear}-{options.EndYear}",
            $"Total Maintenance records: {GetTotalRecordCount(options):N0}",
            $"Annual distribution: {string.Join(", ", Enumerable.Range(options.StartYear, yearCount).Select(year => $"{year}={GetRecordCountForYear(options, year):N0}"))}",
            $"Master Work Orders: {options.WorkOrderCount:N0}",
            $"Creation activity per record: {(options.IncludeActivity ? "yes" : "no")}",
            $"Master quick_check: {masterCheck}",
            $"Annual partition open/first-row: avg={yearOpenAverageMs:N1} ms; max={yearOpenMaxMs:N1} ms across {yearCount} years",
            $"All-years first page: {allYearsPageWatch.Elapsed.TotalMilliseconds:N1} ms ({allYearsPage.Items.Count:N0} rows)",
            $"All-years FTS sentinel search: {allYearsSearchWatch.Elapsed.TotalMilliseconds:N1} ms ({allYearsSearch.Items.Count:N0} rows)",
            $"Generation time: {totalWatch.Elapsed}",
            "",
            "Annual databases:"
        };
        info.AddRange(summaries);
        info.Add("");
        info.Add("IMPORTANT:");
        info.Add("Use this as a disposable test Data folder. The generated master database contains");
        info.Add("MaintenanceNumberSequence values synchronized to the highest generated MNT in each year.");
        info.Add("Replacing only the annual files while keeping an unrelated master database can cause");
        info.Add("future MNT allocation collisions. The recommended test setup is to replace the entire");
        info.Add("application Data folder while the application is closed.");
        await File.WriteAllLinesAsync(infoPath, info).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("MULTI-YEAR FIXTURE RESULTS");
        foreach (var summary in summaries) Console.WriteLine(summary);
        Console.WriteLine($"Master database: {paths.MasterDatabasePath}");
        Console.WriteLine($"Years directory: {paths.YearsDirectory}");
        Console.WriteLine($"Fixture info: {infoPath}");
        Console.WriteLine($"Annual partition open/first-row: avg={yearOpenAverageMs:N1} ms; max={yearOpenMaxMs:N1} ms across {yearCount} years");
        Console.WriteLine($"All-years first page: {allYearsPageWatch.Elapsed.TotalMilliseconds:N1} ms; rows={allYearsPage.Items.Count:N0}");
        Console.WriteLine($"All-years FTS sentinel search: {allYearsSearchWatch.Elapsed.TotalMilliseconds:N1} ms; rows={allYearsSearch.Items.Count:N0}");
        Console.WriteLine($"Total generation time: {totalWatch.Elapsed}");
        Console.WriteLine("Multi-year copyable database fixture: PASS");
        return 0;
    }

    private static async Task SeedCreationActivitiesAsync(int year, long recordCount, DatabaseContextFactory factory)
    {
        await using var annual = factory.CreateAnnualDbContext(year);
        var existing = await annual.MaintenanceActivity.LongCountAsync().ConfigureAwait(false);
        if (existing != 0)
        {
            if (existing != recordCount)
                throw new InvalidOperationException($"Existing {year} activity table contains {existing:N0} rows, expected {recordCount:N0}. Use a fresh fixture root.");
            Console.WriteLine($"Existing {year} creation activity rows detected: {existing:N0}; activity seeding skipped.");
            return;
        }

        await annual.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = annual.Database.GetDbConnection();
        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const int commitBatchSize = 50_000;
        var progressWatch = Stopwatch.StartNew();

        for (long batchStart = 1; batchStart <= recordCount; batchStart += commitBatchSize)
        {
            var batchEnd = Math.Min(recordCount, batchStart + commitBatchSize - 1);
            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO MaintenanceActivity (MaintenanceActivityId, MaintenanceRecordId, OccurredAtUtc, OccurredAtUtcTicks, ActivityType, Revision, Reason, Changes, ChangedBy) " +
                "VALUES ($activityId, $recordId, $occurred, $ticks, 'Created', 1, '', $changes, 'MultiYearStressFixture');";
            foreach (var name in StressParameterNames.Activity)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                command.Parameters.Add(parameter);
            }
            await command.PrepareAsync(CancellationToken.None).ConfigureAwait(false);

            for (var sequence = batchStart; sequence <= batchEnd; sequence++)
            {
                var occurred = yearStart.AddSeconds(sequence * 31).AddSeconds(1);
                Set(command, "$activityId", CreateDeterministicActivityGuid(sequence, year));
                Set(command, "$recordId", CreateDeterministicGuid(sequence, year));
                Set(command, "$occurred", occurred);
                Set(command, "$ticks", occurred.UtcTicks);
                Set(command, "$changes", $"Maintenance type: {(sequence % 2 == 0 ? "HVAC" : "Electrical")} • Location: Location {(sequence % 200) + 1}");
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            ReportRowProgress($"SEED {year} ACTIVITY", batchEnd, recordCount, progressWatch);
        }
    }

    private static async Task VerifyActivityCountAsync(int year, long expectedCount, DatabaseContextFactory factory)
    {
        await using var annual = factory.CreateAnnualDbContext(year);
        var actualCount = await annual.MaintenanceActivity.LongCountAsync().ConfigureAwait(false);
        if (actualCount != expectedCount)
            throw new InvalidOperationException($"{year} activity certification count is {actualCount:N0}; expected {expectedCount:N0}.");
    }

    private static async Task SynchronizeMaintenanceSequenceAsync(int year, long lastValue, DatabaseContextFactory factory)
    {
        await using var master = factory.CreateMasterDbContext();
        await master.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = master.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "INSERT INTO MaintenanceNumberSequence (Year, LastValue) VALUES ($year, $value) " +
            "ON CONFLICT(Year) DO UPDATE SET LastValue = excluded.LastValue;";
        var yearParameter = command.CreateParameter();
        yearParameter.ParameterName = "$year";
        yearParameter.Value = year;
        command.Parameters.Add(yearParameter);
        var valueParameter = command.CreateParameter();
        valueParameter.ParameterName = "$value";
        valueParameter.Value = lastValue;
        command.Parameters.Add(valueParameter);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task CheckpointMasterAsync(DatabaseContextFactory factory)
    {
        await using var master = factory.CreateMasterDbContext();
        await master.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = master.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task VerifyMaintenanceSequencesAsync(Options options, DatabaseContextFactory factory)
    {
        await using var master = factory.CreateMasterDbContext();
        await master.Database.OpenConnectionAsync().ConfigureAwait(false);
        for (var year = options.StartYear; year <= options.EndYear; year++)
        {
            await using var command = master.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT LastValue FROM MaintenanceNumberSequence WHERE Year = $year LIMIT 1;";
            AddParameter(command, "$year", year);
            var raw = await command.ExecuteScalarAsync().ConfigureAwait(false);
            var actual = raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            var expected = GetRecordCountForYear(options, year);
            if (actual != expected)
                throw new InvalidOperationException($"MaintenanceNumberSequence {year} is {actual:N0}; expected {expected:N0}.");
        }
    }

    private static async Task<string> QuickCheckMasterAsync(DatabaseContextFactory factory)
    {
        await using var master = factory.CreateMasterDbContext();
        await master.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = master.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<int> RunScaleAsync(
        Options options,
        DataPaths paths,
        DatabaseContextFactory factory,
        DatabaseInitializer initializer)
    {
        var seedWatch = StartPhase(1, 3, "SEED MAINTENANCE", $"Target: {options.RecordCount:N0} rows");
        await SeedWithEfAsync(options, factory).ConfigureAwait(false);
        CompletePhase("SEED MAINTENANCE", seedWatch);

        var annualManager = new AnnualMaintenanceDatabaseManager(paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annualManager);

        var pagingPhase = StartPhase(2, 3, "KEYSET PAGING", "Read two bounded 200-row pages and verify no overlap.");
        var firstPageWatch = Stopwatch.StartNew();
        var firstPage = await maintenance.GetPageAsync(new MaintenanceQuery(Year: options.Year, PageSize: 200)).ConfigureAwait(false);
        firstPageWatch.Stop();
        if (firstPage.Items.Count > 200) throw new InvalidOperationException("Page exceeded the 200-row bound.");

        var secondPageWatch = Stopwatch.StartNew();
        var secondPage = firstPage.NextCursor is null
            ? new MaintenancePage([], null, false)
            : await maintenance.GetPageAsync(new MaintenanceQuery(Year: options.Year, PageSize: 200, Cursor: firstPage.NextCursor)).ConfigureAwait(false);
        secondPageWatch.Stop();
        var overlap = firstPage.Items.Select(item => item.MaintenanceRecordId)
            .Intersect(secondPage.Items.Select(item => item.MaintenanceRecordId))
            .Any();
        if (overlap) throw new InvalidOperationException("Keyset pages overlapped.");
        CompletePhase("KEYSET PAGING", pagingPhase);

        var aggregatePhase = StartPhase(3, 3, "FILTERED AGGREGATE", "Location 1, valid records only.");
        var aggregateWatch = Stopwatch.StartNew();
        var aggregate = await maintenance.GetAggregateAsync(new MaintenanceQuery(
            Year: options.Year,
            Location: "Location 1",
            IncludeInvalid: false,
            PageSize: 200)).ConfigureAwait(false);
        aggregateWatch.Stop();
        CompletePhase("FILTERED AGGREGATE", aggregatePhase);

        Console.WriteLine();
        Console.WriteLine("RESULTS");
        Console.WriteLine($"Seed time: {seedWatch.Elapsed}");
        Console.WriteLine($"First 200-row page: {firstPageWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"Second 200-row page: {secondPageWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"Filtered aggregate: {aggregateWatch.ElapsedMilliseconds} ms (count={aggregate.Count:N0}, cost={aggregate.TotalCost:N2})");
        Console.WriteLine("Paging overlap: none");
        Console.WriteLine("Scale harness: PASS");
        return 0;
    }

    private static async Task<int> RunMillionStressAsync(
        Options options,
        DataPaths paths,
        DatabaseContextFactory factory,
        DatabaseInitializer initializer)
    {
        if (options.RecordCount < 50_000)
            throw new InvalidOperationException("Production stress mode requires at least 50,000 Maintenance records in the benchmark year so filtered-page assertions remain meaningful.");

        using var process = Process.GetCurrentProcess();
        const int totalPhases = 14;
        var totalWatch = Stopwatch.StartNew();
        var seedWatch = Stopwatch.StartNew();

        Stopwatch phaseWatch;
        if (options.BenchmarkOnly)
        {
            phaseWatch = StartPhase(1, totalPhases, "VERIFY PERSISTENT CORPUS", "Reuse existing marked Maintenance and Work Order databases without reseeding.");
            await VerifyExistingStressCorpusAsync(options, paths, factory).ConfigureAwait(false);
            CompletePhase("VERIFY PERSISTENT CORPUS", phaseWatch);
            SkipPhase(2, totalPhases, "SEED WORK ORDERS", "Benchmark-only mode reuses the existing marked corpus.");
        }
        else
        {
            phaseWatch = StartPhase(1, totalPhases, "SEED MAINTENANCE", $"Target: {options.RecordCount:N0} rows");
            await SeedStressRowsAsync(options, factory).ConfigureAwait(false);
            CompletePhase("SEED MAINTENANCE", phaseWatch);

            phaseWatch = StartPhase(2, totalPhases, "SEED WORK ORDERS", $"Target: {options.WorkOrderCount:N0} rows");
            await SeedWorkOrdersAsync(options, factory).ConfigureAwait(false);
            CompletePhase("SEED WORK ORDERS", phaseWatch);
        }
        seedWatch.Stop();

        phaseWatch = StartPhase(3, totalPhases, "OPTIMIZE + CHECKPOINT", "ANALYZE SQLite statistics and truncate WAL state.");
        var sqliteMaintenance = new SqliteMaintenanceService(paths);
        var annualMaintenance = await sqliteMaintenance.OptimizeYearAsync(
            options.Year, options.RecordCount, forceAnalyze: true).ConfigureAwait(false);
        var masterMaintenance = await sqliteMaintenance.OptimizeMasterAsync(
            options.WorkOrderCount, forceAnalyze: true).ConfigureAwait(false);
        await CheckpointAsync(options.Year, factory).ConfigureAwait(false);
        CompletePhase("OPTIMIZE + CHECKPOINT", phaseWatch);

        if (options.GenerateOnly)
        {
            WriteStressMarker(paths.DataDirectory, options);
            Console.WriteLine();
            Console.WriteLine("PERSISTENT STRESS CORPUS RESULTS");
            Console.WriteLine($"Data root: {paths.DataDirectory}");
            Console.WriteLine($"Maintenance records: {options.RecordCount:N0}");
            Console.WriteLine($"Work Orders: {options.WorkOrderCount:N0}");
            Console.WriteLine("Persistent stress corpus generation: PASS");
            return 0;
        }

        process.Refresh();
        var workingSetAfterSeedMb = ToMb(process.WorkingSet64);
        var databasePath = paths.GetYearDatabasePath(options.Year);
        var databaseSizeMb = ToMb(new FileInfo(databasePath).Length);

        phaseWatch = StartPhase(4, totalPhases, "INTEGRITY + DASHBOARD SUMMARY", "Count rows, validate permanent references, PRAGMA quick_check, and summary consistency.");
        var integrityWatch = Stopwatch.StartNew();
        var integrity = await VerifyMillionRowIntegrityAsync(options, factory).ConfigureAwait(false);
        integrityWatch.Stop();

        var annualManager = new AnnualMaintenanceDatabaseManager(paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annualManager);
        var metrics = new StressMetrics
        {
            AnnualMaintenance = annualMaintenance,
            MasterMaintenance = masterMaintenance
        };

        var summaryVerification = await maintenance.VerifyDashboardSummariesAsync(options.Year).ConfigureAwait(false);
        if (!summaryVerification.IsConsistent)
            throw new InvalidOperationException($"Dashboard summaries are inconsistent before stress reads: location mismatches={summaryVerification.LocationMismatchCount:N0}, type mismatches={summaryVerification.MaintenanceTypeMismatchCount:N0}.");
        CompletePhase("INTEGRITY + DASHBOARD SUMMARY", phaseWatch);

        phaseWatch = StartPhase(5, totalPhases, "KEYSET PAGING", "First page plus 50-page / 10,000-row sweep.");
        await RunPageStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("KEYSET PAGING", phaseWatch);

        phaseWatch = StartPhase(6, totalPhases, "DIRECT MNT LOOKUPS", $"{options.LookupIterations:N0} randomized permanent-reference lookups.");
        await RunLookupStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("DIRECT MNT LOOKUPS", phaseWatch);

        phaseWatch = StartPhase(7, totalPhases, "FILTERED PAGES", "Location, Maintenance Type, and Asset filters.");
        await RunFilteredQueryStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("FILTERED PAGES", phaseWatch);

        phaseWatch = StartPhase(8, totalPhases, "AGGREGATES", "Global, Location, and Maintenance Type aggregates.");
        await RunAggregateStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("AGGREGATES", phaseWatch);

        phaseWatch = StartPhase(9, totalPhases, "DASHBOARD QUERIES", "Current-month and full-summary latency percentiles.");
        await RunDashboardStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("DASHBOARD QUERIES", phaseWatch);

        phaseWatch = StartPhase(10, totalPhases, "FTS SEARCH", "Run the sentinel full-text query 25 times and calculate percentiles.");
        await RunBroadSearchStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("FTS SEARCH", phaseWatch);

        phaseWatch = StartPhase(11, totalPhases, "CONCURRENT READS", $"{options.ConcurrentWorkers} workers x {options.QueriesPerWorker} operations.");
        await RunConcurrentReadStressAsync(options, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("CONCURRENT READS", phaseWatch);

        phaseWatch = StartPhase(12, totalPhases, "WORK ORDER QUERIES", $"Exercise pages, filters, and dashboard against {options.WorkOrderCount:N0} rows.");
        await RunWorkOrderStressAsync(options, factory, maintenance, metrics).ConfigureAwait(false);
        CompletePhase("WORK ORDER QUERIES", phaseWatch);

        phaseWatch = StartPhase(13, totalPhases, "QUERY PLAN EVIDENCE", "Capture and enforce index-backed SQLite query plans.");
        await CaptureQueryPlanEvidenceAsync(options, factory, metrics).ConfigureAwait(false);
        CompletePhase("QUERY PLAN EVIDENCE", phaseWatch);

        if (!options.SkipBackupRestore)
        {
            phaseWatch = StartPhase(14, totalPhases, "BACKUP / RESTORE", "Healthy restore, tamper rejection, and corrupt-current recovery.");
            await RunBackupRestoreStressAsync(options, paths, factory, initializer, metrics).ConfigureAwait(false);
            CompletePhase("BACKUP / RESTORE", phaseWatch);
        }
        else
        {
            SkipPhase(14, totalPhases, "BACKUP / RESTORE", "Disabled by --skip-backup-restore.");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        totalWatch.Stop();

        var peakWorkingSetMb = ToMb(process.PeakWorkingSet64);
        var finalWorkingSetMb = ToMb(process.WorkingSet64);
        if (peakWorkingSetMb > options.MaxWorkingSetMb)
            throw new InvalidOperationException($"Peak working set {peakWorkingSetMb:N0} MB exceeded the configured {options.MaxWorkingSetMb:N0} MB ceiling.");

        Console.WriteLine();
        Console.WriteLine("PRODUCTION STRESS RESULTS");
        Console.WriteLine($"Target records: {options.RecordCount:N0}");
        Console.WriteLine($"Verified rows: {integrity.RowCount:N0}");
        Console.WriteLine($"Invalid rows: {integrity.InvalidCount:N0}");
        Console.WriteLine($"Reference sequence range: {integrity.MinSequence:N0}..{integrity.MaxSequence:N0}");
        Console.WriteLine($"Database quick_check: {integrity.QuickCheck}");
        Console.WriteLine($"Database size: {databaseSizeMb:N1} MB");
        Console.WriteLine($"SQLite annual maintenance: analyze={metrics.AnnualMaintenance?.AnalyzeRan}; stats={metrics.AnnualMaintenance?.After.StatisticsRowCount:N0}; pages={metrics.AnnualMaintenance?.After.PageCount:N0}; page-size={metrics.AnnualMaintenance?.After.PageSize:N0}; freelist={metrics.AnnualMaintenance?.After.FreelistCount:N0}; journal={metrics.AnnualMaintenance?.After.JournalMode}");
        Console.WriteLine($"SQLite master maintenance: analyze={metrics.MasterMaintenance?.AnalyzeRan}; stats={metrics.MasterMaintenance?.After.StatisticsRowCount:N0}; pages={metrics.MasterMaintenance?.After.PageCount:N0}; page-size={metrics.MasterMaintenance?.After.PageSize:N0}; freelist={metrics.MasterMaintenance?.After.FreelistCount:N0}; journal={metrics.MasterMaintenance?.After.JournalMode}");
        Console.WriteLine($"Seed time: {seedWatch.Elapsed}");
        Console.WriteLine($"Integrity scan: {integrityWatch.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"First 200-row page: {metrics.FirstPageMs:N0} ms");
        Console.WriteLine($"50 keyset pages / 10,000 rows: {metrics.KeysetSweepMs:N0} ms; overlap=none; order=valid");
        Console.WriteLine($"Direct MNT lookup x{options.LookupIterations}: p50={metrics.LookupP50Ms:N1} ms; p95={metrics.LookupP95Ms:N1} ms; max={metrics.LookupMaxMs:N1} ms");
        Console.WriteLine($"Location-filtered page: {metrics.LocationFilteredPageMs:N1} ms");
        Console.WriteLine($"Maintenance-type filtered page: {metrics.TypeFilteredPageMs:N1} ms");
        Console.WriteLine($"Asset-filtered page: {metrics.AssetFilteredPageMs:N1} ms");
        Console.WriteLine($"Global valid aggregate: {metrics.GlobalAggregateMs:N0} ms (count={metrics.GlobalAggregateCount:N0}, cost={metrics.GlobalAggregateCost:N2})");
        Console.WriteLine($"Location aggregate: {metrics.LocationAggregateMs:N0} ms (count={metrics.LocationAggregateCount:N0}, cost={metrics.LocationAggregateCost:N2})");
        Console.WriteLine($"Maintenance-type aggregate: {metrics.TypeAggregateMs:N0} ms (count={metrics.TypeAggregateCount:N0}, cost={metrics.TypeAggregateCost:N2})");
        Console.WriteLine($"Dashboard current-month aggregate p95: {metrics.DashboardMonthP95Ms:N1} ms (limit={options.MaxDashboardMonthMs:N0} ms)");
        Console.WriteLine($"Dashboard full summary warm p95: {metrics.DashboardP95Ms:N1} ms; max={metrics.DashboardMaxMs:N1} ms (limit={options.MaxDashboardMs:N0} ms)");
        Console.WriteLine($"FTS text search x25: p50={metrics.SearchP50Ms:N1} ms; p95={metrics.SearchP95Ms:N1} ms; max={metrics.SearchMaxMs:N1} ms; matches={metrics.SearchMatchCount:N0} (p95 limit={options.MaxSearchMs:N0} ms)");
        Console.WriteLine($"Concurrent read burst: {options.ConcurrentWorkers} workers x {options.QueriesPerWorker} operations = {metrics.ConcurrentOperations:N0} operations in {metrics.ConcurrentMs:N0} ms; errors=0");
        Console.WriteLine($"Work Orders: rows={options.WorkOrderCount:N0}; first-page={metrics.WorkOrderPageMs:N1} ms; filtered-page={metrics.WorkOrderFilteredPageMs:N1} ms; dashboard={metrics.WorkOrderDashboardMs:N1} ms");
        Console.WriteLine($"Query plan Maintenance page: {metrics.MaintenancePagePlan}");
        Console.WriteLine($"Query plan direct MNT: {metrics.MaintenanceLookupPlan}");
        Console.WriteLine($"Query plan Location filter: {metrics.MaintenanceLocationPlan}");
        Console.WriteLine($"Query plan Maintenance type filter: {metrics.MaintenanceTypePlan}");
        Console.WriteLine($"Query plan Asset filter: {metrics.MaintenanceAssetPlan}");
        Console.WriteLine($"Query plan Maintenance Activity type report: {metrics.MaintenanceActivityTypePlan}");
        Console.WriteLine($"Query plan Work Order page: {metrics.WorkOrderPagePlan}");
        Console.WriteLine($"Query plan Pending Work Order page: {metrics.PendingWorkOrderPagePlan}");
        Console.WriteLine($"Query plan Activity timeline: {metrics.ActivityTimelinePlan}");
        Console.WriteLine($"Query plan Activity record-type report: {metrics.ActivityRecordTypePlan}");
        Console.WriteLine($"Query plan Activity Site report: {metrics.ActivitySitePlan}");
        Console.WriteLine($"Query plan Activity Location report: {metrics.ActivityLocationPlan}");
        Console.WriteLine($"Query plan Activity type report: {metrics.ActivityTypePlan}");
        Console.WriteLine($"Query plan Activity ChangedBy report: {metrics.ActivityChangedByPlan}");
        if (!options.SkipBackupRestore)
        {
            Console.WriteLine($"Backup creation: {metrics.BackupMs:N0} ms; size={metrics.BackupSizeMb:N1} MB");
            Console.WriteLine($"Healthy backup restore: {metrics.HealthyRestoreMs:N0} ms");
            Console.WriteLine($"Tampered backup rejection: {(metrics.TamperedBackupRejected ? "PASS" : "FAIL")}");
            Console.WriteLine($"Corrupt-current + healthy-backup restore: {metrics.CorruptCurrentRestoreMs:N0} ms");
        }
        else
        {
            Console.WriteLine("Backup/restore stress: SKIPPED BY EXPLICIT OPTION");
        }
        Console.WriteLine($"Working set after seed: {workingSetAfterSeedMb:N1} MB");
        Console.WriteLine($"Final working set: {finalWorkingSetMb:N1} MB");
        Console.WriteLine($"Peak working set: {peakWorkingSetMb:N1} MB (limit={options.MaxWorkingSetMb:N0} MB)");
        Console.WriteLine($"Total stress time: {totalWatch.Elapsed}");
        Console.WriteLine("Paging overlap: none");
        Console.WriteLine("Scale harness: PASS");
        Console.WriteLine("Production stress test: PASS");
        if (options.RecordCount == 1_000_000) Console.WriteLine("Million stress test: PASS");
        return 0;
    }

    private static async Task SeedWithEfAsync(Options options, DatabaseContextFactory factory)
    {
        await using var annual = factory.CreateAnnualDbContext(options.Year);
        annual.ChangeTracker.AutoDetectChangesEnabled = false;
        var existing = await annual.MaintenanceRecords.LongCountAsync().ConfigureAwait(false);
        if (existing != 0)
        {
            if (existing != options.RecordCount)
                throw new InvalidOperationException($"Existing annual database contains {existing:N0} rows, expected {options.RecordCount:N0}. Use a fresh --root path.");
            Console.WriteLine($"Existing annual rows detected: {existing:N0}; seeding skipped.");
            return;
        }

        const int batchSize = 5_000;
        var progressWatch = Stopwatch.StartNew();
        for (long first = 1; first <= options.RecordCount; first += batchSize)
        {
            var count = (int)Math.Min(batchSize, options.RecordCount - first + 1);
            var batch = new List<MaintenanceRecord>(count);
            for (var offset = 0; offset < count; offset++)
            {
                var sequence = first + offset;
                var occurred = new DateTimeOffset(options.Year, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddMinutes(sequence % 525_600);
                var created = occurred.AddSeconds(1);
                batch.Add(new MaintenanceRecord
                {
                    MaintenanceRecordId = Guid.NewGuid(),
                    MaintenanceNumber = $"MNT-{options.Year:0000}-{sequence:0000000}",
                    ReferenceYear = options.Year,
                    ReferenceSequence = sequence,
                    OccurredAt = occurred,
                    OccurredAtUtcTicks = occurred.UtcTicks,
                    Site = $"Site {(sequence % 20) + 1}",
                    Location = $"Location {(sequence % 200) + 1}",
                    Subject = $"Synthetic maintenance {sequence}",
                    MaintenanceType = sequence % 2 == 0 ? "HVAC" : "Electrical",
                    WorkPerformed = "Synthetic scale-harness record",
                    PerformedBy = $"Tech {(sequence % 50) + 1}",
                    Cost = sequence % 100,
                    Notes = string.Empty,
                    CreatedAtUtc = created,
                    CreatedAtUtcTicks = created.UtcTicks,
                    CreatedBy = "ScaleHarness",
                    UpdatedAtUtc = created,
                    Revision = 1,
                    IsInvalid = false
                });
            }

            annual.MaintenanceRecords.AddRange(batch);
            await annual.SaveChangesAsync().ConfigureAwait(false);
            annual.ChangeTracker.Clear();
            var inserted = first + count - 1;
            ReportRowProgress("SEED MAINTENANCE", inserted, options.RecordCount, progressWatch);
        }
    }

    private static async Task SeedStressRowsAsync(Options options, DatabaseContextFactory factory)
    {
        await using var annual = factory.CreateAnnualDbContext(options.Year);
        var existing = await annual.MaintenanceRecords.LongCountAsync().ConfigureAwait(false);
        if (existing != 0)
        {
            if (existing != options.RecordCount)
                throw new InvalidOperationException($"Existing annual database contains {existing:N0} rows, expected exactly {options.RecordCount:N0}. Use a fresh root or delete the old stress database.");
            Console.WriteLine($"Existing annual rows detected: {existing:N0}; stress seeding skipped.");
            return;
        }

        await annual.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = annual.Database.GetDbConnection();
        var yearStart = new DateTimeOffset(options.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const int commitBatchSize = 50_000;
        var progressWatch = Stopwatch.StartNew();

        for (long batchStart = 1; batchStart <= options.RecordCount; batchStart += commitBatchSize)
        {
            var batchEnd = Math.Min(options.RecordCount, batchStart + commitBatchSize - 1);
            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = CreateStressInsertCommand(connection, transaction);
            await command.PrepareAsync(CancellationToken.None).ConfigureAwait(false);

            for (var sequence = batchStart; sequence <= batchEnd; sequence++)
            {
                var occurred = yearStart.AddSeconds(sequence * 31);
                var created = occurred.AddSeconds(1);
                Set(command, "$id", CreateDeterministicGuid(sequence, options.Year));
                Set(command, "$number", $"MNT-{options.Year:0000}-{sequence:0000000}");
                Set(command, "$year", options.Year);
                Set(command, "$sequence", sequence);
                Set(command, "$occurred", occurred);
                Set(command, "$occurredTicks", occurred.UtcTicks);
                Set(command, "$site", $"Site {(sequence % 20) + 1}");
                Set(command, "$location", $"Location {(sequence % 200) + 1}");
                Set(command, "$subject", $"Synthetic maintenance {sequence}");
                Set(command, "$type", sequence % 2 == 0 ? "HVAC" : "Electrical");
                Set(command, "$work", "Synthetic million-record stress row");
                Set(command, "$performedBy", $"Tech {(sequence % 50) + 1}");
                Set(command, "$cost", (sequence % 100) * 100L);
                Set(command, "$notes", sequence % GetSentinelInterval(options.RecordCount) == 0 ? "stress-sentinel" : string.Empty);
                Set(command, "$assetId", sequence % 2 == 0 ? CreateDeterministicAssetGuid((sequence % 100) + 1) : (object)DBNull.Value);
                Set(command, "$assetNumber", string.Empty);
                Set(command, "$assetName", string.Empty);
                Set(command, "$workOrder", string.Empty);
                Set(command, "$created", created);
                Set(command, "$createdTicks", created.UtcTicks);
                Set(command, "$createdBy", "MillionStressHarness");
                Set(command, "$updated", created);
                Set(command, "$revision", 1);
                var invalid = sequence % 1_000 == 0;
                Set(command, "$invalid", invalid);
                Set(command, "$invalidReason", invalid ? "Synthetic invalid row for aggregate stress coverage" : string.Empty);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            ReportRowProgress("SEED MAINTENANCE", batchEnd, options.RecordCount, progressWatch);
        }
    }

    private static async Task SeedWorkOrdersAsync(Options options, DatabaseContextFactory factory)
    {
        if (options.WorkOrderCount <= 0) return;
        await using var master = factory.CreateMasterDbContext();
        var existing = await master.WorkOrders.LongCountAsync().ConfigureAwait(false);
        if (existing != 0)
        {
            if (existing != options.WorkOrderCount)
                throw new InvalidOperationException($"Existing master database contains {existing:N0} Work Orders, expected {options.WorkOrderCount:N0}.");
            Console.WriteLine($"Existing Work Orders detected: {existing:N0}; seeding skipped.");
            await VerifyWorkOrderSequenceHighWaterAsync(factory, options.Year).ConfigureAwait(false);
            return;
        }

        master.ChangeTracker.AutoDetectChangesEnabled = false;
        const int batchSize = 5_000;
        var progressWatch = Stopwatch.StartNew();
        var reportedBase = new DateTimeOffset(options.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (long first = 1; first <= options.WorkOrderCount; first += batchSize)
        {
            var count = (int)Math.Min(batchSize, options.WorkOrderCount - first + 1);
            var batch = new List<WorkOrder>(count);
            for (var offset = 0; offset < count; offset++)
            {
                var sequence = first + offset;
                var reported = reportedBase.AddMinutes(sequence);
                var closed = sequence % 5 == 0;
                var status = closed ? GeneralMaintenanceManager.Core.Enums.WorkOrderStatus.Completed : GeneralMaintenanceManager.Core.Enums.WorkOrderStatus.Assigned;
                var priority = (GeneralMaintenanceManager.Core.Enums.WorkOrderPriority)((sequence % 4) + 1);
                var due = DateOnly.FromDateTime(reported.UtcDateTime.Date.AddDays((sequence % 60) - 30));
                batch.Add(new WorkOrder
                {
                    WorkOrderId = CreateDeterministicWorkOrderGuid(sequence, options.Year),
                    WorkOrderNumber = $"WO-{options.Year:0000}-{sequence:000000}",
                    ReferenceYear = options.Year,
                    ReferenceSequence = sequence,
                    Site = $"Site {(sequence % 20) + 1}",
                    Location = $"Location {(sequence % 200) + 1}",
                    Title = $"Synthetic work order {sequence}",
                    MaintenanceCategory = sequence % 2 == 0 ? "HVAC" : "Electrical",
                    MaintenanceType = sequence % 2 == 0 ? GeneralMaintenanceManager.Core.Enums.MaintenanceType.CorrectiveMaintenance : GeneralMaintenanceManager.Core.Enums.MaintenanceType.GeneralMaintenance,
                    Priority = priority,
                    ProblemDescription = "Synthetic DB-10 work-order scale row",
                    RequestedBy = "ScaleHarness",
                    ReportedAtUtc = reported,
                    ReportedAtUtcTicks = reported.UtcTicks,
                    DueDate = due,
                    Status = status,
                    AssignedTo = $"Tech {(sequence % 50) + 1}",
                    CreatedBy = "ScaleHarness",
                    CreatedAtUtc = reported,
                    Revision = 1,
                    LastModifiedAtUtc = reported,
                    ActivityAtUtcTicks = reported.UtcTicks,
                    IsClosed = closed,
                    SortDueDateOrdinal = due.DayNumber,
                    LastModifiedBy = "ScaleHarness"
                });
            }
            master.WorkOrders.AddRange(batch);
            await master.SaveChangesAsync().ConfigureAwait(false);
            master.ChangeTracker.Clear();
            var inserted = first + count - 1;
            ReportRowProgress("SEED WORK ORDERS", inserted, options.WorkOrderCount, progressWatch);
        }

        await VerifyWorkOrderSequenceHighWaterAsync(factory, options.Year).ConfigureAwait(false);
    }

    private static async Task VerifyWorkOrderSequenceHighWaterAsync(DatabaseContextFactory factory, int year)
    {
        await using var context = factory.CreateMasterDbContext();
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE((SELECT MAX(ReferenceSequence) FROM WorkOrders WHERE ReferenceYear=$year), 0),
                COALESCE((SELECT LastValue FROM WorkOrderNumberSequence WHERE Year=$year), 0);
            """;
        AddParameter(command, "$year", year);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Work Order sequence verification returned no row.");
        var maxReference = reader.GetInt64(0);
        var highWater = reader.GetInt64(1);
        if (highWater < maxReference)
            throw new InvalidOperationException(
                $"Work Order sequence high-water mark {highWater:N0} is behind the stored reference maximum {maxReference:N0} for {year}.");
        Console.WriteLine($"Work Order sequence high-water verified: {year}={highWater:N0} (stored max={maxReference:N0}).");
    }

    private static DbCommand CreateStressInsertCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MaintenanceRecords (
                MaintenanceRecordId, MaintenanceNumber, ReferenceYear, ReferenceSequence,
                OccurredAt, OccurredAtUtcTicks, Site, Location, Subject, MaintenanceType,
                WorkPerformed, PerformedBy, CostMinorUnits, Notes, AssetId, AssetNumberSnapshot, AssetNameSnapshot,
                WorkOrderNumberSnapshot, CreatedAtUtc, CreatedAtUtcTicks, CreatedBy, UpdatedAtUtc,
                Revision, IsInvalid, InvalidReason
            ) VALUES (
                $id, $number, $year, $sequence,
                $occurred, $occurredTicks, $site, $location, $subject, $type,
                $work, $performedBy, $cost, $notes, $assetId, $assetNumber, $assetName,
                $workOrder, $created, $createdTicks, $createdBy, $updated,
                $revision, $invalid, $invalidReason
            );
            """;

        foreach (var name in StressParameterNames.All)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static void Set(DbCommand command, string name, object value) => command.Parameters[name].Value = value;

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task CheckpointAsync(int year, DatabaseContextFactory factory)
    {
        await using var context = factory.CreateAnnualDbContext(year);
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IntegrityResult> VerifyMillionRowIntegrityAsync(Options options, DatabaseContextFactory factory)
    {
        await using var context = factory.CreateAnnualDbContext(options.Year);
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();

        long rowCount;
        long minSequence;
        long maxSequence;
        long invalidCount;
        await using (var summary = connection.CreateCommand())
        {
            summary.CommandText =
                """
                SELECT COUNT(*), MIN(ReferenceSequence), MAX(ReferenceSequence), SUM(CASE WHEN IsInvalid <> 0 THEN 1 ELSE 0 END)
                FROM MaintenanceRecords;
                """;
            await using var reader = await summary.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                throw new InvalidOperationException("The million-row database returned no integrity summary.");

            rowCount = reader.GetInt64(0);
            minSequence = reader.GetInt64(1);
            maxSequence = reader.GetInt64(2);
            invalidCount = reader.GetInt64(3);
        }
        if (rowCount != options.RecordCount || minSequence != 1 || maxSequence != options.RecordCount)
            throw new InvalidOperationException($"Million-row identity check failed: rows={rowCount:N0}, sequence={minSequence:N0}..{maxSequence:N0}.");
        var expectedInvalidCount = options.RecordCount / 1_000;
        if (invalidCount != expectedInvalidCount)
            throw new InvalidOperationException($"Synthetic invalid-row count was {invalidCount:N0}; expected {expectedInvalidCount:N0}.");

        await using var format = connection.CreateCommand();
        format.CommandText =
            """
            SELECT COUNT(*) FROM MaintenanceRecords
            WHERE MaintenanceNumber <> printf('MNT-%04d-%07d', ReferenceYear, ReferenceSequence)
               OR ReferenceYear <> $year;
            """;
        var yearParameter = format.CreateParameter();
        yearParameter.ParameterName = "$year";
        yearParameter.Value = options.Year;
        format.Parameters.Add(yearParameter);
        var mismatch = Convert.ToInt64(await format.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (mismatch != 0) throw new InvalidOperationException($"Found {mismatch:N0} malformed or mispartitioned Maintenance references.");

        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var quickCheck = Convert.ToString(await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite quick_check failed: {quickCheck}");

        return new IntegrityResult(rowCount, minSequence, maxSequence, invalidCount, quickCheck);
    }

    private static async Task RunPageStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        var watch = Stopwatch.StartNew();
        var page = await maintenance.GetPageAsync(new MaintenanceQuery(Year: options.Year, IncludeInvalid: true, PageSize: 200)).ConfigureAwait(false);
        watch.Stop();
        metrics.FirstPageMs = watch.ElapsedMilliseconds;
        RequireAtMost("first page", metrics.FirstPageMs, options.MaxPageMs);
        if (page.Items.Count != 200) throw new InvalidOperationException($"First page returned {page.Items.Count} rows; expected 200.");
        if (page.Items[0].ReferenceSequence != options.RecordCount)
            throw new InvalidOperationException("First page did not start at the newest million-row sequence.");

        var seen = new HashSet<Guid>();
        var previousSequence = options.RecordCount + 1;
        var sweep = Stopwatch.StartNew();
        var current = page;
        for (var pageIndex = 0; pageIndex < 50; pageIndex++)
        {
            foreach (var item in current.Items)
            {
                if (!seen.Add(item.MaintenanceRecordId)) throw new InvalidOperationException("Keyset stress detected a duplicate Maintenance record across pages.");
                if (item.ReferenceSequence >= previousSequence) throw new InvalidOperationException("Keyset stress detected non-descending Maintenance order.");
                previousSequence = item.ReferenceSequence;
            }

            if (pageIndex == 49) break;
            if (current.NextCursor is null) throw new InvalidOperationException("Keyset stress ended before 50 pages.");
            current = await maintenance.GetPageAsync(new MaintenanceQuery(
                Year: options.Year,
                IncludeInvalid: true,
                PageSize: 200,
                Cursor: current.NextCursor)).ConfigureAwait(false);
            if (current.Items.Count != 200) throw new InvalidOperationException("Keyset stress returned an unexpectedly short intermediate page.");
        }
        sweep.Stop();
        metrics.KeysetSweepMs = sweep.ElapsedMilliseconds;
        if (seen.Count != 10_000) throw new InvalidOperationException($"Keyset sweep produced {seen.Count:N0} unique rows; expected 10,000.");
        RequireAtMost("50-page keyset sweep", metrics.KeysetSweepMs, options.MaxKeysetSweepMs);
    }

    private static async Task RunLookupStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        var random = new Random(options.Seed);
        var timings = new double[options.LookupIterations];
        for (var index = 0; index < options.LookupIterations; index++)
        {
            var sequence = random.NextInt64(1, options.RecordCount + 1);
            var number = $"MNT-{options.Year:0000}-{sequence:0000000}";
            var watch = Stopwatch.StartNew();
            var record = await maintenance.GetByNumberAsync(number).ConfigureAwait(false);
            watch.Stop();
            if (record?.ReferenceSequence != sequence)
                throw new InvalidOperationException($"Direct lookup failed for {number}.");
            timings[index] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        metrics.LookupP50Ms = Percentile(timings, 0.50);
        metrics.LookupP95Ms = Percentile(timings, 0.95);
        metrics.LookupMaxMs = timings[^1];
        RequireAtMost("direct MNT lookup p95", metrics.LookupP95Ms, options.MaxLookupP95Ms);
    }

    private static async Task RunFilteredQueryStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        var locationWatch = Stopwatch.StartNew();
        var locationPage = await maintenance.GetPageAsync(new MaintenanceQuery(
            Year: options.Year,
            Location: "Location 1",
            IncludeInvalid: false,
            PageSize: 200)).ConfigureAwait(false);
        locationWatch.Stop();
        metrics.LocationFilteredPageMs = locationWatch.Elapsed.TotalMilliseconds;
        if (locationPage.Items.Count != 200) throw new InvalidOperationException($"Location-filtered page returned {locationPage.Items.Count} rows; expected 200.");
        if (locationPage.Items.Any(item => item.IsInvalid || !string.Equals(item.Location, "Location 1", StringComparison.Ordinal)))
            throw new InvalidOperationException("Location-filtered page returned a row outside the requested predicate.");
        RequireAtMost("location-filtered page", metrics.LocationFilteredPageMs, options.MaxFilterPageMs);

        var typeWatch = Stopwatch.StartNew();
        var typePage = await maintenance.GetPageAsync(new MaintenanceQuery(
            Year: options.Year,
            MaintenanceType: "HVAC",
            IncludeInvalid: false,
            PageSize: 200)).ConfigureAwait(false);
        typeWatch.Stop();
        metrics.TypeFilteredPageMs = typeWatch.Elapsed.TotalMilliseconds;
        if (typePage.Items.Count != 200 || typePage.Items.Any(item => item.IsInvalid || !string.Equals(item.MaintenanceType, "HVAC", StringComparison.Ordinal)))
            throw new InvalidOperationException("Maintenance-type filtered page did not return 200 valid HVAC rows.");
        RequireAtMost("maintenance-type filtered page", metrics.TypeFilteredPageMs, options.MaxFilterPageMs);

        var assetId = CreateDeterministicAssetGuid(1);
        var assetWatch = Stopwatch.StartNew();
        var assetPage = await maintenance.GetPageAsync(new MaintenanceQuery(
            Year: options.Year,
            AssetId: assetId,
            IncludeInvalid: false,
            PageSize: 200)).ConfigureAwait(false);
        assetWatch.Stop();
        metrics.AssetFilteredPageMs = assetWatch.Elapsed.TotalMilliseconds;
        if (assetPage.Items.Count != 200 || assetPage.Items.Any(item => item.IsInvalid || item.AssetId != assetId))
            throw new InvalidOperationException("Asset-filtered page did not return 200 valid rows for the requested synthetic asset.");
        RequireAtMost("asset-filtered page", metrics.AssetFilteredPageMs, options.MaxFilterPageMs);
    }

    private static async Task RunAggregateStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        var globalWatch = Stopwatch.StartNew();
        var global = await maintenance.GetAggregateAsync(new MaintenanceQuery(Year: options.Year, IncludeInvalid: false)).ConfigureAwait(false);
        globalWatch.Stop();
        metrics.GlobalAggregateMs = globalWatch.ElapsedMilliseconds;
        metrics.GlobalAggregateCount = global.Count;
        metrics.GlobalAggregateCost = global.TotalCost;
        var expectedInvalid = options.RecordCount / 1_000L;
        var expectedGlobalCount = options.RecordCount - expectedInvalid;
        var expectedGlobalCost = SumModulo100(options.RecordCount);
        if (global.Count != expectedGlobalCount || global.TotalCost != expectedGlobalCost)
            throw new InvalidOperationException($"Global valid aggregate mismatch: count={global.Count:N0}/{expectedGlobalCount:N0}, cost={global.TotalCost:N2}/{expectedGlobalCost:N2}.");
        RequireAtMost("global aggregate", metrics.GlobalAggregateMs, options.MaxAggregateMs);

        var locationWatch = Stopwatch.StartNew();
        var location = await maintenance.GetAggregateAsync(new MaintenanceQuery(
            Year: options.Year,
            Location: "Location 1",
            IncludeInvalid: false)).ConfigureAwait(false);
        locationWatch.Stop();
        metrics.LocationAggregateMs = locationWatch.ElapsedMilliseconds;
        metrics.LocationAggregateCount = location.Count;
        metrics.LocationAggregateCost = location.TotalCost;
        var expectedLocationCount = (options.RecordCount / 200L) - expectedInvalid;
        if (location.Count != expectedLocationCount || location.TotalCost != 0m)
            throw new InvalidOperationException($"Location aggregate mismatch: count={location.Count:N0}/{expectedLocationCount:N0}, cost={location.TotalCost:N2}/0.00.");
        RequireAtMost("location aggregate", metrics.LocationAggregateMs, options.MaxAggregateMs);

        var typeWatch = Stopwatch.StartNew();
        var type = await maintenance.GetAggregateAsync(new MaintenanceQuery(
            Year: options.Year,
            MaintenanceType: "HVAC",
            IncludeInvalid: false)).ConfigureAwait(false);
        typeWatch.Stop();
        metrics.TypeAggregateMs = typeWatch.ElapsedMilliseconds;
        metrics.TypeAggregateCount = type.Count;
        metrics.TypeAggregateCost = type.TotalCost;
        var expectedTypeCount = (options.RecordCount / 2L) - expectedInvalid;
        var expectedTypeCost = SumEvenModulo100(options.RecordCount);
        if (type.Count != expectedTypeCount || type.TotalCost != expectedTypeCost)
            throw new InvalidOperationException($"Maintenance-type aggregate mismatch: count={type.Count:N0}/{expectedTypeCount:N0}, cost={type.TotalCost:N2}/{expectedTypeCost:N2}.");
        RequireAtMost("maintenance-type aggregate", metrics.TypeAggregateMs, options.MaxAggregateMs);
    }

    private static async Task RunDashboardStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        const int warmIterations = 12;
        var now = DateTimeOffset.Now;
        var monthStartLocal = new DateTimeOffset(new DateTime(options.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local));
        var monthEndLocal = monthStartLocal.AddMonths(1).AddTicks(-1);

        var monthTimings = new double[warmIterations];
        for (var index = 0; index < warmIterations; index++)
        {
            var monthWatch = Stopwatch.StartNew();
            _ = await maintenance.GetAggregateAsync(new MaintenanceQuery(
                Year: options.Year,
                FromInclusive: monthStartLocal,
                ToInclusive: monthEndLocal,
                IncludeInvalid: false)).ConfigureAwait(false);
            monthWatch.Stop();
            monthTimings[index] = monthWatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(monthTimings);
        metrics.DashboardMonthP95Ms = Percentile(monthTimings, 0.95);
        RequireAtMost("dashboard current-month aggregate p95", metrics.DashboardMonthP95Ms, options.MaxDashboardMonthMs);

        var dashboardTimings = new double[warmIterations];
        MaintenanceDashboardSummary? summary = null;
        for (var index = 0; index < warmIterations; index++)
        {
            var dashboardWatch = Stopwatch.StartNew();
            summary = await maintenance.GetDashboardSummaryAsync(options.Year).ConfigureAwait(false);
            dashboardWatch.Stop();
            dashboardTimings[index] = dashboardWatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(dashboardTimings);
        metrics.DashboardP95Ms = Percentile(dashboardTimings, 0.95);
        metrics.DashboardMaxMs = dashboardTimings[^1];

        if (summary is null
            || summary.ReferenceYear != options.Year
            || summary.RecentRecords.Count > 6
            || summary.TopLocations.Count > 5
            || summary.TopMaintenanceTypes.Count > 5)
        {
            throw new InvalidOperationException("Dashboard stress returned an invalid bounded summary.");
        }

        RequireAtMost("dashboard warm p95", metrics.DashboardP95Ms, options.MaxDashboardMs);
    }

    private static async Task RunBroadSearchStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        const int iterations = 25;
        var timings = new double[iterations];
        var matchCount = 0;
        for (var index = 0; index < iterations; index++)
        {
            var watch = Stopwatch.StartNew();
            var page = await maintenance.GetPageAsync(new MaintenanceQuery(
                Year: options.Year,
                SearchText: "stress-sentinel",
                IncludeInvalid: true,
                PageSize: 200)).ConfigureAwait(false);
            watch.Stop();
            timings[index] = watch.Elapsed.TotalMilliseconds;
            matchCount = page.Items.Count;
            var expectedMatches = (int)GetSentinelCount(options.RecordCount);
            if (matchCount != expectedMatches)
                throw new InvalidOperationException($"FTS sentinel query returned {matchCount} rows; expected {expectedMatches}.");
        }

        Array.Sort(timings);
        metrics.SearchP50Ms = Percentile(timings, 0.50);
        metrics.SearchP95Ms = Percentile(timings, 0.95);
        metrics.SearchMaxMs = timings[^1];
        metrics.SearchMatchCount = matchCount;
        RequireAtMost("FTS text search p95", metrics.SearchP95Ms, options.MaxSearchMs);
    }

    private static async Task RunConcurrentReadStressAsync(Options options, MaintenanceRecordService maintenance, StressMetrics metrics)
    {
        var watch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, options.ConcurrentWorkers)
            .Select(worker => RunReadWorkerAsync(options, maintenance, worker))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        watch.Stop();
        metrics.ConcurrentMs = watch.ElapsedMilliseconds;
        metrics.ConcurrentOperations = results.Sum(result => result.Operations);
        var errors = results.SelectMany(result => result.Errors).ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException("Concurrent read stress failed: " + string.Join(" | ", errors));
        if (metrics.ConcurrentOperations != options.ConcurrentWorkers * options.QueriesPerWorker)
            throw new InvalidOperationException("Concurrent read stress did not complete the expected operation count.");
        RequireAtMost("concurrent read burst", metrics.ConcurrentMs, options.MaxConcurrentMs);
    }

    private static async Task<WorkerResult> RunReadWorkerAsync(Options options, MaintenanceRecordService maintenance, int worker)
    {
        var random = new Random(options.Seed + worker + 1);
        var errors = new List<string>();
        var operations = 0;
        for (var iteration = 0; iteration < options.QueriesPerWorker; iteration++)
        {
            try
            {
                if (iteration % 2 == 0)
                {
                    var sequence = random.NextInt64(1, options.RecordCount + 1);
                    var number = $"MNT-{options.Year:0000}-{sequence:0000000}";
                    var record = await maintenance.GetByNumberAsync(number).ConfigureAwait(false);
                    if (record?.ReferenceSequence != sequence) throw new InvalidOperationException($"lookup mismatch for {number}");
                }
                else
                {
                    var location = $"Location {random.Next(1, 201)}";
                    var page = await maintenance.GetPageAsync(new MaintenanceQuery(
                        Year: options.Year,
                        Location: location,
                        IncludeInvalid: false,
                        PageSize: 50)).ConfigureAwait(false);
                    if (page.Items.Count > 50 || page.Items.Any(item => !string.Equals(item.Location, location, StringComparison.Ordinal)))
                        throw new InvalidOperationException($"filtered page mismatch for {location}");
                }
                operations++;
            }
            catch (Exception ex)
            {
                errors.Add($"worker {worker}, iteration {iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return new WorkerResult(operations, errors);
    }

    private static async Task RunWorkOrderStressAsync(
        Options options,
        DatabaseContextFactory factory,
        MaintenanceRecordService maintenance,
        StressMetrics metrics)
    {
        if (options.WorkOrderCount <= 0) return;
        var service = new WorkOrderService(factory, maintenance);

        var pageWatch = Stopwatch.StartNew();
        var page = await service.GetPageAsync(new WorkOrderQuery(PageSize: 200)).ConfigureAwait(false);
        pageWatch.Stop();
        metrics.WorkOrderPageMs = pageWatch.Elapsed.TotalMilliseconds;
        if (page.Items.Count != 200) throw new InvalidOperationException($"Work Order first page returned {page.Items.Count}, expected 200.");
        RequireAtMost("Work Order first page", metrics.WorkOrderPageMs, options.MaxWorkOrderPageMs);

        var filteredWatch = Stopwatch.StartNew();
        var filtered = await service.GetPageAsync(new WorkOrderQuery(Location: "Location 1", IsClosed: false, PageSize: 200)).ConfigureAwait(false);
        filteredWatch.Stop();
        metrics.WorkOrderFilteredPageMs = filteredWatch.Elapsed.TotalMilliseconds;
        if (filtered.Items.Any(item => item.IsClosed || !string.Equals(item.Location, "Location 1", StringComparison.Ordinal)))
            throw new InvalidOperationException("Work Order filtered page returned a row outside the requested predicate.");
        RequireAtMost("Work Order filtered page", metrics.WorkOrderFilteredPageMs, options.MaxWorkOrderPageMs);

        var dashboardWatch = Stopwatch.StartNew();
        var dashboard = await service.GetDashboardMetricsAsync().ConfigureAwait(false);
        dashboardWatch.Stop();
        metrics.WorkOrderDashboardMs = dashboardWatch.Elapsed.TotalMilliseconds;
        if (dashboard.OpenCount <= 0 || dashboard.UrgentOpenCount <= 0)
            throw new InvalidOperationException("Work Order dashboard scale metrics were not populated as expected.");
        RequireAtMost("Work Order dashboard", metrics.WorkOrderDashboardMs, options.MaxDashboardMs);
    }

    private static async Task RunBackupRestoreStressAsync(
        Options options,
        DataPaths paths,
        DatabaseContextFactory factory,
        DatabaseInitializer initializer,
        StressMetrics metrics)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var health = new DatabaseHealthService(paths);
        using var backupService = new BackupService(paths, health, initializer);
        var healthyBackupPath = Path.Combine(paths.BackupsDirectory, $"DB10-{options.Year:0000}-healthy.zip");
        var tamperedBackupPath = Path.Combine(paths.BackupsDirectory, $"DB10-{options.Year:0000}-tampered.zip");
        if (File.Exists(healthyBackupPath)) File.Delete(healthyBackupPath);
        if (File.Exists(tamperedBackupPath)) File.Delete(tamperedBackupPath);

        Console.WriteLine("  [1/4] Creating certified backup...");
        var backupWatch = Stopwatch.StartNew();
        var backup = await backupService.CreateBackupAsync(healthyBackupPath).ConfigureAwait(false);
        backupWatch.Stop();
        metrics.BackupMs = backupWatch.Elapsed.TotalMilliseconds;
        metrics.BackupSizeMb = ToMb(backup.SizeBytes);
        Console.WriteLine($"        backup created: {metrics.BackupSizeMb:N1} MB in {backupWatch.Elapsed}");

        Console.WriteLine("  [2/4] Restoring healthy backup and re-validating million-row data...");
        var healthyRestoreWatch = Stopwatch.StartNew();
        await backupService.RestoreBackupAsync(healthyBackupPath).ConfigureAwait(false);
        healthyRestoreWatch.Stop();
        metrics.HealthyRestoreMs = healthyRestoreWatch.Elapsed.TotalMilliseconds;
        await RequireRestoredScaleDataAsync(options, factory).ConfigureAwait(false);
        Console.WriteLine($"        healthy restore verified in {healthyRestoreWatch.Elapsed}");

        Console.WriteLine("  [3/4] Tampering manifest and verifying rejection...");
        File.Copy(healthyBackupPath, tamperedBackupPath, overwrite: true);
        TamperBackupManifest(tamperedBackupPath);
        try
        {
            await backupService.RestoreBackupAsync(tamperedBackupPath).ConfigureAwait(false);
            throw new InvalidOperationException("Tampered backup was accepted unexpectedly.");
        }
        catch (InvalidDataException)
        {
            metrics.TamperedBackupRejected = true;
        }
        await RequireRestoredScaleDataAsync(options, factory).ConfigureAwait(false);
        Console.WriteLine("        tampered backup rejected: PASS");

        Console.WriteLine("  [4/4] Corrupting current annual DB, restoring healthy backup, and re-validating...");
        await CheckpointAsync(options.Year, factory).ConfigureAwait(false);
        var annualPath = paths.GetYearDatabasePath(options.Year);
        await File.WriteAllBytesAsync(annualPath, "not-a-sqlite-database"u8.ToArray()).ConfigureAwait(false);
        TryDeleteFile(annualPath + "-wal");
        TryDeleteFile(annualPath + "-shm");

        var corruptRestoreWatch = Stopwatch.StartNew();
        await backupService.RestoreBackupAsync(healthyBackupPath).ConfigureAwait(false);
        corruptRestoreWatch.Stop();
        metrics.CorruptCurrentRestoreMs = corruptRestoreWatch.Elapsed.TotalMilliseconds;
        await RequireRestoredScaleDataAsync(options, factory).ConfigureAwait(false);
        Console.WriteLine($"        corrupt-current recovery verified in {corruptRestoreWatch.Elapsed}");
    }

    private static async Task RequireRestoredScaleDataAsync(Options options, DatabaseContextFactory factory)
    {
        var integrity = await VerifyMillionRowIntegrityAsync(options, factory).ConfigureAwait(false);
        if (integrity.RowCount != options.RecordCount || !string.Equals(integrity.QuickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restored annual database did not preserve the certified million-row dataset.");

        await using var master = factory.CreateMasterDbContext();
        var workOrders = await master.WorkOrders.LongCountAsync().ConfigureAwait(false);
        if (workOrders != options.WorkOrderCount)
            throw new InvalidDataException($"Restored master database contains {workOrders:N0} Work Orders, expected {options.WorkOrderCount:N0}.");
    }

    private static void TamperBackupManifest(string backupPath)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("backup-manifest.json")
            ?? throw new InvalidDataException("Backup manifest was missing from the certification archive.");
        string manifest;
        using (var reader = new StreamReader(entry.Open()))
            manifest = reader.ReadToEnd();

        const string marker = "\"Sha256\":\"";
        var markerIndex = manifest.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) throw new InvalidDataException("Backup manifest contains no SHA-256 entry to tamper.");
        var hashIndex = markerIndex + marker.Length;
        var replacement = manifest[hashIndex] == '0' ? '1' : '0';
        manifest = manifest[..hashIndex] + replacement + manifest[(hashIndex + 1)..];

        entry.Delete();
        var replacementEntry = archive.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacementEntry.Open());
        writer.Write(manifest);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort inside the disposable certification fixture.
        }
    }

    private static async Task CaptureQueryPlanEvidenceAsync(Options options, DatabaseContextFactory factory, StressMetrics metrics)
    {
        await using var annual = factory.CreateAnnualDbContext(options.Year);
        await annual.Database.OpenConnectionAsync().ConfigureAwait(false);
        var annualConnection = annual.Database.GetDbConnection();
        metrics.MaintenancePagePlan = await ExplainAsync(annualConnection,
            "SELECT MaintenanceRecordId FROM MaintenanceRecords WHERE IsInvalid=0 ORDER BY OccurredAtUtcTicks DESC, CreatedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.MaintenanceLookupPlan = await ExplainAsync(annualConnection,
            $"SELECT MaintenanceRecordId FROM MaintenanceRecords WHERE MaintenanceNumber='MNT-{options.Year:0000}-0500000' LIMIT 1;").ConfigureAwait(false);
        metrics.MaintenanceLocationPlan = await ExplainAsync(annualConnection,
            "SELECT MaintenanceRecordId FROM MaintenanceRecords WHERE Location='Location 1' AND IsInvalid=0 ORDER BY OccurredAtUtcTicks DESC, CreatedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.MaintenanceTypePlan = await ExplainAsync(annualConnection,
            "SELECT MaintenanceRecordId FROM MaintenanceRecords WHERE MaintenanceType='HVAC' AND IsInvalid=0 ORDER BY OccurredAtUtcTicks DESC, CreatedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.MaintenanceAssetPlan = await ExplainAsync(annualConnection,
            $"SELECT MaintenanceRecordId FROM MaintenanceRecords WHERE AssetId='{CreateDeterministicAssetGuid(1).ToString("D").ToUpperInvariant()}' AND IsInvalid=0 ORDER BY OccurredAtUtcTicks DESC, CreatedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.MaintenanceActivityTypePlan = await ExplainAsync(annualConnection,
            "SELECT a.MaintenanceActivityId FROM MaintenanceActivity AS a INNER JOIN MaintenanceRecords AS r ON r.MaintenanceRecordId=a.MaintenanceRecordId WHERE a.ActivityType='Created' ORDER BY a.OccurredAtUtcTicks DESC, a.MaintenanceActivityId DESC LIMIT 100;").ConfigureAwait(false);

        await using var master = factory.CreateMasterDbContext();
        await master.Database.OpenConnectionAsync().ConfigureAwait(false);
        metrics.WorkOrderPagePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT WorkOrderId FROM WorkOrders ORDER BY IsClosed ASC, Priority DESC, SortDueDateOrdinal ASC, ReportedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.PendingWorkOrderPagePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT WorkOrderId FROM WorkOrders WHERE IsClosed=0 ORDER BY IsClosed ASC, Priority DESC, SortDueDateOrdinal ASC, ReportedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 200;").ConfigureAwait(false);
        metrics.ActivityTimelinePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);
        metrics.ActivityRecordTypePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE RecordType='WorkOrder' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);
        metrics.ActivitySitePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE Site='Site 1' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);
        metrics.ActivityLocationPlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE Location='Location 1' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);
        metrics.ActivityTypePlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE ActivityType='WorkOrderCreated' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);
        metrics.ActivityChangedByPlan = await ExplainAsync(master.Database.GetDbConnection(),
            "SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE ChangedBy='ScaleHarness' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 100;").ConfigureAwait(false);

        RequirePlan(metrics.MaintenancePagePlan, "IX_MaintenanceRecords_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence", "Maintenance page");
        RequirePlan(metrics.MaintenanceLookupPlan, "IX_MaintenanceRecords_MaintenanceNumber", "direct MNT lookup");
        RequirePlan(metrics.MaintenanceLocationPlan, "IX_MaintenanceRecords_Location_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence", "Location filter");
        RequirePlan(metrics.MaintenanceTypePlan, "IX_MaintenanceRecords_MaintenanceType_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence", "Maintenance type filter");
        RequirePlan(metrics.MaintenanceAssetPlan, "IX_MaintenanceRecords_AssetId_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence", "Asset filter");
        RequirePlan(metrics.MaintenanceActivityTypePlan, "IX_MaintenanceActivity_ActivityType_OccurredAtUtcTicks_MaintenanceActivityId", "Maintenance Activity type report");
        RequirePlan(metrics.WorkOrderPagePlan, "IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence", "Work Order page");
        RequirePlan(metrics.PendingWorkOrderPagePlan, "IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence", "Pending Work Order page");
        RequirePlan(metrics.ActivityTimelinePlan, "IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History timeline");
        RequirePlan(metrics.ActivityRecordTypePlan, "IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History record-type report");
        RequirePlan(metrics.ActivitySitePlan, "IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History Site report");
        RequirePlan(metrics.ActivityLocationPlan, "IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History Location report");
        RequirePlan(metrics.ActivityTypePlan, "IX_ActivityHistory_ActivityType_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History type report");
        RequirePlan(metrics.ActivityChangedByPlan, "IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId", "Activity History ChangedBy report");
    }

    private static void RequirePlan(string plan, string expectedIndex, string label)
    {
        if (!plan.Contains(expectedIndex, StringComparison.Ordinal)
            || plan.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} query plan did not use the required bounded index '{expectedIndex}': {plan}");
    }

    private static async Task<string> ExplainAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var details = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            details.Add(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty);
        return string.Join(" | ", details);
    }

    private static Guid CreateDeterministicAssetGuid(long bucket)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, bucket);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], unchecked((int)0x41535354));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], unchecked((int)0x474D4D34));
        return new Guid(bytes);
    }

    private static Guid CreateDeterministicWorkOrderGuid(long sequence, int year)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], year);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], unchecked((int)0x474D4D33));
        return new Guid(bytes);
    }

    private static Guid CreateDeterministicGuid(long sequence, int year)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], year);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], unchecked((int)0x474D4D31));
        return new Guid(bytes);
    }

    private static Guid CreateDeterministicActivityGuid(long sequence, int year)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], year);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], unchecked((int)0x474D4D32));
        return new Guid(bytes);
    }

    private static Stopwatch StartPhase(int phase, int totalPhases, string name, string? detail = null)
    {
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PHASE {phase}/{totalPhases}: {name}");
        if (!string.IsNullOrWhiteSpace(detail))
            Console.WriteLine($"  {detail}");
        return Stopwatch.StartNew();
    }

    private static void CompletePhase(string name, Stopwatch watch)
    {
        watch.Stop();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PASS: {name} | elapsed {FormatDuration(watch.Elapsed)}");
    }

    private static void SkipPhase(int phase, int totalPhases, string name, string reason)
    {
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PHASE {phase}/{totalPhases}: {name} | SKIPPED");
        Console.WriteLine($"  {reason}");
    }

    private static void ReportRowProgress(string label, long completed, long total, Stopwatch watch)
    {
        var elapsedSeconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001d);
        var rate = completed / elapsedSeconds;
        var remaining = Math.Max(0, total - completed);
        var eta = rate > 0
            ? TimeSpan.FromSeconds(remaining / rate)
            : TimeSpan.Zero;
        var percent = total <= 0 ? 100d : (completed * 100d) / total;

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {label}: {completed:N0}/{total:N0} ({percent,5:N1}%) | " +
            $"{rate:N0} rows/s | elapsed {FormatDuration(watch.Elapsed)} | ETA {FormatDuration(eta)}");
    }

    private static string FormatDuration(TimeSpan value)
    {
        var totalHours = (int)value.TotalHours;
        return totalHours > 0
            ? $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static void RequireAtMost(string label, double actualMs, double maximumMs)
    {
        if (actualMs > maximumMs)
            throw new InvalidOperationException($"{label} took {actualMs:N1} ms, exceeding the configured {maximumMs:N1} ms stress ceiling.");
    }

    private static double ToMb(long bytes) => bytes / (1024d * 1024d);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cleanup warning for '{path}': {ex.Message}");
        }
    }
}

internal static class StressParameterNames
{
    public static readonly string[] Activity = ["$activityId", "$recordId", "$occurred", "$ticks", "$changes"];

    public static readonly string[] All =
    [
        "$id", "$number", "$year", "$sequence", "$occurred", "$occurredTicks", "$site", "$location",
        "$subject", "$type", "$work", "$performedBy", "$cost", "$notes", "$assetId", "$assetNumber", "$assetName",
        "$workOrder", "$created", "$createdTicks", "$createdBy", "$updated", "$revision", "$invalid", "$invalidReason"
    ];
}

internal sealed class StressMetrics
{
    public long FirstPageMs { get; set; }
    public long KeysetSweepMs { get; set; }
    public double LookupP50Ms { get; set; }
    public double LookupP95Ms { get; set; }
    public double LookupMaxMs { get; set; }
    public double LocationFilteredPageMs { get; set; }
    public double TypeFilteredPageMs { get; set; }
    public double AssetFilteredPageMs { get; set; }
    public long GlobalAggregateMs { get; set; }
    public long GlobalAggregateCount { get; set; }
    public decimal GlobalAggregateCost { get; set; }
    public long LocationAggregateMs { get; set; }
    public long LocationAggregateCount { get; set; }
    public decimal LocationAggregateCost { get; set; }
    public long TypeAggregateMs { get; set; }
    public long TypeAggregateCount { get; set; }
    public decimal TypeAggregateCost { get; set; }
    public double DashboardMonthP95Ms { get; set; }
    public double DashboardP95Ms { get; set; }
    public double DashboardMaxMs { get; set; }
    public double SearchP50Ms { get; set; }
    public double SearchP95Ms { get; set; }
    public double SearchMaxMs { get; set; }
    public int SearchMatchCount { get; set; }
    public long ConcurrentMs { get; set; }
    public int ConcurrentOperations { get; set; }
    public double WorkOrderPageMs { get; set; }
    public double WorkOrderFilteredPageMs { get; set; }
    public double WorkOrderDashboardMs { get; set; }
    public double BackupMs { get; set; }
    public double BackupSizeMb { get; set; }
    public double HealthyRestoreMs { get; set; }
    public bool TamperedBackupRejected { get; set; }
    public double CorruptCurrentRestoreMs { get; set; }
    public SqliteMaintenanceResult? AnnualMaintenance { get; set; }
    public SqliteMaintenanceResult? MasterMaintenance { get; set; }
    public string MaintenancePagePlan { get; set; } = string.Empty;
    public string MaintenanceLookupPlan { get; set; } = string.Empty;
    public string MaintenanceLocationPlan { get; set; } = string.Empty;
    public string MaintenanceTypePlan { get; set; } = string.Empty;
    public string MaintenanceAssetPlan { get; set; } = string.Empty;
    public string MaintenanceActivityTypePlan { get; set; } = string.Empty;
    public string WorkOrderPagePlan { get; set; } = string.Empty;
    public string PendingWorkOrderPagePlan { get; set; } = string.Empty;
    public string ActivityTimelinePlan { get; set; } = string.Empty;
    public string ActivityRecordTypePlan { get; set; } = string.Empty;
    public string ActivitySitePlan { get; set; } = string.Empty;
    public string ActivityLocationPlan { get; set; } = string.Empty;
    public string ActivityTypePlan { get; set; } = string.Empty;
    public string ActivityChangedByPlan { get; set; } = string.Empty;
}

internal sealed record IntegrityResult(long RowCount, long MinSequence, long MaxSequence, long InvalidCount, string QuickCheck);
internal sealed record WorkerResult(int Operations, IReadOnlyList<string> Errors);

internal sealed record Options(
    int Year,
    long RecordCount,
    long TotalRecordCount,
    string? Root,
    bool KeepData,
    bool Stress,
    bool GenerateFixture,
    bool GenerateOnly,
    bool BenchmarkOnly,
    bool VerifyExistingCorpus,
    bool Reset,
    int StartYear,
    int EndYear,
    bool IncludeActivity,
    long WorkOrderCount,
    bool SkipBackupRestore,
    int LookupIterations,
    int ConcurrentWorkers,
    int QueriesPerWorker,
    int Seed,
    double MaxPageMs,
    double MaxKeysetSweepMs,
    double MaxLookupP95Ms,
    double MaxFilterPageMs,
    double MaxAggregateMs,
    double MaxDashboardMonthMs,
    double MaxDashboardMs,
    double MaxWorkOrderPageMs,
    double MaxSearchMs,
    double MaxConcurrentMs,
    double MaxWorkingSetMb)
{
    public static Options Parse(string[] args)
    {
        var year = DateTime.Today.Year;
        long records = 100_000;
        long totalRecords = 0;
        string? root = null;
        var keep = false;
        var stress = false;
        var generateFixture = false;
        var generateOnly = false;
        var benchmarkOnly = false;
        var verifyExistingCorpus = false;
        var reset = false;
        var startYear = 2020;
        var endYear = 2026;
        var includeActivity = false;
        long workOrderCount = 100_000;
        var skipBackupRestore = false;
        var lookupIterations = 200;
        var concurrentWorkers = 8;
        var queriesPerWorker = 25;
        var seed = 20260909;
        double maxPageMs = 250;
        double maxKeysetSweepMs = 30_000;
        double maxLookupP95Ms = 100;
        double maxFilterPageMs = 300;
        double maxAggregateMs = 15_000;
        double maxDashboardMonthMs = 250;
        double maxDashboardMs = 300;
        double maxWorkOrderPageMs = 250;
        double maxSearchMs = 500;
        double maxConcurrentMs = 120_000;
        double maxWorkingSetMb = 1_024;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--year=", StringComparison.OrdinalIgnoreCase))
                year = ParseInt(arg[7..], "year");
            else if (arg.StartsWith("--start-year=", StringComparison.OrdinalIgnoreCase))
                startYear = ParseInt(arg[13..], "start-year");
            else if (arg.StartsWith("--end-year=", StringComparison.OrdinalIgnoreCase))
                endYear = ParseInt(arg[11..], "end-year");
            else if (arg.StartsWith("--records-per-year=", StringComparison.OrdinalIgnoreCase))
                records = ParseLong(arg[19..], "records-per-year");
            else if (arg.StartsWith("--records=", StringComparison.OrdinalIgnoreCase))
                records = ParseLong(arg[10..], "records");
            else if (arg.StartsWith("--total-records=", StringComparison.OrdinalIgnoreCase))
                totalRecords = ParseLong(arg[16..], "total-records");
            else if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
                root = arg[7..];
            else if (arg.StartsWith("--work-orders=", StringComparison.OrdinalIgnoreCase))
                workOrderCount = ParseLong(arg[14..], "work-orders");
            else if (arg.StartsWith("--lookups=", StringComparison.OrdinalIgnoreCase))
                lookupIterations = ParseInt(arg[10..], "lookups");
            else if (arg.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
                concurrentWorkers = ParseInt(arg[10..], "workers");
            else if (arg.StartsWith("--queries-per-worker=", StringComparison.OrdinalIgnoreCase))
                queriesPerWorker = ParseInt(arg[21..], "queries-per-worker");
            else if (arg.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase))
                seed = ParseInt(arg[7..], "seed");
            else if (arg.StartsWith("--max-page-ms=", StringComparison.OrdinalIgnoreCase))
                maxPageMs = ParseDouble(arg[14..], "max-page-ms");
            else if (arg.StartsWith("--max-keyset-ms=", StringComparison.OrdinalIgnoreCase))
                maxKeysetSweepMs = ParseDouble(arg[16..], "max-keyset-ms");
            else if (arg.StartsWith("--max-lookup-p95-ms=", StringComparison.OrdinalIgnoreCase))
                maxLookupP95Ms = ParseDouble(arg[20..], "max-lookup-p95-ms");
            else if (arg.StartsWith("--max-filter-ms=", StringComparison.OrdinalIgnoreCase))
                maxFilterPageMs = ParseDouble(arg[16..], "max-filter-ms");
            else if (arg.StartsWith("--max-aggregate-ms=", StringComparison.OrdinalIgnoreCase))
                maxAggregateMs = ParseDouble(arg[19..], "max-aggregate-ms");
            else if (arg.StartsWith("--max-dashboard-month-ms=", StringComparison.OrdinalIgnoreCase))
                maxDashboardMonthMs = ParseDouble(arg[25..], "max-dashboard-month-ms");
            else if (arg.StartsWith("--max-dashboard-ms=", StringComparison.OrdinalIgnoreCase))
                maxDashboardMs = ParseDouble(arg[19..], "max-dashboard-ms");
            else if (arg.StartsWith("--max-work-order-page-ms=", StringComparison.OrdinalIgnoreCase))
                maxWorkOrderPageMs = ParseDouble(arg[25..], "max-work-order-page-ms");
            else if (arg.StartsWith("--max-search-ms=", StringComparison.OrdinalIgnoreCase))
                maxSearchMs = ParseDouble(arg[16..], "max-search-ms");
            else if (arg.StartsWith("--max-concurrent-ms=", StringComparison.OrdinalIgnoreCase))
                maxConcurrentMs = ParseDouble(arg[20..], "max-concurrent-ms");
            else if (arg.StartsWith("--max-working-set-mb=", StringComparison.OrdinalIgnoreCase))
                maxWorkingSetMb = ParseDouble(arg[21..], "max-working-set-mb");
            else if (string.Equals(arg, "--keep", StringComparison.OrdinalIgnoreCase))
                keep = true;
            else if (string.Equals(arg, "--generate-fixture", StringComparison.OrdinalIgnoreCase))
            {
                generateFixture = true;
                keep = true;
            }
            else if (string.Equals(arg, "--generate-only", StringComparison.OrdinalIgnoreCase))
            {
                generateOnly = true;
                stress = true;
                keep = true;
            }
            else if (string.Equals(arg, "--benchmark-only", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkOnly = true;
                stress = true;
                keep = true;
            }
            else if (string.Equals(arg, "--verify-existing-corpus", StringComparison.OrdinalIgnoreCase))
            {
                verifyExistingCorpus = true;
                keep = true;
            }
            else if (string.Equals(arg, "--reset", StringComparison.OrdinalIgnoreCase))
                reset = true;
            else if (string.Equals(arg, "--include-activity", StringComparison.OrdinalIgnoreCase))
                includeActivity = true;
            else if (string.Equals(arg, "--skip-backup-restore", StringComparison.OrdinalIgnoreCase))
                skipBackupRestore = true;
            else if (string.Equals(arg, "--million", StringComparison.OrdinalIgnoreCase))
                records = 1_000_000;
            else if (string.Equals(arg, "--stress", StringComparison.OrdinalIgnoreCase))
            {
                stress = true;
                records = 1_000_000;
            }
            else
            {
                throw new ArgumentException(
                    $"Unknown argument '{arg}'. Use --records=N, --million, --stress, --year=YYYY, --root=PATH, --keep, " +
                    "--generate-fixture, --generate-only, --benchmark-only, --verify-existing-corpus, --reset, --start-year=YYYY, --end-year=YYYY, --records-per-year=N, --total-records=N, --include-activity, " +
                    "--work-orders=N, --skip-backup-restore, --lookups=N, --workers=N, --queries-per-worker=N, or the --max-*-ms/--max-working-set-mb thresholds.");
            }
        }

        if (generateOnly && benchmarkOnly)
            throw new ArgumentException("--generate-only and --benchmark-only are mutually exclusive.");
        if (verifyExistingCorpus && (generateOnly || benchmarkOnly || reset || generateFixture))
            throw new ArgumentException("--verify-existing-corpus cannot be combined with generation, benchmark or reset modes.");
        if ((generateOnly || benchmarkOnly || verifyExistingCorpus || reset) && string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Persistent stress modes require --root=PATH.");

        DataPaths.ValidateYear(year);
        DataPaths.ValidateYear(startYear);
        DataPaths.ValidateYear(endYear);
        if (startYear > endYear)
            throw new ArgumentOutOfRangeException(nameof(args), $"--start-year ({startYear}) must be <= --end-year ({endYear}).");
        if (endYear - startYear + 1 > 50)
            throw new ArgumentOutOfRangeException(nameof(args), "Fixture generation is limited to 50 annual databases per run.");
        if (records is < 1 or > 9_999_999)
            throw new ArgumentOutOfRangeException(nameof(args), records, "--records must be between 1 and 9,999,999.");
        if (totalRecords is < 0 or > 99_999_999)
            throw new ArgumentOutOfRangeException(nameof(args), totalRecords, "--total-records must be between 1 and 99,999,999 when specified.");
        if (workOrderCount is < 0 or > 999_999)
            throw new ArgumentOutOfRangeException(nameof(args), workOrderCount, "--work-orders must be between 0 and 999,999 because WO references use six digits.");
        if (lookupIterations is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(args), lookupIterations, "--lookups must be between 1 and 10,000.");
        if (concurrentWorkers is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(args), concurrentWorkers, "--workers must be between 1 and 64.");
        if (queriesPerWorker is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(args), queriesPerWorker, "--queries-per-worker must be between 1 and 1,000.");

        return new Options(
            year, records, totalRecords, root, keep, stress, generateFixture, generateOnly, benchmarkOnly, verifyExistingCorpus, reset, startYear, endYear, includeActivity, workOrderCount, skipBackupRestore,
            lookupIterations, concurrentWorkers, queriesPerWorker, seed,
            maxPageMs, maxKeysetSweepMs, maxLookupP95Ms, maxFilterPageMs, maxAggregateMs,
            maxDashboardMonthMs, maxDashboardMs, maxWorkOrderPageMs, maxSearchMs, maxConcurrentMs, maxWorkingSetMb);
    }

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {name} value '{value}'.");

    private static long ParseLong(string value, string name) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {name} value '{value}'.");

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Invalid {name} value '{value}'.");
}
