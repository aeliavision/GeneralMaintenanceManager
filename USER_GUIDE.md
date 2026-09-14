# General Maintenance Manager — User Guide

**Application version:** 2.2.0  
**Platform:** Windows 10 / Windows 11 desktop  
**Product approach:** Maintenance-first • Location-first • Asset-optional

---

## 1. What General Maintenance Manager does

General Maintenance Manager uses a simple order-first maintenance workflow. You create the maintenance request first, keep it visible while the work is pending, and create the permanent Maintenance history only when the work is finished.

The normal workflow is:

1. Choose **Add Maintenance Order**.
2. Enter the location, subject, Maintenance Type, and problem/request.
3. The request appears in **Work Orders → Open**, which is the default Work Orders tab.
4. Open the order when the work is finished and choose **Mark Complete**.
5. Enter what was done and any completion details.
6. The completed order receives exactly one permanent MNT-backed Maintenance record, leaves **Open**, and appears under **Completed**.

A maintenance order can be location-only. Asset Tracking remains optional.

Pending orders are stored in the master database, not in an annual Maintenance database. This is intentional: an order created on 31 December and still open on 1 January remains the same pending order. Nothing is copied or duplicated. When it is completed, the permanent Maintenance record is written to the annual database for the **local completion year**.

---

## 2. Main navigation

The sidebar is grouped by what people normally do first:

- **Overview** — opens the Dashboard with current maintenance KPIs, recent completed Maintenance, open-order counts, due preventive work, and quick actions.
- **Work Orders** — the single workspace for all maintenance requests. It opens on **Open** by default and includes **Open**, **Completed**, **Cancelled**, and **All** tabs. The sidebar badge shows the current open-order count. Advanced Assign/Start/Hold/Resume controls can be enabled in Settings without creating a second workspace.
- **Maintenance Records** — the permanent completed-maintenance log. **Add Maintenance Order** starts a new open request; it does not immediately create permanent history.
  The record year is selected from the lower-left sidebar database panel. Maintenance search/date/location/type/performed-by filters use a compact layout; From Date and To Date start on today. A selected Location, Maintenance Type, or Performed By value stays visible while the filtered results refresh, so you can always see which filters are active.
- **Preventive Plans** — recurring maintenance schedules. The sidebar badge appears only when one or more plans are due.
- **Assets** — appears inside the Maintenance group only when Asset Tracking is enabled.
- **Reports & History** — search and review completed Maintenance, Work Orders, Preventive Maintenance, and activity history.
- **Settings** — system configuration, printing, backup/restore, and deliberate database maintenance.

The expanded sidebar groups these entries under **Overview**, **Maintenance**, **Reporting**, and **System**. It can be collapsed when you want more room for grids and forms; the compact rail keeps the same priority order and uses the same labels in tooltips.

There is no separate History page. Historical review is handled through **Reports & History** and the activity timeline shown on individual records.

---

## 3. Dashboard

The Dashboard summarizes current operations without loading the full historical database into the screen.

Typical information includes:

- Maintenance completed this month
- Maintenance cost this month
- Open work orders
- Preventive Maintenance due
- Recent completed Maintenance
- Active locations
- Common maintenance types
- Quick actions

Use **Add Maintenance Order** to create a new request. Use **Work Orders → Open** to finish or cancel existing requests.

---

## 4. Create and complete a Maintenance Order

Choose **Add Maintenance Order** from Maintenance or Dashboard.

### Information when creating the order

Required:

- Location
- Subject / what needs maintenance
- Maintenance Type
- Problem / request

Optional:

- Site / Building
- Priority
- Requested By
- Due Date
- Assigned To
- Notes
- Asset, when Asset Tracking is enabled

Creating the order assigns a permanent `WO-YYYY-NNNNNN` Work Order identity in the master database. It does **not** create an MNT yet.

### Open Work Orders

Every non-closed order appears in **Work Orders → Open**. From there you can:

- open/edit the order;
- print it;
- cancel it;
- mark it complete.

The simple maintenance workflow does not require Assign or Start before completion. Enable **Advanced Work Order Controls** in Settings when you need Assign, Start, Hold, or Resume; the Work Orders screen itself remains the same.

### Mark Complete

When the work is finished, select the order and choose **Mark Complete**. Record at least **Work performed**; you can also enter Performed By, cost, downtime, materials/parts, condition/status after the work, and completion notes.

Completion creates exactly one canonical Maintenance record and permanent reference such as:

