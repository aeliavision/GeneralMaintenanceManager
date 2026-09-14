using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class ReportsUiTests
{
    [TestMethod]
    public void ReportsUsesCompactFiltersCompactKpisAndNoHorizontalScrollbar()
    {
        var reportsView = ReadContract("ReportsView.xaml");

        StringAssert.Contains(reportsView, "Padding=\"12,8\"");
        StringAssert.Contains(reportsView, "Height=\"32\"");
        StringAssert.Contains(reportsView, "MinHeight=\"60\"");
        StringAssert.Contains(reportsView, "ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"");
        Assert.IsFalse(reportsView.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal));
    }

    private static string ReadContract(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "UiContracts", fileName);
        Assert.IsTrue(File.Exists(path), $"UI contract source was not copied: {path}");
        return File.ReadAllText(path);
    }
}
