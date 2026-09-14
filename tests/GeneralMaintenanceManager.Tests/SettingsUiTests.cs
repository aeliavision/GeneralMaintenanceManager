namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class SettingsUiTests
{
    [TestMethod]
    public void SettingsUsesFourTabModernNavigationWithoutInheritingSelectedHeaderForeground()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "SettingsView.xaml"));

        StringAssert.Contains(xaml, "x:Key=\"ModernSettingsTabControlStyle\"");
        StringAssert.Contains(xaml, "x:Key=\"ModernSettingsTabItemStyle\"");
        StringAssert.Contains(xaml, "x:Name=\"SettingsHeaderSurface\"");
        StringAssert.Contains(xaml, "x:Name=\"TabHeaderText\"");
        StringAssert.Contains(xaml, "TargetName=\"TabHeaderText\" Property=\"Foreground\" Value=\"{StaticResource OnPrimaryBrush}\"");
        Assert.IsFalse(xaml.Contains("<Setter Property=\"Foreground\" Value=\"{StaticResource OnPrimaryBrush}\"/>", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "Header=\"{DynamicResource GeneralSettingsTab}\"");
        StringAssert.Contains(xaml, "Header=\"{DynamicResource DataBackupSettingsTab}\"");
        StringAssert.Contains(xaml, "Header=\"{DynamicResource PrintSettingsTab}\"");
        StringAssert.Contains(xaml, "Header=\"{DynamicResource DatabaseMaintenanceTab}\"");
    }

    [TestMethod]
    public void GeneralAndDataBackupSettingsAreSeparatedIntoPurposeBuiltLayouts()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "SettingsView.xaml"));

        StringAssert.Contains(xaml, "x:Name=\"OptionalFeaturesMaintenanceTypesRow\"");
        StringAssert.Contains(xaml, "x:Name=\"GeneralLanguageCard\"");
        StringAssert.Contains(xaml, "x:Name=\"DataBackupSettingsRoot\"");
        StringAssert.Contains(xaml, "x:Name=\"InventoryPortableRow\"");
        StringAssert.Contains(xaml, "x:Name=\"BackupRestoreRow\"");
        StringAssert.Contains(xaml, "x:Name=\"DataStorageCard\"");
    }
    [TestMethod]
    public void SettingsStyleTriggersAreNestedInStyleTriggersCollection()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContracts", "SettingsView.xaml"));

        StringAssert.Contains(xaml, "<Style.Triggers>");
        StringAssert.Contains(xaml, "<DataTrigger Binding=\"{Binding IsAssetTrackingEnabled}\" Value=\"False\">");
        Assert.IsFalse(
            xaml.Contains("<Setter Property=\"Grid.Column\" Value=\"2\"/>\r\n                                    <DataTrigger", StringComparison.Ordinal) ||
            xaml.Contains("<Setter Property=\"Grid.Column\" Value=\"2\"/>\n                                    <DataTrigger", StringComparison.Ordinal),
            "DataTrigger must be nested inside Style.Triggers; direct Style children are invalid WPF XAML.");
    }

}