`MNT-2027-0000001`

The seven-digit sequence is unique within its year. The MNT year is the **local calendar year when the order is completed**, not the year when the order was originally created.

Example: `WO-2026-000123` created on 31 December 2026 and completed on 2 January 2027 remains the same Work Order and produces an `MNT-2027-...` Maintenance record. There is no duplicate 2026 Maintenance row to move or delete.

After completion, the order leaves the **Open** tab, appears under **Completed**, and its MNT appears in Maintenance History/Report.

---

## 5. Maintenance workspace

The Maintenance workspace shows completed Maintenance and supports normal operational filtering.

The **Year** selector at the top of the Maintenance workspace lets you switch quickly between annual history files. It includes **All years**, the current year, and every archived year currently present in the app's `Data\Years` folder. Selecting a specific year keeps normal reads focused on that annual database instead of scanning unrelated years.

Common filters include:

- Year / All years
- Search text
- Date range
- Location
- Maintenance Type
- Performed By

The same year scope is shared with **Report**. Completed orders are written to the annual database for their completion year, so a new year appears in the selector as soon as the first Maintenance record for that year exists.

Double-click a row to open the complete record.

The Maintenance details window shows the record together with its activity/history timeline. Depending on the record state, available actions include:

- **Edit**
- **Print**
- **Mark Invalid**

### Editing a Maintenance record

Editing corrects the existing permanent record. It does not create a second Maintenance reference and cannot move the record into another MNT year.

Saving without making a meaningful change does not create unnecessary revision activity.

### Mark Invalid

Maintenance records are preserved for audit/history. If a record was entered in error, use **Mark Invalid** and provide the required reason instead of deleting it.

Invalid records remain historically available but are excluded from normal operational Maintenance counts and cost totals.

---

## 6. Find Maintenance by MNT

Use the MNT lookup in Maintenance or Report when you know the permanent reference.

Example:

`MNT-2026-0000001`

The year in the reference identifies the correct annual Maintenance history directly.

Where numeric-only lookup is offered, entering a sequence such as `1` uses the applicable selected/current year and resolves it to the full permanent MNT.

Always confirm the opened record before editing, invalidating, or printing it.

---

## 7. Work Orders

Every **Maintenance Order is backed by the Work Order engine**. There is one Work Orders workspace, with **Open**, **Completed**, **Cancelled**, and **All** tabs. **Open** is the default. Enable **Advanced Work Order Controls** in Settings only when you need assignment, Start, Hold, or Resume controls.

A Work Order can exist without an Asset.

Typical information includes:

- Location
- Subject
- Description
- Priority
- Assigned person/team
- Due date
- Optional Asset
- Optional relationship to a Preventive Maintenance plan

### Work Order lifecycle

For the simple Maintenance Order workflow, an open order may be completed directly. Assignment and Start are optional.

When **Advanced Work Order Controls** are enabled, the same Work Orders workspace also provides explicit lifecycle actions:

1. **Assign**
2. **Start**
3. **Put On Hold**, when required
4. **Resume**, for a held Work Order
5. **Complete**, when the work is finished
6. **Cancel**, when the Work Order should end without completion

A held Work Order must be resumed explicitly. **Start** is not used as a hidden Resume action.

Important lifecycle actions are recorded in activity/audit history.

### Completing a Work Order

Completing a Work Order creates exactly one permanent Maintenance record and assigns its permanent MNT.

After completion, the Work Order can take you to the generated MNT so you can:

- review it,
- edit/correct it when appropriate,
- mark it invalid when appropriate,
- print it.

If the application is interrupted during completion, its recovery mechanism is designed to continue the same completion instead of creating a second MNT.

---

## 8. Preventive Maintenance

Preventive Maintenance is for recurring planned work.

A Preventive Maintenance plan can be location-based and does not require an Asset.

Typical plan information includes:

- Location
- Subject
- Maintenance Type / task
- Frequency
- Next due date
- Assignment information
- Optional Asset

When a Preventive Maintenance plan generates a Work Order, the application prevents duplicate open Work Orders for the same plan.

After the generated Work Order is completed, the Preventive Maintenance schedule advances once. Retrying or recovering the completion must not advance it a second time.

Use pause/inactive behavior rather than deleting historical plans when the plan should no longer generate work.

---

## 9. Report

Report is the combined search/history workspace.

It can be used to review:

- Maintenance
- Work Orders
- Preventive Maintenance
- Operational/activity history

