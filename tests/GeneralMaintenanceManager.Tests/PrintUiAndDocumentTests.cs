namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class PrintUiAndDocumentTests
{
    [TestMethod]
    public void PrintSettingsUsesModernBrandingDropdownAndKeepsVisibilityControls()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "SettingsView.xaml"));

        StringAssert.Contains(xaml, "x:Key=\"PrintBrandingComboBoxStyle\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource PrintBrandingComboBoxStyle}\"");
        StringAssert.Contains(xaml, "PrintSettingsEditor.WorkOrder.ShowProblem");
        StringAssert.Contains(xaml, "PrintSettingsEditor.WorkOrder.ShowWorkPerformed");
        StringAssert.Contains(xaml, "PrintSettingsEditor.Maintenance.ShowProblem");
        StringAssert.Contains(xaml, "PrintSettingsEditor.Maintenance.ShowWorkPerformed");
        StringAssert.Contains(xaml, "PrintSettingsEditor.ListReport.ShowNotes");
    }

    [TestMethod]
    public void PrintServiceUsesStructuredProfessionalLayoutInsteadOfTextDividers()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "PrintService.cs"));

        StringAssert.Contains(source, "AddDocumentHeader(");
        StringAssert.Contains(source, "AddMetadataTable(");
        StringAssert.Contains(source, "AddSection(");
        StringAssert.Contains(source, "AddSignatureBlock(");
        StringAssert.Contains(source, "TableCell");
        Assert.IsFalse(source.Contains("────────────────", StringComparison.Ordinal),
            "Printed documents must use real borders/tables instead of Unicode divider text.");
        StringAssert.Contains(source, "if (layout.ShowProblem)");
        StringAssert.Contains(source, "if (layout.ShowWorkPerformed)");
        StringAssert.Contains(source, "if (layout.ShowNotes)");
    }

    [TestMethod]
    public void PrintServicePrivateHelpersUseConcreteTypesForAnalyzerCleanBuild()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "PrintService.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        StringAssert.Contains(source, "private static void AddMetadataTable(FlowDocument document, List<(string Label, string Value)> fields)");
        StringAssert.Contains(source, "private void AddMaintenanceActivityTable(FlowDocument document, MaintenanceActivity[] activities)");
        StringAssert.Contains(source, "private void AddActivityHistoryTable(FlowDocument document, ActivityHistoryEntry[] activities)");
        StringAssert.Contains(source, "private void AddMaintenanceListTable(FlowDocument document, MaintenanceRecord[] records, ListReportPrintLayout layout)");
        StringAssert.Contains(source, "List<string> headers,\n        IReadOnlyList<string[]> rows,\n        List<double> weights)");
        StringAssert.Contains(source, "private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)");
        StringAssert.Contains(source, "if (activities.Length == 0) return;");
        StringAssert.Contains(source, "if (records.Length == 0) return;");

        foreach (var helperHeader in new[]
        {
            "private void AddMaintenanceActivityTable(FlowDocument document, MaintenanceActivity[] activities)",
            "private void AddActivityHistoryTable(FlowDocument document, ActivityHistoryEntry[] activities)",
            "private void AddMaintenanceListTable(FlowDocument document, MaintenanceRecord[] records, ListReportPrintLayout layout)"
        })
        {
            var helperStart = source.IndexOf(helperHeader, StringComparison.Ordinal);
            Assert.IsTrue(helperStart >= 0, $"Expected array-backed print helper is missing: {helperHeader}");
            var helperWindow = source.Substring(helperStart, Math.Min(320, source.Length - helperStart));
            Assert.IsFalse(helperWindow.Contains("activities.Count == 0", StringComparison.Ordinal),
                $"Array-backed print helper must use Length, not Count: {helperHeader}");
            Assert.IsFalse(helperWindow.Contains("records.Count == 0", StringComparison.Ordinal),
                $"Array-backed print helper must use Length, not Count: {helperHeader}");
        }
    }
}
