using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Models;

public sealed record AssetDialogResult(AssetDraft? Draft, bool DeleteRequested)
{
    public static AssetDialogResult Save(AssetDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new AssetDialogResult(draft, DeleteRequested: false);
    }

    public static AssetDialogResult Delete() => new(Draft: null, DeleteRequested: true);
}
