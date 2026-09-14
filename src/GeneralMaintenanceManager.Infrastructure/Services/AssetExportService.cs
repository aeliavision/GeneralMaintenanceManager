using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class AssetExportService(DatabaseContextFactory contextFactory) : IAssetExportService
{
    private static readonly string[] Headers =
    [
        "AssetNumber", "AssetName", "Category", "Site", "Location", "Manufacturer", "Country", "Model",
        "SerialNumber", "Condition", "OperationalStatus", "TechnicalSpecification", "PurchaseDate", "PurchasePrice",
        "InstallationDate", "WarrantyExpiryDate", "Notes"
    ];

    public async Task<int> ExportExcelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var assets = await LoadAssetsAsync(cancellationToken).ConfigureAwait(false);
        EnsureDestinationDirectory(filePath);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Assets");
        for (var i = 0; i < Headers.Length; i++) worksheet.Cell(1, i + 1).Value = Headers[i];
        var headerRange = worksheet.Range(1, 1, 1, Headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#6D101A");
        headerRange.Style.Font.FontColor = XLColor.White;
        worksheet.Row(1).Height = 22;
        worksheet.SheetView.FreezeRows(1);

        for (var index = 0; index < assets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteExcelRow(worksheet, index + 2, assets[index]);
        }
        if (assets.Length > 0) worksheet.Range(1, 1, assets.Length + 1, Headers.Length).SetAutoFilter();
        foreach (var column in new[] { 1, 2, 3, 4, 5, 8, 9, 10, 11, 12 }) worksheet.Column(column).Style.NumberFormat.Format = "@";
        worksheet.Columns(1, Headers.Length).AdjustToContents();
        for (var i = 1; i <= Headers.Length; i++) CapColumnWidth(worksheet, i, i is 12 or 17 ? 42 : 24);
        worksheet.Column(17).Style.Alignment.WrapText = true;
        workbook.SaveAs(filePath);
        return assets.Length;
    }

    public async Task<int> ExportCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var assets = await LoadAssetsAsync(cancellationToken).ConfigureAwait(false);
        EnsureDestinationDirectory(filePath);
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", Headers.Select(EscapeCsv))).ConfigureAwait(false);
        foreach (var item in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(",", GetExportValues(item).Select(EscapeCsv))).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return assets.Length;
    }

    private async Task<Asset[]> LoadAssetsAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.Asset.AsNoTracking().Where(item => !item.IsArchived)
            .OrderBy(item => item.AssetNumber == string.Empty).ThenBy(item => item.AssetNumber).ThenBy(item => item.AssetName)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteExcelRow(IXLWorksheet worksheet, int rowNumber, Asset asset)
    {
        var values = GetExportValues(asset);
        for (var i = 0; i < values.Length; i++) worksheet.Cell(rowNumber, i + 1).Value = values[i];
        if (asset.PurchasePrice.HasValue)
        {
            worksheet.Cell(rowNumber, 14).Value = asset.PurchasePrice.Value;
            worksheet.Cell(rowNumber, 14).Style.NumberFormat.Format = "#,##0.00";
        }
    }

    private static string[] GetExportValues(Asset asset) =>
    [
        string.IsNullOrWhiteSpace(asset.AssetNumber) ? asset.ImportedAssetNumber : asset.AssetNumber,
        asset.AssetName, asset.AssetCategory, asset.Site, asset.Location, asset.Manufacturer, asset.Country, asset.Model,
        asset.SerialNumber, asset.Condition, asset.OperationalStatus, asset.TechnicalSpecification,
        FormatDate(asset.PurchaseDate), asset.PurchasePrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        FormatDate(asset.InstallationDate), FormatDate(asset.WarrantyExpiryDate), asset.Notes
    ];

    private static string FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string EscapeCsv(string? value)
    {
        var text = NeutralizeSpreadsheetFormula(value ?? string.Empty);
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    internal static string NeutralizeSpreadsheetFormula(string value)
    {
        if (value.Length == 0) return value;
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') return "'" + value;
        return value;
    }
    private static void EnsureDestinationDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }
    private static void CapColumnWidth(IXLWorksheet worksheet, int columnNumber, double maxWidth)
    {
        var column = worksheet.Column(columnNumber); if (column.Width > maxWidth) column.Width = maxWidth;
    }
}
