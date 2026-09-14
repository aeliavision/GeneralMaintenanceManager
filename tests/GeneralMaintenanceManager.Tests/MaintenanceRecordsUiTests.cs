using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class MaintenanceRecordsUiTests
{
    [TestMethod]
    public void MaintenanceRecordsUsesSidebarYearCompactStableFiltersAndNoHorizontalScrollbar()
    {
        var mainWindow = ReadContract("MainWindow.xaml");
        var maintenanceView = ReadContract("MaintenanceView.xaml");
        var mainViewModel = ReadContract("MainViewModel.cs");

        StringAssert.Contains(mainWindow, "x:Name=\"SidebarYearSelector\"");
        StringAssert.Contains(mainWindow, "SelectedItem=\"{Binding SelectedYear, Mode=TwoWay}\"");
        Assert.IsFalse(maintenanceView.Contains("ItemsSource=\"{Binding Years}\"", StringComparison.Ordinal));

        StringAssert.Contains(maintenanceView, "ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"");
        StringAssert.Contains(maintenanceView, "Height=\"32\"");

        StringAssert.Contains(maintenanceView, "Text=\"{Binding MaintenanceLocationFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
        StringAssert.Contains(maintenanceView, "Text=\"{Binding MaintenanceTypeFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
        StringAssert.Contains(maintenanceView, "Text=\"{Binding MaintenancePerformedByFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
        Assert.IsFalse(maintenanceView.Contains("SelectedValue=\"{Binding MaintenanceLocationFilter", StringComparison.Ordinal));
        Assert.IsFalse(maintenanceView.Contains("SelectedValue=\"{Binding MaintenanceTypeFilter", StringComparison.Ordinal));
        Assert.IsFalse(maintenanceView.Contains("SelectedValue=\"{Binding MaintenancePerformedByFilter", StringComparison.Ordinal));

        StringAssert.Contains(mainViewModel, "private DateTime? _maintenanceFromDate = DateTime.Today;");
        StringAssert.Contains(mainViewModel, "private DateTime? _maintenanceToDate = DateTime.Today;");
    }

    [TestMethod]
    public void ActiveMaintenanceFilterKeepsItsSuggestionItemsSourceStableDuringReload()
    {
        var presentation = ReadContract("MainViewModel.Presentation.cs");

        StringAssert.Contains(presentation, "if (MaintenanceLocationFilter.Trim().Length == 0)");
        StringAssert.Contains(presentation, "if (MaintenanceTypeFilter.Trim().Length == 0)");
        StringAssert.Contains(presentation, "if (MaintenancePerformedByFilter.Trim().Length == 0)");
    }

    private static string ReadContract(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "UiContracts", fileName);
        Assert.IsTrue(File.Exists(path), $"UI contract source was not copied: {path}");
        return File.ReadAllText(path);
    }
}