Use filters to narrow large histories before opening individual records. Report and Maintenance history load a bounded first window (100 rows) and show **Load more** when older matching rows remain. Filters are applied by the database before rows are loaded, so you do not need to wait for an entire multi-year history to be copied into memory. Use **Load more** repeatedly when you need older records.

Printing from a Report/Maintenance list prints the currently loaded visible rows. When more database rows remain, the printout identifies that it represents the loaded window rather than silently claiming to be the complete history.

Depending on the report item, double-clicking opens the appropriate details window.

For Maintenance, the canonical Maintenance details window is used for viewing, editing, marking invalid, and printing the permanent record.

---

## 10. Asset Tracking

Asset Tracking is optional and is disabled by default.

Enable it only when your maintenance operation benefits from identifying individual equipment/assets.

When enabled, the Asset workspace supports asset details and associated maintenance context while preserving the core rule:

**Maintenance does not require an Asset.**

Asset changes that affect operational state are recorded in audit history. An Asset cannot be archived while a live Work Order or active Preventive Maintenance plan still depends on it; complete/cancel/reassign the Work Order or pause/reassign the plan first. When duplicate Assets are merged through Review, active operational references are redirected to the survivor while historical Maintenance snapshots remain unchanged.

Asset import/review tools are available from the Settings/Asset workflow when required. Spreadsheet imports report malformed date/number/money cells instead of silently accepting them, and blank-number rows are not matched solely by their worksheet row position.

---

## 11. Activity and audit history

Important changes are recorded so you can understand what happened and when.

Examples include:

- Maintenance creation/edit/invalidation
- Work Order assignment and lifecycle changes
- Work Order completion
- Preventive Maintenance changes/generation/advancement
- Asset state changes
- Relevant review/import actions

Normal screens present user-friendly localized values instead of internal identifiers or enum names wherever possible.

---

## 12. English and Arabic

The application supports English and Arabic.

Change the language from **Settings**.

When Arabic is selected, supported screens and printed content use right-to-left presentation where applicable. If you notice clipped text, an untranslated label, or incorrect RTL layout during local use, record the screen and workflow so it can be corrected.

---

## 13. Backup

Use **Settings → Backup** to create a complete application backup.

A backup includes the operational database, annual Maintenance history databases, and required application data/settings. The backup package contains integrity information so damaged or modified backup files can be rejected during restore.

Recommended practice:

- create backups regularly;
- keep at least one copy outside the computer running the application;
- keep multiple generations instead of overwriting your only known-good backup;
- test restore periodically on a safe machine/test data location.

---

## 14. Restore

Use **Settings → Restore** and select a known-good application backup.

Restore is designed to validate the incoming backup before replacing current data. The current Data directory is quarantined during activation so the application can roll back if the restored data fails validation.

This design also allows restore to proceed when the current database itself is damaged, provided the selected backup is valid.

Do not manually modify files inside an application backup ZIP.

After a successful restore, restored settings should appear immediately without requiring you to manually reproduce them.

---

## 15. Printing

Individual canonical Maintenance records can be printed from their details window. Completed-order Maintenance prints preserve separate rows for Problem Details, Repair / Work Done, Order Notes, Materials / Parts Notes, and Repair Notes when those fields are enabled in the print profile.

Full asset-history printing is intentionally bounded to 2,000 Maintenance records to avoid building an unsafe oversized WPF print document. If an asset exceeds that limit, narrow the result in Reports by year/date and print the bounded report instead.

Printed Maintenance uses the permanent MNT reference and localized presentation values. Arabic printing uses right-to-left document direction where applicable.

Because printer drivers and Windows print environments vary, confirm the first printed page after installation or after changing printers.

---

## 16. Recommended everyday workflow

For most users, the simplest operating model is:

1. **New maintenance request?** Create an **Add Maintenance Order**.
2. **Work finished?** Open it in **Work Orders → Open** and choose **Complete Work Order**.
3. **Need advanced assignment/start/hold tracking?** Enable **Advanced Work Order Controls** in Settings.
4. **Repeats on a schedule?** Create Preventive Maintenance and let it generate orders.
5. **Need to find what happened?** Use Report or MNT lookup.
6. **Wrong historical record?** Mark the Maintenance record invalid instead of deleting it.
7. **Need equipment-specific tracking?** Enable Asset Tracking only if it is useful.
8. **Protect the history?** Create regular backups.

---

## 17. Data and troubleshooting basics

