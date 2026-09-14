using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.Services;

/// <summary>
/// Builds bounded, paper-first print documents from already-selected records. Large annual
/// datasets are never loaded here; query services return the bounded window. Visibility,
/// branding, footer and signature choices come from portable Print Settings.
/// </summary>
public sealed class PrintService(
    ILocalizationService localization,
    IUserSettingsService userSettingsService) : IPrintService
{
    private static readonly Brush PrintAccentBrush = CreateFrozenBrush(120, 15, 26);
    private static readonly Brush PrintAccentSoftBrush = CreateFrozenBrush(248, 241, 242);
    private static readonly Brush PrintBorderBrush = CreateFrozenBrush(211, 205, 202);
    private static readonly Brush PrintTextBrush = CreateFrozenBrush(25, 25, 25);
    private static readonly Brush PrintLabelBrush = CreateFrozenBrush(92, 86, 82);
    private static readonly Brush PrintAltSurfaceBrush = CreateFrozenBrush(248, 247, 245);

    public bool PrintAssetHistory(Asset asset, IReadOnlyList<MaintenanceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(records);
        var settings = userSettingsService.LoadPrintSettings();
        var layout = settings.ListReportLayout;
        var title = $"{DisplayAsset(asset)} - {localization.GetString("History")}";
        return Print(title, settings, layout.ShowPrintDate, showSignature: false, document =>
        {
            var metadata = new List<(string Label, string Value)>();
            AddMetadata(metadata, localization.GetString("PrintLocation"), Join(asset.Site, asset.Location), layout.ShowLocation);
            AddMetadata(metadata, localization.GetString("PrintModel"), asset.Model, show: true);
            AddMetadata(metadata, localization.GetString("PrintSerialNumber"), asset.SerialNumber, show: true);
            AddMetadataTable(document, metadata);
            AddMaintenanceListTable(document, records.OrderByDescending(item => item.OccurredAtUtcTicks).ToArray(), layout);
        });
    }

    public bool PrintWorkOrder(WorkOrder workOrder, Asset? asset, bool useDefaultPrinter = false)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        var settings = userSettingsService.LoadPrintSettings();
        var layout = settings.WorkOrderLayout;
        return Print(workOrder.WorkOrderNumber, settings, layout.ShowPrintDate, layout.ShowSignature, document =>
        {
            AddSection(document, localization.GetString("Subject"), workOrder.Title);

            var metadata = new List<(string Label, string Value)>();
            AddMetadata(metadata, localization.GetString("HistoryReference"), workOrder.WorkOrderNumber, layout.ShowOrderNumber);
            AddMetadata(metadata, localization.GetString("Location"), Join(workOrder.Site, workOrder.Location), layout.ShowLocation);
            AddMetadata(metadata, localization.GetString("Priority"), MaintenanceTextPresentation.LocalizeWorkOrderPriority(workOrder.Priority, localization), layout.ShowPriority);
            AddMetadata(metadata, localization.GetString("Status"), MaintenanceTextPresentation.LocalizeWorkOrderStatus(workOrder.Status, localization), layout.ShowStatus);
            AddMetadata(metadata, localization.GetString("Asset"), asset is null ? workOrder.AssetNumber : DisplayAsset(asset), layout.ShowAsset);
            AddMetadata(metadata, localization.GetString("PerformedBy"), workOrder.PerformedBy, layout.ShowPerformedBy);
            AddMetadata(metadata, localization.GetString("Cost"), workOrder.Cost?.ToString("N2", localization.CurrentCulture) ?? string.Empty, layout.ShowCost);
            if (layout.ShowOrderNumber && !string.IsNullOrWhiteSpace(workOrder.GeneratedMaintenanceNumber))
                AddMetadata(metadata, localization.GetString("Maintenance"), workOrder.GeneratedMaintenanceNumber, show: true);
            AddMetadataTable(document, metadata);

            if (layout.ShowProblem) AddSection(document, localization.GetString("ProblemDescription"), workOrder.ProblemDescription);
            if (layout.ShowWorkPerformed) AddSection(document, localization.GetString("WorkPerformedDone"), workOrder.WorkPerformed);
            if (layout.ShowNotes)
            {
                AddSection(document, localization.GetString("OrderNotes"), workOrder.Notes);
                AddSection(document, localization.GetString("MaterialsParts"), workOrder.MaterialsOrPartsNotes);
                AddSection(document, localization.GetString("RepairNotes"), workOrder.CompletionNotes);
            }
        }, useDefaultPrinter);
    }

    public bool PrintMaintenanceRecord(
        MaintenanceRecord record,
        IReadOnlyList<MaintenanceActivity> activities,
        WorkOrder? sourceWorkOrder = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(activities);
        var settings = userSettingsService.LoadPrintSettings();
        var layout = settings.MaintenanceLayout;
        var title = layout.ShowReference ? record.MaintenanceNumber : localization.GetString("Maintenance");
        return Print(title, settings, layout.ShowPrintDate, layout.ShowSignature, document =>
        {
            AddSection(document, localization.GetString("Subject"), record.Subject);
            AddMaintenance(document, record, layout, sourceWorkOrder);
            if (!layout.ShowAuditInformation || activities.Count == 0) return;
            AddMaintenanceActivityTable(document, activities.OrderBy(item => item.OccurredAtUtcTicks).ToArray());
        });
    }

    public bool PrintMaintenancePlan(MaintenancePlan plan, IReadOnlyList<ActivityHistoryEntry> activities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(activities);
        var settings = userSettingsService.LoadPrintSettings();
        var layout = settings.ListReportLayout;
        var title = layout.ShowReference ? plan.MaintenancePlanNumber : localization.GetString("PreventiveMaintenance");
        return Print(title, settings, layout.ShowPrintDate, showSignature: false, document =>
        {
            AddSection(document, localization.GetString("Subject"), plan.PlanName);
            var metadata = new List<(string Label, string Value)>();
            AddMetadata(metadata, localization.GetString("HistoryReference"), plan.MaintenancePlanNumber, layout.ShowReference);
            AddMetadata(metadata, localization.GetString("Location"), Join(plan.Site, plan.Location), layout.ShowLocation);
            AddMetadata(metadata, localization.GetString("Date"), plan.NextDueDate.ToString("d", localization.CurrentCulture), layout.ShowDate);
            AddMetadata(metadata, localization.GetString("Status"), plan.IsActive ? localization.GetString("Active") : localization.GetString("Inactive"), layout.ShowType);
            AddMetadata(metadata, localization.GetString("AssignedTo"), plan.AssignedTo, layout.ShowPerformedBy);
            AddMetadata(metadata, localization.GetString("Cost"), plan.EstimatedCost?.ToString("N2", localization.CurrentCulture) ?? string.Empty, layout.ShowCost);
            AddMetadataTable(document, metadata);
            if (!layout.ShowNotes) return;
            AddSection(document, localization.GetString("Description"), plan.Instructions);
            AddActivityHistoryTable(document, activities.OrderByDescending(item => item.OccurredAtUtc).ToArray());
        });
    }

    public bool PrintGlobalHistory(IReadOnlyList<GlobalHistoryItemViewModel> items, string scope)
    {
        ArgumentNullException.ThrowIfNull(items);
        var settings = userSettingsService.LoadPrintSettings();
        var layout = settings.ListReportLayout;
        return Print(scope, settings, layout.ShowPrintDate, showSignature: false, document =>
        {
            AddGlobalHistoryTable(document, items, layout);
        });
    }

    private bool Print(
        string title,
        PrintSettings settings,
        bool showPrintDate,
        bool showSignature,
        Action<FlowDocument> build,
        bool useDefaultPrinter = false)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.25,
            Foreground = PrintTextBrush,
            PagePadding = new Thickness(34),
            ColumnGap = 0,
            ColumnWidth = double.PositiveInfinity,
            FlowDirection = string.Equals(localization.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight
        };

        AddDocumentHeader(document, title, settings, showPrintDate);
        build(document);
        if (showSignature) AddSignatureBlock(document);
        AddFooter(document, settings.FooterText);

        var dialog = new PrintDialog();
        if (useDefaultPrinter)
        {
            using var server = new LocalPrintServer();
            dialog.PrintQueue = server.DefaultPrintQueue;
        }
        else if (dialog.ShowDialog() != true)
        {
            return false;
        }

        document.PageHeight = dialog.PrintableAreaHeight;
        document.PageWidth = dialog.PrintableAreaWidth;
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
        return true;
    }

    private void AddDocumentHeader(FlowDocument document, string title, PrintSettings settings, bool showPrintDate)
    {
        if (!string.IsNullOrWhiteSpace(settings.HeaderText))
        {
            document.Blocks.Add(new Paragraph(new Run(settings.HeaderText))
            {
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrintLabelBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 7)
            });
        }

        AddBrandingBlock(document, settings);

        var header = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14) };
        header.Columns.Add(new TableColumn { Width = new GridLength(1.8, GridUnitType.Star) });
        if (showPrintDate) header.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run(title))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = PrintAccentBrush,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(1, 3, showPrintDate ? 0 : 1, 1),
            Background = Brushes.White
        });
        if (showPrintDate)
        {
            row.Cells.Add(new TableCell(new Paragraph(new Run(DateTimeOffset.Now.ToString("f", localization.CurrentCulture)))
            {
                TextAlignment = TextAlignment.Right,
                FontSize = 8.8,
                Foreground = PrintLabelBrush,
                Margin = new Thickness(0)
            })
            {
                Padding = new Thickness(12, 9, 12, 9),
                BorderBrush = PrintBorderBrush,
                BorderThickness = new Thickness(1, 3, 1, 1),
                Background = Brushes.White
            });
        }
        group.Rows.Add(row);
        header.RowGroups.Add(group);
        document.Blocks.Add(header);
    }

    private static void AddBrandingBlock(FlowDocument document, PrintSettings settings)
    {
        var logoPath = settings.LogoPath;
        if (settings.CenterBrandingMode == PrintCenterBrandingMode.Logo
            && !string.IsNullOrWhiteSpace(logoPath)
            && File.Exists(logoPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            document.Blocks.Add(new BlockUIContainer(new Image
            {
                Source = bitmap,
                MaxHeight = 52,
                MaxWidth = 175,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            })
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
        else if (settings.CenterBrandingMode == PrintCenterBrandingMode.Text && !string.IsNullOrWhiteSpace(settings.CenterText))
        {
            document.Blocks.Add(new Paragraph(new Run(settings.CenterText))
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrintAccentBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
    }

    private static void AddMetadata(List<(string Label, string Value)> fields, string label, string? value, bool show)
    {
        if (show && !string.IsNullOrWhiteSpace(value)) fields.Add((label, value.Trim()));
    }

    private static void AddMetadataTable(FlowDocument document, List<(string Label, string Value)> fields)
    {
        if (fields.Count == 0) return;
        var columnCount = Math.Min(3, fields.Count);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
        for (var column = 0; column < columnCount; column++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        for (var index = 0; index < fields.Count; index += columnCount)
        {
            var row = new TableRow();
            for (var column = 0; column < columnCount && index + column < fields.Count; column++)
            {
                var field = fields[index + column];
                var cell = new TableCell
                {
                    Padding = new Thickness(9, 7, 9, 7),
                    BorderBrush = PrintBorderBrush,
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White
                };
                cell.Blocks.Add(new Paragraph(new Run(field.Label))
                {
                    FontSize = 7.8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrintLabelBrush,
                    Margin = new Thickness(0, 0, 0, 2)
                });
                cell.Blocks.Add(new Paragraph(new Run(field.Value))
                {
                    FontSize = 10.2,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrintTextBrush,
                    Margin = new Thickness(0)
                });
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
    }

    private static void AddSection(FlowDocument document, string title, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var heading = new TableRow();
        heading.Cells.Add(new TableCell(new Paragraph(new Run(title))
        {
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = PrintAccentBrush,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(10, 5, 10, 5),
            Background = PrintAccentSoftBrush,
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(1)
        });
        var body = new TableRow();
        body.Cells.Add(new TableCell(new Paragraph(new Run(value.Trim()))
        {
            FontSize = 10.3,
            Foreground = PrintTextBrush,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(10, 8, 10, 9),
            Background = Brushes.White,
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(1, 0, 1, 1)
        });
        group.Rows.Add(heading);
        group.Rows.Add(body);
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
    }

    private void AddMaintenance(
        FlowDocument document,
        MaintenanceRecord record,
        MaintenancePrintLayout layout,
        WorkOrder? sourceWorkOrder = null)
    {
        var metadata = new List<(string Label, string Value)>();
        AddMetadata(metadata, localization.GetString("HistoryReference"), record.MaintenanceNumber, layout.ShowReference);
        AddMetadata(metadata, localization.GetString("Date"), record.OccurredAt.ToLocalTime().ToString("g", localization.CurrentCulture), layout.ShowDate);
        AddMetadata(metadata, localization.GetString("MaintenanceType"), MaintenanceTextPresentation.LocalizeMaintenanceType(record.MaintenanceType, localization), layout.ShowMaintenanceType);
        AddMetadata(metadata, localization.GetString("Location"), Join(record.Site, record.Location), layout.ShowLocation);
        AddMetadata(metadata, localization.GetString("Asset"), Join(record.AssetNumberSnapshot, record.AssetNameSnapshot), layout.ShowAsset);
        AddMetadata(metadata, localization.GetString("PerformedBy"), record.PerformedBy, layout.ShowPerformedBy);
        AddMetadata(metadata, localization.GetString("Cost"), record.Cost?.ToString("N2", localization.CurrentCulture) ?? string.Empty, layout.ShowCost);
        if (layout.ShowReference && !string.IsNullOrWhiteSpace(record.WorkOrderNumberSnapshot))
            AddMetadata(metadata, localization.GetString("WorkOrders"), record.WorkOrderNumberSnapshot, show: true);
        AddMetadataTable(document, metadata);

        if (sourceWorkOrder is not null && layout.ShowProblem)
            AddSection(document, localization.GetString("ProblemDescription"), sourceWorkOrder.ProblemDescription);
        if (layout.ShowWorkPerformed) AddSection(document, localization.GetString("WorkPerformedDone"), record.WorkPerformed);
        if (layout.ShowNotes)
        {
            if (sourceWorkOrder is null)
            {
                AddSection(document, localization.GetString("Notes"), record.Notes);
            }
            else
            {
                AddSection(document, localization.GetString("OrderNotes"), sourceWorkOrder.Notes);
                AddSection(document, localization.GetString("MaterialsParts"), sourceWorkOrder.MaterialsOrPartsNotes);
                AddSection(document, localization.GetString("RepairNotes"), sourceWorkOrder.CompletionNotes);
            }
        }
        if (record.IsInvalid && layout.ShowAuditInformation)
            AddSection(document, localization.GetString("VoidReason"), record.InvalidReason);
    }

    private void AddMaintenanceActivityTable(FlowDocument document, MaintenanceActivity[] activities)
    {
        if (activities.Length == 0) return;
        var rows = activities.Select(activity => new[]
        {
            activity.OccurredAtUtc.ToLocalTime().ToString("g", localization.CurrentCulture),
            MaintenanceTextPresentation.LocalizeMaintenanceActivityType(activity.ActivityType, localization),
            activity.ChangedBy,
            MaintenanceTextPresentation.FormatMaintenanceActivityDescription(activity, localization)
        }).ToArray();
        AddSimpleTable(document,
            localization.GetString("Activity"),
            [localization.GetString("Date"), localization.GetString("Activity"), localization.GetString("PerformedBy"), localization.GetString("Description")],
            rows,
            [1.1, 1.0, 0.9, 2.2]);
    }

    private void AddActivityHistoryTable(FlowDocument document, ActivityHistoryEntry[] activities)
    {
        if (activities.Length == 0) return;
        var rows = activities.Select(activity => new[]
        {
            activity.OccurredAtUtc.ToLocalTime().ToString("g", localization.CurrentCulture),
            MaintenanceTextPresentation.LocalizeActivityType(activity.ActivityType, localization),
            activity.ChangedBy,
            activity.Description
        }).ToArray();
        AddSimpleTable(document,
            localization.GetString("Activity"),
            [localization.GetString("Date"), localization.GetString("Activity"), localization.GetString("PerformedBy"), localization.GetString("Description")],
            rows,
            [1.1, 1.0, 0.9, 2.2]);
    }

    private void AddMaintenanceListTable(FlowDocument document, MaintenanceRecord[] records, ListReportPrintLayout layout)
    {
        if (records.Length == 0) return;
        var headers = new List<string> { localization.GetString("Subject") };
        var weights = new List<double> { 1.8 };
        if (layout.ShowReference) { headers.Add(localization.GetString("HistoryReference")); weights.Add(1.2); }
        if (layout.ShowDate) { headers.Add(localization.GetString("Date")); weights.Add(1.0); }
        if (layout.ShowType) { headers.Add(localization.GetString("MaintenanceType")); weights.Add(1.0); }
        if (layout.ShowLocation) { headers.Add(localization.GetString("Location")); weights.Add(1.4); }
        if (layout.ShowPerformedBy) { headers.Add(localization.GetString("PerformedBy")); weights.Add(1.0); }
        if (layout.ShowCost) { headers.Add(localization.GetString("Cost")); weights.Add(0.8); }
        if (layout.ShowNotes) { headers.Add(localization.GetString("Notes")); weights.Add(1.5); }

        var rows = new List<string[]>();
        foreach (var record in records)
        {
            var values = new List<string> { record.Subject };
            if (layout.ShowReference) values.Add(record.MaintenanceNumber);
            if (layout.ShowDate) values.Add(record.OccurredAt.ToLocalTime().ToString("g", localization.CurrentCulture));
            if (layout.ShowType) values.Add(MaintenanceTextPresentation.LocalizeMaintenanceType(record.MaintenanceType, localization));
            if (layout.ShowLocation) values.Add(Join(record.Site, record.Location));
            if (layout.ShowPerformedBy) values.Add(record.PerformedBy);
            if (layout.ShowCost) values.Add(record.Cost?.ToString("N2", localization.CurrentCulture) ?? string.Empty);
            if (layout.ShowNotes) values.Add(record.Notes);
            rows.Add(values.ToArray());
        }
        AddSimpleTable(document, null, headers, rows, weights);
    }

    private void AddGlobalHistoryTable(FlowDocument document, IReadOnlyList<GlobalHistoryItemViewModel> items, ListReportPrintLayout layout)
    {
        if (items.Count == 0) return;
        var headers = new List<string> { localization.GetString("Subject") };
        var weights = new List<double> { 1.8 };
        if (layout.ShowReference) { headers.Add(localization.GetString("HistoryReference")); weights.Add(1.2); }
        if (layout.ShowDate) { headers.Add(localization.GetString("Date")); weights.Add(1.0); }
        if (layout.ShowType) { headers.Add(localization.GetString("Activity")); weights.Add(1.0); }
        if (layout.ShowLocation) { headers.Add(localization.GetString("Location")); weights.Add(1.4); }
        if (layout.ShowPerformedBy) { headers.Add(localization.GetString("PerformedBy")); weights.Add(1.0); }
        if (layout.ShowCost) { headers.Add(localization.GetString("Cost")); weights.Add(0.8); }
        if (layout.ShowNotes) { headers.Add(localization.GetString("Notes")); weights.Add(1.6); }

        var rows = new List<string[]>();
        foreach (var item in items)
        {
            var values = new List<string> { item.Title };
            if (layout.ShowReference) values.Add(item.ReferenceNumber);
            if (layout.ShowDate) values.Add(item.DateText);
            if (layout.ShowType) values.Add(item.TypeText);
            if (layout.ShowLocation) values.Add(item.LocationDisplay);
            if (layout.ShowPerformedBy) values.Add(item.PerformedBy);
            if (layout.ShowCost) values.Add(item.CostText);
            if (layout.ShowNotes) values.Add(Join(item.Description, item.Notes));
            rows.Add(values.ToArray());
        }
        AddSimpleTable(document, null, headers, rows, weights);
    }

    private static void AddSimpleTable(
        FlowDocument document,
        string? sectionTitle,
        List<string> headers,
        IReadOnlyList<string[]> rows,
        List<double> weights)
    {
        if (headers.Count == 0 || rows.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(sectionTitle)) AddSectionHeading(document, sectionTitle);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
        for (var index = 0; index < headers.Count; index++)
        {
            var weight = index < weights.Count ? weights[index] : 1;
            table.Columns.Add(new TableColumn { Width = new GridLength(weight, GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        var headerRow = new TableRow();
        foreach (var header in headers)
            headerRow.Cells.Add(CreateTableCell(header, isHeader: true, background: PrintAccentBrush));
        group.Rows.Add(headerRow);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            var background = rowIndex % 2 == 0 ? Brushes.White : PrintAltSurfaceBrush;
            var values = rows[rowIndex];
            for (var column = 0; column < headers.Count; column++)
            {
                var value = column < values.Length ? values[column] : string.Empty;
                row.Cells.Add(CreateTableCell(value, isHeader: false, background));
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
    }

    private static TableCell CreateTableCell(string? text, bool isHeader, Brush background)
    {
        var paragraph = new Paragraph(new Run(text ?? string.Empty))
        {
            FontSize = isHeader ? 8.2 : 8.8,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isHeader ? Brushes.White : PrintTextBrush,
            Margin = new Thickness(0)
        };
        return new TableCell(paragraph)
        {
            Padding = new Thickness(6, 5, 6, 5),
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(0.5),
            Background = background
        };
    }

    private static void AddSectionHeading(FlowDocument document, string title)
    {
        document.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrintAccentBrush,
            Margin = new Thickness(0, 5, 0, 7)
        });
    }

    private void AddSignatureBlock(FlowDocument document)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 14, 0, 4) };
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run(localization.GetString("PrintSignature")))
        {
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrintLabelBrush,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(8, 8, 8, 16),
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        });
        row.Cells.Add(new TableCell(new Paragraph(new Run("________________________________"))
        {
            FontSize = 10,
            Foreground = PrintTextBrush,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(8, 8, 8, 16),
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        });
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
    }

    private static void AddFooter(FlowDocument document, string? footerText)
    {
        if (string.IsNullOrWhiteSpace(footerText)) return;
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 10, 0, 0) };
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run(footerText.Trim()))
        {
            FontSize = 8.5,
            Foreground = PrintLabelBrush,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0)
        })
        {
            Padding = new Thickness(8, 8, 8, 0),
            BorderBrush = PrintBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        });
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static string DisplayAsset(Asset asset) =>
        string.IsNullOrWhiteSpace(asset.AssetNumber) ? asset.AssetName : $"#{asset.AssetNumber} - {asset.AssetName}";

    private static string Join(params string?[] values) =>
        string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
}
