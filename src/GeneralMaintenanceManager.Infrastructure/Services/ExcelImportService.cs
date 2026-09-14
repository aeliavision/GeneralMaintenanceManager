using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class ExcelImportService(
    DataPaths paths,
    DatabaseContextFactory contextFactory,
    IReviewService reviewService,
    SqliteMaintenanceService sqliteMaintenance) : IExcelImportService
{
    // External spreadsheet aliases intentionally accept common equipment/machine headers;
    // they are import vocabulary only and do not represent an internal legacy schema.
    private static readonly Dictionary<string, string[]> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AssetNumber"] = ["AssetNumber", "Asset Number", "Asset No", "Asset #", "No"],
        ["Site"] = ["Site", "Facility", "Hospital", "Hosp"],
        ["Location"] = ["Location", "Area", "Department", "Dpt"],
        ["AssetName"] = ["AssetName", "Asset Name", "EquipmentName", "Equipment Name", "MachineName", "Machine Name", "Name"],
        ["AssetCategory"] = ["AssetCategory", "Asset Category", "Category"],
        ["Manufacturer"] = ["Manufacturer", "Make", "Company"],
        ["Country"] = ["Country", "Origin"],
        ["Model"] = ["Model"],
        ["SerialNumber"] = ["SerialNumber", "Serial Number", "SerialNo", "Serial No", "Serial"],
        ["Condition"] = ["Condition", "Case"],
        ["OperationalStatus"] = ["OperationalStatus", "Operational Status", "Status"],
        ["TechnicalSpecification"] = ["TechnicalSpecification", "Technical Specification", "Specification", "Specs", "Voltage", "Volt"],
        ["PurchaseDate"] = ["PurchaseDate", "Purchase Date"],
        ["PurchasePrice"] = ["PurchasePrice", "Purchase Price", "Price"],
        ["InstallationDate"] = ["InstallationDate", "Installation Date", "Installed Date"],
        ["WarrantyExpiryDate"] = ["WarrantyExpiryDate", "Warranty Expiry Date", "Warranty Expiry", "WarrantyUntil"],
        ["Notes"] = ["Notes", "Note", "Note1", "Remarks"]
    };

    public async Task<ExcelImportResult> ImportAsync(
        string filePath,
        IProgress<ExcelImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected asset import file does not exist.", filePath);
        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .xlsx and .csv asset import files are supported.");

        Report(progress, ExcelImportStage.OpeningWorkbook, 2);
        var parsedRows = new List<AssetImportRow>();
        var issues = new List<ImportIssue>();
        var missingAssetNumber = 0;
        var invalidRows = 0;

        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            ParseCsv(filePath, parsedRows, issues, ref missingAssetNumber, ref invalidRows, progress, cancellationToken);
        }
        else
        {
            using var workbook = new XLWorkbook(filePath);
            ParseWorkbook(workbook, parsedRows, issues, ref missingAssetNumber, ref invalidRows, progress, cancellationToken);
        }

        Report(progress, ExcelImportStage.LoadingExistingRecords, 58, parsedRows.Count, parsedRows.Count);
        var added = 0; var updated = 0; var unchanged = 0; var duplicateAssetNumber = 0;
        var now = DateTimeOffset.UtcNow;

        await using (var context = contextFactory.CreateMasterDbContext())
        {
            var existing = await context.Asset.Where(item => !item.IsArchived).ToListAsync(cancellationToken).ConfigureAwait(false);
            var bySource = existing.Where(item => item.SourceRow > 0 && !string.IsNullOrWhiteSpace(item.SourceSheet))
                .GroupBy(item => SourceKey(item.SourceSheet, item.SourceRow), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byAsset = existing.Where(item => !string.IsNullOrWhiteSpace(item.AssetNumber))
                .GroupBy(item => item.AssetNumber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byStableBlankIdentity = existing
                .Where(item => string.IsNullOrWhiteSpace(item.AssetNumber))
                .Select(item => (Key: BuildStableBlankImportKey(item), Asset: item))
                .Where(item => item.Key.Length > 0)
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Asset, StringComparer.OrdinalIgnoreCase);
            var seenAssetsThisImport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Report(progress, ExcelImportStage.UpdatingDatabase, 62, 0, parsedRows.Count);
            for (var index = 0; index < parsedRows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = parsedRows[index];
                var sourceKey = SourceKey(row.Worksheet, row.RowNumber);
                Asset? asset = null;
                var sourceMatched = false;
                var stableBlankIdentity = row.AssetNumber.Length == 0 ? BuildStableBlankImportKey(row) : string.Empty;
                if (stableBlankIdentity.Length > 0 && byStableBlankIdentity.TryGetValue(stableBlankIdentity, out var stableAsset))
                {
                    asset = stableAsset;
                    sourceMatched = true;
                }
                else if (row.AssetNumber.Length > 0
                    && bySource.TryGetValue(sourceKey, out var sourceCandidate)
                    && CanMatchExistingBySource(sourceCandidate, row))
                {
                    asset = sourceCandidate;
                    sourceMatched = true;
                }
                var assetSeenBefore = row.AssetNumber.Length > 0 && !seenAssetsThisImport.Add(row.AssetNumber);

                if (!sourceMatched && row.AssetNumber.Length > 0 && !assetSeenBefore && byAsset.TryGetValue(row.AssetNumber, out var assetOwner))
                    asset = CanMatchExistingByAsset(assetOwner, row) ? assetOwner : null;

                if (asset is null)
                {
                    var canOwnAsset = row.AssetNumber.Length > 0 && !assetSeenBefore && !byAsset.ContainsKey(row.AssetNumber);
                    asset = new Asset
                    {
                        AssetId = Guid.NewGuid(), AssetNumber = canOwnAsset ? row.AssetNumber : string.Empty,
                        ImportedAssetNumber = row.AssetNumber, SourceSheet = row.Worksheet, SourceRow = row.RowNumber,
                        CreatedAtUtc = now, UpdatedAtUtc = now
                    };
                    ApplyImportedValues(asset, row, onlyWhenPresent: false);
                    context.Asset.Add(asset); existing.Add(asset); bySource[sourceKey] = asset;
                    if (stableBlankIdentity.Length > 0 && !byStableBlankIdentity.ContainsKey(stableBlankIdentity))
                        byStableBlankIdentity[stableBlankIdentity] = asset;
                    context.AssetAudit.Add(new AssetAudit
                    {
                        AuditId = Guid.NewGuid(),
                        AssetId = asset.AssetId,
                        Action = "Imported",
                        Reason = $"Imported from {row.Worksheet} row {row.RowNumber}.",
                        Changes = $"AssetNumber={asset.AssetNumber}; AssetName={asset.AssetName}; Location={asset.Location}",
                        ChangedBy = Environment.UserName,
                        ChangedAtUtc = now
                    });
                    if (canOwnAsset) byAsset[row.AssetNumber] = asset;
                    if (row.AssetNumber.Length > 0 && !canOwnAsset) duplicateAssetNumber++;
                    added++;
                }
                else
                {
                    if (asset.SourceRow == 0) { asset.SourceSheet = row.Worksheet; asset.SourceRow = row.RowNumber; bySource[sourceKey] = asset; }
                    if (asset.ImportedAssetNumber.Length == 0 && row.AssetNumber.Length > 0) asset.ImportedAssetNumber = row.AssetNumber;
                    var changedFields = GetChangedFields(asset, row);
                    if (changedFields.Count == 0) unchanged++;
                    else
                    {
                        ApplyImportedValues(asset, row, onlyWhenPresent: true);
                        asset.UpdatedAtUtc = now; updated++;
                        context.AssetAudit.Add(new AssetAudit
                        {
                            AuditId = Guid.NewGuid(),
                            AssetId = asset.AssetId,
                            Action = "ImportUpdated",
                            Reason = $"Updated from {row.Worksheet} row {row.RowNumber}.",
                            Changes = string.Join(", ", changedFields),
                            ChangedBy = Environment.UserName,
                            ChangedAtUtc = now
                        });
                    }
                }

                if (index == 0 || (index + 1) % 20 == 0 || index + 1 == parsedRows.Count)
                {
                    var percent = 62 + (int)Math.Round(25d * (index + 1) / Math.Max(1, parsedRows.Count), MidpointRounding.AwayFromZero);
                    Report(progress, ExcelImportStage.UpdatingDatabase, Math.Min(87, percent), index + 1, parsedRows.Count);
                }
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // The asset transaction is committed at this point. Post-commit maintenance must never
        // turn a successful import into a reported failure, because that would invite users to
        // retry changes that are already durable. Record any post-commit problem as an issue.
        var changedRows = added + updated;
        if (cancellationToken.IsCancellationRequested)
        {
            issues.Add(new ImportIssue("System", 0, string.Empty,
                "Asset changes were committed, but post-import optimization/review processing was skipped because cancellation was requested."));
        }
        else
        {
            if (changedRows >= SqliteMaintenanceService.OptimizeAfterBulkChangeThreshold)
            {
                try
                {
                    await sqliteMaintenance.OptimizeMasterAsync(changedRows, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    issues.Add(new ImportIssue("System", 0, string.Empty,
                        $"Asset changes were committed, but SQLite optimization could not complete: {ex.Message}"));
                }
            }

            Report(progress, ExcelImportStage.RecordingHistory, 91, parsedRows.Count, parsedRows.Count);
            try
            {
                await reviewService.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new ImportIssue("System", 0, string.Empty,
                    $"Asset changes were committed, but duplicate/review refresh could not complete: {ex.Message}"));
            }
        }

        Report(progress, ExcelImportStage.WritingReviewLog, 97, parsedRows.Count, parsedRows.Count);
        string? logPath = null;
        if (issues.Count > 0)
        {
            try
            {
                logPath = WriteIssueLog(filePath, issues);
            }
            catch (Exception ex)
            {
                // The database commit remains successful even if the optional issue log cannot be written.
                issues.Add(new ImportIssue("System", 0, string.Empty,
                    $"Asset changes were committed, but the import issue log could not be written: {ex.Message}"));
            }
        }
        Report(progress, ExcelImportStage.Completed, 100, parsedRows.Count, parsedRows.Count);
        return new ExcelImportResult(added, updated, unchanged, missingAssetNumber, duplicateAssetNumber, invalidRows, issues, logPath);
    }

    private static bool CanMatchExistingBySource(Asset asset, AssetImportRow row)
    {
        if (row.AssetNumber.Length == 0) return false;
        var incoming = NormalizeMatchValue(row.AssetNumber);
        return NormalizeMatchValue(asset.AssetNumber) == incoming || NormalizeMatchValue(asset.ImportedAssetNumber) == incoming;
    }

    private static string BuildStableBlankImportKey(AssetImportRow row) =>
        BuildStableBlankImportKey(row.AssetName, row.SerialNumber, row.Model, row.Manufacturer, row.Site, row.Location);

    private static string BuildStableBlankImportKey(Asset asset) =>
        BuildStableBlankImportKey(asset.AssetName, asset.SerialNumber, asset.Model, asset.Manufacturer, asset.Site, asset.Location);

    private static string BuildStableBlankImportKey(
        string assetName,
        string serialNumber,
        string model,
        string manufacturer,
        string site,
        string location)
    {
        var serial = NormalizeMatchValue(serialNumber);
        if (serial.Length > 0) return $"SERIAL|{serial}";
        var name = NormalizeMatchValue(assetName);
        if (name.Length == 0) return string.Empty;
        var modelKey = NormalizeMatchValue(model);
        var manufacturerKey = NormalizeMatchValue(manufacturer);
        var siteKey = NormalizeMatchValue(site);
        var locationKey = NormalizeMatchValue(location);
        if (modelKey.Length == 0 && manufacturerKey.Length == 0 && (siteKey.Length == 0 || locationKey.Length == 0))
            return string.Empty;
        return $"DESC|{name}|{modelKey}|{manufacturerKey}|{siteKey}|{locationKey}";
    }

    private static bool CanMatchExistingByAsset(Asset asset, AssetImportRow row)
    {
        var existingSerial = NormalizeMatchValue(asset.SerialNumber); var incomingSerial = NormalizeMatchValue(row.SerialNumber);
        if (existingSerial.Length > 0 && incomingSerial.Length > 0) return existingSerial == incomingSerial;
        if (!SameMatchValue(asset.AssetName, row.AssetName)) return false;
        var sameModel = SameNonBlankMatchValue(asset.Model, row.Model);
        var sameManufacturer = SameNonBlankMatchValue(asset.Manufacturer, row.Manufacturer);
        var sameLocation = SameNonBlankMatchValue(asset.Site, row.Site) && SameNonBlankMatchValue(asset.Location, row.Location);
        return sameModel || sameManufacturer || sameLocation;
    }

    private static bool SameNonBlankMatchValue(string? a, string? b)
    {
        var x = NormalizeMatchValue(a); var y = NormalizeMatchValue(b); return x.Length > 0 && y.Length > 0 && x == y;
    }
    private static bool SameMatchValue(string? a, string? b) => NormalizeMatchValue(a) == NormalizeMatchValue(b);
    private static string NormalizeMatchValue(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string SourceKey(string sheet, int row) => $"{sheet.Trim()}|{row.ToString(CultureInfo.InvariantCulture)}";

    private static IXLRow? FindHeaderRow(IEnumerable<IXLRow> rows) => rows.Take(10).FirstOrDefault(row =>
    {
        var headers = BuildHeaderMap(row);
        return HasHeader(headers, "AssetName") && (HasHeader(headers, "AssetNumber") || HasHeader(headers, "Site") || HasHeader(headers, "Location"));
    });

    private static Dictionary<string, int> BuildHeaderMap(IXLRow headerRow)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var header = NormalizeHeader(cell.GetFormattedString());
            if (header.Length > 0 && !result.ContainsKey(header)) result.Add(header, cell.Address.ColumnNumber);
        }
        return result;
    }

    private static string NormalizeHeader(string? header) => (header ?? string.Empty).Trim().Replace("_", " ", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal);

    private static bool HasHeader(Dictionary<string, int> headers, string canonical) => GetColumn(headers, canonical).HasValue;

    private static int? GetColumn(Dictionary<string, int> headers, string canonical)
    {
        if (!HeaderAliases.TryGetValue(canonical, out var aliases)) aliases = [canonical];
        foreach (var alias in aliases)
            if (headers.TryGetValue(NormalizeHeader(alias), out var column)) return column;
        return null;
    }

    private static bool IsBlankRow(IXLRow row) => row.CellsUsed().All(cell => string.IsNullOrWhiteSpace(cell.GetFormattedString()));

    private static string ReadText(IXLRow row, Dictionary<string, int> headers, string canonical)
    {
        var column = GetColumn(headers, canonical); return column.HasValue ? row.Cell(column.Value).GetFormattedString().Trim() : string.Empty;
    }

    private static decimal? ReadDecimal(
        IXLRow row,
        Dictionary<string, int> headers,
        string canonical,
        List<ImportIssue>? issues = null,
        string worksheet = "",
        string assetNumber = "")
    {
        var column = GetColumn(headers, canonical); if (!column.HasValue) return null;
        var cell = row.Cell(column.Value); if (cell.IsEmpty()) return null;
        decimal parsed;
        if (cell.TryGetValue<decimal>(out var numeric)) parsed = numeric;
        else
        {
            var text = cell.GetFormattedString().Trim();
            if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out parsed)
                && !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out parsed))
            {
                issues?.Add(new ImportIssue(worksheet, row.RowNumber(), assetNumber,
                    $"{canonical} value '{text}' is not a valid number; the value was not imported."));
                return null;
            }
        }
        return ValidateImportedMoney(parsed, canonical, worksheet, row.RowNumber(), assetNumber, issues);
    }

    private static DateOnly? ReadDate(
        IXLRow row,
        Dictionary<string, int> headers,
        string canonical,
        List<ImportIssue>? issues = null,
        string worksheet = "",
        string assetNumber = "")
    {
        var column = GetColumn(headers, canonical); if (!column.HasValue) return null;
        var cell = row.Cell(column.Value); if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<DateTime>(out var dateTime)) return DateOnly.FromDateTime(dateTime);
        var text = cell.GetFormattedString().Trim();
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dateTime)) return DateOnly.FromDateTime(dateTime);
        issues?.Add(new ImportIssue(worksheet, row.RowNumber(), assetNumber,
            $"{canonical} value '{text}' is not a valid date; the value was not imported."));
        return null;
    }

    private static string CombineNotes(IXLRow row, Dictionary<string, int> headers)
    {
        var values = new List<string>();
        foreach (var alias in HeaderAliases["Notes"])
        {
            if (!headers.TryGetValue(NormalizeHeader(alias), out var column)) continue;
            var value = NormalizeImportedNote(row.Cell(column).GetFormattedString());
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        }
        return string.Join(" | ", values);
    }

    private static string NormalizeImportedNote(string value)
    {
        var normalized = value.Trim(); return string.Equals(normalized, "ü", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private static void ApplyImportedValues(Asset asset, AssetImportRow row, bool onlyWhenPresent)
    {
        asset.Site = Choose(asset.Site, row.Site, onlyWhenPresent); asset.Location = Choose(asset.Location, row.Location, onlyWhenPresent);
        asset.AssetName = Choose(asset.AssetName, row.AssetName, onlyWhenPresent); asset.AssetCategory = Choose(asset.AssetCategory, row.AssetCategory, onlyWhenPresent);
        asset.Manufacturer = Choose(asset.Manufacturer, row.Manufacturer, onlyWhenPresent); asset.Country = Choose(asset.Country, row.Country, onlyWhenPresent);
        asset.Model = Choose(asset.Model, row.Model, onlyWhenPresent); asset.SerialNumber = Choose(asset.SerialNumber, row.SerialNumber, onlyWhenPresent);
        asset.Condition = Choose(asset.Condition, row.Condition, onlyWhenPresent);
        var incomingStatus = row.OperationalStatus.Length == 0 && !onlyWhenPresent ? "In Service" : row.OperationalStatus;
        asset.OperationalStatus = Choose(asset.OperationalStatus, incomingStatus, onlyWhenPresent);
        asset.TechnicalSpecification = Choose(asset.TechnicalSpecification, row.TechnicalSpecification, onlyWhenPresent);
        asset.Notes = Choose(asset.Notes, row.Notes, onlyWhenPresent); asset.SourceSheet = row.Worksheet; asset.SourceRow = row.RowNumber;
        if (!onlyWhenPresent || row.PurchaseDate.HasValue) asset.PurchaseDate = row.PurchaseDate;
        if (!onlyWhenPresent || row.PurchasePrice.HasValue) asset.PurchasePrice = row.PurchasePrice;
        if (!onlyWhenPresent || row.InstallationDate.HasValue) asset.InstallationDate = row.InstallationDate;
        if (!onlyWhenPresent || row.WarrantyExpiryDate.HasValue) asset.WarrantyExpiryDate = row.WarrantyExpiryDate;
    }

    private static List<string> GetChangedFields(Asset asset, AssetImportRow row)
    {
        var changes = new List<string>();
        AddImportedChange(changes, "site", asset.Site, row.Site); AddImportedChange(changes, "location", asset.Location, row.Location);
        AddImportedChange(changes, "asset name", asset.AssetName, row.AssetName); AddImportedChange(changes, "category", asset.AssetCategory, row.AssetCategory);
        AddImportedChange(changes, "manufacturer", asset.Manufacturer, row.Manufacturer); AddImportedChange(changes, "country", asset.Country, row.Country);
        AddImportedChange(changes, "model", asset.Model, row.Model); AddImportedChange(changes, "serial number", asset.SerialNumber, row.SerialNumber);
        AddImportedChange(changes, "condition", asset.Condition, row.Condition); AddImportedChange(changes, "operational status", asset.OperationalStatus, row.OperationalStatus);
        AddImportedChange(changes, "technical specification", asset.TechnicalSpecification, row.TechnicalSpecification); AddImportedChange(changes, "notes", asset.Notes, row.Notes);
        if (row.PurchaseDate.HasValue && asset.PurchaseDate != row.PurchaseDate) changes.Add("purchase date");
        if (row.PurchasePrice.HasValue && asset.PurchasePrice != row.PurchasePrice) changes.Add("price");
        if (row.InstallationDate.HasValue && asset.InstallationDate != row.InstallationDate) changes.Add("installation date");
        if (row.WarrantyExpiryDate.HasValue && asset.WarrantyExpiryDate != row.WarrantyExpiryDate) changes.Add("warranty expiry");
        return changes;
    }

    private static void AddImportedChange(List<string> changes, string label, string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming)) return;
        if (!string.Equals(current.Trim(), incoming.Trim(), StringComparison.Ordinal)) changes.Add(label);
    }

    private static string Choose(string current, string incoming, bool onlyWhenPresent) => onlyWhenPresent && string.IsNullOrWhiteSpace(incoming) ? current : incoming.Trim();

    private string WriteIssueLog(string sourceFilePath, List<ImportIssue> issues)
    {
        Directory.CreateDirectory(paths.ImportLogsDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = GetUniqueImportLogPath(timestamp);
        var builder = new StringBuilder(); builder.AppendLine("SourceFile,Worksheet,Row,AssetNumber,Reason");
        foreach (var issue in issues)
            builder.Append(Csv(Path.GetFileName(sourceFilePath))).Append(',').Append(Csv(issue.Worksheet)).Append(',')
                .Append(issue.RowNumber.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Csv(issue.AssetNumber)).Append(',').Append(Csv(issue.Reason)).AppendLine();
        File.WriteAllText(logPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); return logPath;
    }

    private string GetUniqueImportLogPath(string timestamp)
    {
        var candidate = Path.Combine(paths.ImportLogsDirectory, $"AssetImport-{timestamp}.csv"); if (!File.Exists(candidate)) return candidate;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            candidate = Path.Combine(paths.ImportLogsDirectory, $"AssetImport-{timestamp}_{suffix.ToString(CultureInfo.InvariantCulture)}.csv");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a unique import log file name.");
    }

    private static void ParseWorkbook(
        XLWorkbook workbook,
        List<AssetImportRow> parsedRows,
        List<ImportIssue> issues,
        ref int missingAssetNumber,
        ref int invalidRows,
        IProgress<ExcelImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var estimatedRowCount = Math.Max(1, workbook.Worksheets.Sum(ws => Math.Max(0, ws.RowsUsed().Count() - 1)));
        var processedRows = 0;
        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerRow = FindHeaderRow(worksheet.RowsUsed());
            if (headerRow is null)
            {
                issues.Add(new ImportIssue(worksheet.Name, 0, string.Empty, "No recognizable Asset Number / Asset Name header row was found."));
                continue;
            }

            var headers = BuildHeaderMap(headerRow);
            foreach (var row in worksheet.RowsUsed().Where(item => item.RowNumber() > headerRow.RowNumber()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedRows++;
                ReportReadProgress(progress, processedRows, estimatedRowCount);
                if (IsBlankRow(row)) continue;

                var assetNumber = ReadText(row, headers, "AssetNumber");
                var assetName = ReadText(row, headers, "AssetName");
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    invalidRows++;
                    issues.Add(new ImportIssue(worksheet.Name, row.RowNumber(), assetNumber, "Asset name is missing."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(assetNumber)) missingAssetNumber++;

                parsedRows.Add(new AssetImportRow(
                    assetNumber,
                    ReadText(row, headers, "Site"),
                    ReadText(row, headers, "Location"),
                    assetName,
                    ReadText(row, headers, "AssetCategory"),
                    ReadText(row, headers, "Manufacturer"),
                    ReadText(row, headers, "Country"),
                    ReadText(row, headers, "Model"),
                    ReadText(row, headers, "SerialNumber"),
                    ReadText(row, headers, "Condition"),
                    ReadText(row, headers, "OperationalStatus"),
                    ReadText(row, headers, "TechnicalSpecification"),
                    ReadDate(row, headers, "PurchaseDate", issues, worksheet.Name, assetNumber),
                    ReadDecimal(row, headers, "PurchasePrice", issues, worksheet.Name, assetNumber),
                    ReadDate(row, headers, "InstallationDate", issues, worksheet.Name, assetNumber),
                    ReadDate(row, headers, "WarrantyExpiryDate", issues, worksheet.Name, assetNumber),
                    CombineNotes(row, headers),
                    worksheet.Name,
                    row.RowNumber()));
            }
        }
    }

    private static void ParseCsv(
        string filePath,
        List<AssetImportRow> parsedRows,
        List<ImportIssue> issues,
        ref int missingAssetNumber,
        ref int invalidRows,
        IProgress<ExcelImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        const string worksheet = "CSV Import";
        using var parser = new TextFieldParser(filePath, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        Dictionary<string, int>? headers = null;
        var rowNumber = 0;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = parser.ReadFields() ?? [];
            rowNumber++;
            if (headers is null)
            {
                if (rowNumber > 10)
                {
                    issues.Add(new ImportIssue(worksheet, 0, string.Empty, "No recognizable Asset Number / Asset Name header row was found."));
                    return;
                }
                var candidate = BuildHeaderMap(fields);
                if (HasHeader(candidate, "AssetName") && (HasHeader(candidate, "AssetNumber") || HasHeader(candidate, "Site") || HasHeader(candidate, "Location")))
                    headers = candidate;
                continue;
            }

            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            ReportReadProgress(progress, parsedRows.Count + invalidRows + 1, 0);
            var assetNumber = ReadCsvText(fields, headers, "AssetNumber");
            var assetName = ReadCsvText(fields, headers, "AssetName");
            if (assetName.Length == 0)
            {
                invalidRows++;
                issues.Add(new ImportIssue(worksheet, rowNumber, assetNumber, "Asset name is missing."));
                continue;
            }
            if (assetNumber.Length == 0) missingAssetNumber++;

            parsedRows.Add(new AssetImportRow(
                assetNumber,
                ReadCsvText(fields, headers, "Site"),
                ReadCsvText(fields, headers, "Location"),
                assetName,
                ReadCsvText(fields, headers, "AssetCategory"),
                ReadCsvText(fields, headers, "Manufacturer"),
                ReadCsvText(fields, headers, "Country"),
                ReadCsvText(fields, headers, "Model"),
                ReadCsvText(fields, headers, "SerialNumber"),
                ReadCsvText(fields, headers, "Condition"),
                ReadCsvText(fields, headers, "OperationalStatus"),
                ReadCsvText(fields, headers, "TechnicalSpecification"),
                ReadCsvDate(fields, headers, "PurchaseDate", issues, worksheet, rowNumber, assetNumber),
                ReadCsvDecimal(fields, headers, "PurchasePrice", issues, worksheet, rowNumber, assetNumber),
                ReadCsvDate(fields, headers, "InstallationDate", issues, worksheet, rowNumber, assetNumber),
                ReadCsvDate(fields, headers, "WarrantyExpiryDate", issues, worksheet, rowNumber, assetNumber),
                CombineCsvNotes(fields, headers),
                worksheet,
                rowNumber));
        }

        if (headers is null)
            issues.Add(new ImportIssue(worksheet, 0, string.Empty, "No recognizable Asset Number / Asset Name header row was found."));
    }

    private static Dictionary<string, int> BuildHeaderMap(string[] fields)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Length; index++)
        {
            var header = NormalizeHeader(fields[index]);
            if (header.Length > 0 && !result.ContainsKey(header)) result.Add(header, index + 1);
        }
        return result;
    }

    private static string ReadCsvText(string[] fields, Dictionary<string, int> headers, string canonical)
    {
        var column = GetColumn(headers, canonical);
        return column.HasValue && column.Value <= fields.Length ? (fields[column.Value - 1] ?? string.Empty).Trim() : string.Empty;
    }

    private static decimal? ReadCsvDecimal(
        string[] fields,
        Dictionary<string, int> headers,
        string canonical,
        List<ImportIssue> issues,
        string worksheet,
        int rowNumber,
        string assetNumber)
    {
        var text = ReadCsvText(fields, headers, canonical);
        if (text.Length == 0) return null;
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var parsed)
            && !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out parsed))
        {
            issues.Add(new ImportIssue(worksheet, rowNumber, assetNumber,
                $"{canonical} value '{text}' is not a valid number; the value was not imported."));
            return null;
        }
        return ValidateImportedMoney(parsed, canonical, worksheet, rowNumber, assetNumber, issues);
    }

    private static DateOnly? ReadCsvDate(
        string[] fields,
        Dictionary<string, int> headers,
        string canonical,
        List<ImportIssue> issues,
        string worksheet,
        int rowNumber,
        string assetNumber)
    {
        var text = ReadCsvText(fields, headers, canonical);
        if (text.Length == 0) return null;
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime)) return DateOnly.FromDateTime(dateTime);
        issues.Add(new ImportIssue(worksheet, rowNumber, assetNumber,
            $"{canonical} value '{text}' is not a valid date; the value was not imported."));
        return null;
    }

    private static decimal? ValidateImportedMoney(
        decimal value,
        string canonical,
        string worksheet,
        int rowNumber,
        string assetNumber,
        List<ImportIssue>? issues)
    {
        try
        {
            ExactNumericStorage.ValidateMoney(value, canonical);
            return value;
        }
        catch (ArgumentException ex)
        {
            issues?.Add(new ImportIssue(worksheet, rowNumber, assetNumber,
                $"{canonical} value '{value.ToString(CultureInfo.InvariantCulture)}' is outside the supported money policy: {ex.Message}"));
            return null;
        }
    }

    private static string CombineCsvNotes(string[] fields, Dictionary<string, int> headers)
    {
        var values = new List<string>();
        foreach (var alias in HeaderAliases["Notes"])
        {
            if (!headers.TryGetValue(NormalizeHeader(alias), out var column) || column > fields.Length) continue;
            var value = NormalizeImportedNote(fields[column - 1] ?? string.Empty);
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        }
        return string.Join(" | ", values);
    }

    private static void ReportReadProgress(IProgress<ExcelImportProgress>? progress, int processedRows, int estimatedTotal)
    {
        if (estimatedTotal > 0)
        {
            var percent = 10 + (int)Math.Round(45d * Math.Min(processedRows, estimatedTotal) / estimatedTotal, MidpointRounding.AwayFromZero);
            Report(progress, ExcelImportStage.ReadingRows, Math.Min(55, percent), processedRows, estimatedTotal);
        }
        else if (processedRows == 1 || processedRows % 20 == 0)
        {
            Report(progress, ExcelImportStage.ReadingRows, 30, processedRows, 0);
        }
    }

    private static void Report(IProgress<ExcelImportProgress>? progress, ExcelImportStage stage, int percent, int processedRows = 0, int totalRows = 0) =>
        progress?.Report(new ExcelImportProgress(stage, Math.Clamp(percent, 0, 100), processedRows, totalRows));
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private sealed record AssetImportRow(
        string AssetNumber, string Site, string Location, string AssetName, string AssetCategory, string Manufacturer,
        string Country, string Model, string SerialNumber, string Condition, string OperationalStatus, string TechnicalSpecification,
        DateOnly? PurchaseDate, decimal? PurchasePrice, DateOnly? InstallationDate, DateOnly? WarrantyExpiryDate,
        string Notes, string Worksheet, int RowNumber);
}
