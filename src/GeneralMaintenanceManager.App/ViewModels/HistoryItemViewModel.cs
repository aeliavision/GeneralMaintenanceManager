using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(MaintenanceRecord record, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localization);

        SourceMaintenanceRecord = record;
        HistoryKind = "Maintenance";
        ReferenceNumber = record.MaintenanceNumber;
        IsMaintenance = true;
        HasAudit = record.Revision > 1;
        AuditLabel = HasAudit
            ? localization.Format("HistoryEditedCountFormat", record.Revision - 1)
            : string.Empty;
        DateText = record.OccurredAt.ToString("dd MMM yyyy", localization.CurrentCulture);
        TypeText = record.IsInvalid
            ? localization.GetString("MaintenanceMarkedInvalid")
            : MaintenanceTextPresentation.LocalizeMaintenanceType(record.MaintenanceType, localization);
        Title = string.IsNullOrWhiteSpace(record.Subject) ? TypeText : record.Subject;
        Description = record.WorkPerformed;

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.PerformedBy))
            metadata.Add(localization.Format("HistoryMetaPerformedByFormat", record.PerformedBy));
        if (record.Cost.HasValue)
            metadata.Add(localization.Format("HistoryMetaCostFormat", record.Cost.Value));
        if (!string.IsNullOrWhiteSpace(record.CreatedBy))
            metadata.Add(localization.Format("HistoryMetaRecordedByFormat", record.CreatedBy));
        if (HasAudit) metadata.Add(AuditLabel);

        Meta = string.Join("   •   ", metadata);
        Notes = record.Notes;
        HasDescription = !string.IsNullOrWhiteSpace(Description);
        HasMeta = !string.IsNullOrWhiteSpace(Meta);
        HasNotes = !string.IsNullOrWhiteSpace(Notes);
    }

    public MaintenanceRecord SourceMaintenanceRecord { get; }
    public bool IsMaintenance { get; }
    public bool HasAudit { get; }
    public string AuditLabel { get; }
    public string HistoryKind { get; }
    public string ReferenceNumber { get; }
    public string DateText { get; }
    public string TypeText { get; }
    public string Title { get; }
    public string Description { get; }
    public string Meta { get; }
    public string Notes { get; }
    public bool HasDescription { get; }
    public bool HasMeta { get; }
    public bool HasNotes { get; }
}