### Portable installation and data folder

GMM v2.1.x is a portable writable-folder application. Keep the whole application folder in a location where your Windows account can create and update files. Do **not** place the portable build under a protected folder such as `Program Files` unless the storage policy is deliberately changed by a developer.

Operational databases are stored under `Data`, backups under `Backups`, and bounded diagnostic logs under `Data\SystemLogs` beside the application. When moving the portable application, move the complete folder so its data and backups stay together.

### The application will not start

- Confirm Windows and the application installation are intact.
- If using a development/source build, follow the developer build instructions rather than manually moving runtime files.
- Do not delete application data as a first troubleshooting step if it contains real operational history.

### A completed Work Order does not appear finished after an interruption

Restart the application first. Completion recovery is designed to resume unfinished cross-database completion work using the already reserved permanent MNT.

Do not create a second Work Order merely to work around an interrupted completion without first checking the original record.

### A record is not visible in a large report

Review the report filters and selected year/date range. If the record is older than the currently loaded window, choose **Load more**. Filters/search are database-side, so narrowing the scope is usually faster than loading many broad pages.

### Backup restore fails

Do not modify the backup ZIP to force it to load. A validation failure can indicate missing files, a checksum mismatch, an incompatible schema, or a damaged archive. Use another known-good backup or have the backup reviewed by a developer/administrator.

---

## 18. Important product rules to remember

- Maintenance is **location-first**.
- Asset is **optional**.
- Every Maintenance record has one permanent MNT.
- Completed Work Order = exactly one permanent Maintenance record.
- Historical Maintenance is invalidated, not deleted.
- Work Orders use one tabbed workspace: Open, Completed, Cancelled, and All. Advanced controls add Assign/Start/Hold/Resume without adding another navigation destination.
- Preventive Maintenance should not create duplicate open Work Orders.
- Report is the unified historical/search workspace.
- Back up operational data regularly.

## Settings tabs and production maintenance

### General

The **General** tab contains everyday configuration only: application information, Optional Features beside Maintenance Types, and a compact Language & Layout card. The separate **Data & Backup** tab contains Inventory Data beside the Portable Data Folder, Backup beside Restore, and a compact Data & Storage status card. The tab strip uses one modern rounded navigation surface, and the selected tab color is limited to the tab header so page text remains readable. All commands and stored settings behave the same; the redesign only improves organization and readability.

### Print

The **Print** tab controls printing without changing operational data. You can enable automatic Work Order printing to the Windows default printer, set optional header/footer text, select a managed logo or center text, and choose which rows appear on Maintenance Orders, single Maintenance events, and list/report printouts. Use **Save print settings** to persist changes. Imported logos are copied into the portable `Data\PrintBranding` folder.

### Database & Maintenance

The **Database & Maintenance** tab shows schema, SQLite mode, database/WAL storage, recovery queue, Work Order search-index status, and Preventive due-marker status. It also provides explicit actions for `PRAGMA optimize`, `ANALYZE + optimize`, full integrity checking, Work Order search-index rebuilding, Activity History search-index rebuilding, and Review detection rebuilding. Activity History rebuilding is needed only when adopting an older pre-release master database whose historical activity has not yet been indexed; normal startup does not perform that large rebuild.

These operations are deliberately **not run during normal application startup**. Some can take several minutes on large databases. Let an operation finish and do not run another GMM copy against the same portable Data folder. GMM now prevents a second copy from using that Data folder at the same time.

## Fast startup and diagnostic log

GMM startup opens only the master database and the current-year database, checks lightweight schema identity/connectivity, replays only the small durable completion-recovery queue, loads the bounded first workspace, and shows the main window. Archived years and historical detail are loaded only when requested or after first paint. Full integrity checks, `ANALYZE`, FTS rebuilds, Review rebuilds, and archived-year scans are excluded from startup.

For troubleshooting, startup-stage timings are written to:

`Data\SystemLogs\startup-performance.log`

If startup ever feels slow, this log identifies which bounded stage consumed the time.


### Compact Reports screen
The Reports & History screen uses a more compact filter and summary layout so more report records remain visible. The bottom horizontal scrollbar has been removed; long cell text is trimmed within the available columns.

## Professional print documents

Printed Work Orders, Maintenance records, histories, reports, and Preventive Maintenance documents use a paper-first professional layout with structured sections and tables. Settings > Print controls which fields are shown or hidden, along with branding, logo/text, print date, and signature options.

