# General Maintenance Manager

**Application version:** 2.2.0  
**Platform:** Windows 10 / Windows 11 desktop  
**Product approach:** Maintenance-first • Location-first • Asset-optional

## What it does

General Maintenance Manager is a comprehensive Windows desktop application designed to streamline maintenance workflows. It uses a simple order-first maintenance workflow where you create the maintenance request first, keep it visible while the work is pending, and create the permanent Maintenance history only when the work is finished.

The core rule of the system is: **Maintenance does not require an Asset.**

## Features

- **Dashboard:** A quick overview of current maintenance KPIs, open work orders, preventive work due, and recent completed maintenance.
- **Work Orders Management:** A centralized workspace for tracking open, completed, and cancelled maintenance requests. Supports advanced controls like Assign, Start, Hold, and Resume.
- **Maintenance Records:** A permanent historical log of all completed maintenance. Separated by year to ensure fast loading times and efficient data management.
- **Preventive Plans:** Scheduling and tracking for recurring planned maintenance work.
- **Asset Tracking (Optional):** Can be enabled for operations that benefit from identifying individual equipment and assets, without changing the core requirement that maintenance doesn't strictly need an asset.
- **Reporting & History:** Powerful filtering and search to review past maintenance, work orders, preventive schedules, and system activity.
- **Multilingual Support:** Full support for both English and Arabic interfaces.
- **Professional Printing:** Built-in tools for printing canonical maintenance records, histories, reports, and preventive maintenance documents with customizable branding.
- **Robust Backup & Restore:** Complete application backups including operational databases and annual maintenance history databases with integrity validation.

## Local Workflow Model

1. **New maintenance request?** Create a new Maintenance Order.
2. **Work finished?** Complete the Work Order.
3. **Repeats on a schedule?** Create Preventive Maintenance to automatically generate orders.
4. **Need equipment tracking?** Enable Asset Tracking.
5. **Protect the history?** Create regular backups.

## Installation

This is a portable application for Windows 10/11. Keep the whole application folder in a location where your Windows account can create and update files (do not place it under protected folders like `Program Files` unless instructed). Operational data is stored in the `Data` directory.

## What are all these scripts for?

You've probably noticed there are a *lot* of PowerShell scripts sitting in the root folder (`.ps1` files). Don't worry, none of them are duplicates! The original developers built a really solid suite of tools to help build, test, and release this application. Here's a quick cheat sheet on what they all do:

### Building and Publishing
*   `build.ps1` - Your standard build script to compile the code.
*   `publish-win-x64.ps1` & `publish-windows.ps1` - These package everything up into a portable Windows app that you can actually run.
*   `package-source.ps1` - Zips up the source code for distribution.

### QA and Testing
*   `verify-maintenance-first.ps1` - This is a massive end-to-end test script. It runs through the whole "maintenance-first" workflow to make sure nobody accidentally broke the core logic.
*   `certify-production.ps1` & `certify-database-remediation.ps1` - These make sure the production database schema is solid and test the data migration and recovery processes.

### Stress Testing (Pushing it to the limit)
*   `stress-test.ps1` & `run-million-stress-test.ps1` - Exactly what they sound like! They hammer the app with up to a million fake records to make sure it doesn't slow down under pressure.
*   `run-scale-harness.ps1` - The conductor that orchestrates those stress tests.

### Development Utilities (Fake Data Generators)
*   `generate-live-test-database.ps1` & `install-live-test-database.ps1` - Generates and installs a massive, fake database so developers can test the UI with a lot of data.
*   `benchmark-live-test-database.ps1` - Tests the speed of that fake database.
*   `generate-multiyear-stress-databases.ps1` & `install-multiyear-stress-fixture.ps1` - Generates test data that spans across multiple years (which is super helpful since the app separates data by year).

## How to Run a Complete Local Test & Certification

If you want to manually build, run tests, and certify the application locally, you can use the following runbook. Open PowerShell and run these steps sequentially:

### 1. Preparation
Navigate to your source folder and allow scripts to run in your current session.
```powershell
cd "E:\Downloads\New folder\GeneralMaintenanceManager-v2.2.0-buildfix20-verifier-fix-final-clean-source"
Set-ExecutionPolicy -Scope Process Bypass
```

### 2. Static Source Verification
Verify the core maintenance logic and dependencies.
```powershell
.\verify-maintenance-first.ps1
```

### 3. Restore, Build, and Automated Tests
Compile the codebase and run all unit tests.
```powershell
.\build.ps1
```

### 4. Manual Testing
Run the application to verify the UI and features manually. **Close the application before continuing to the next steps.**
```powershell
dotnet run --project ".\src\GeneralMaintenanceManager.App\GeneralMaintenanceManager.App.csproj" --configuration Release
```

### 5. Generate Production-Scale Stress Database
Create a massive database for performance testing (1,000,000 Maintenance records and 100,000 Work Orders across 6 years).
```powershell
.\stress-test.ps1 -Reset -TotalRecords 1000000 -WorkOrders 100000 -IncludeActivity -IncludeBackup
```

### 6. Run Benchmark Stress Test
Re-run the stress test against the generated database to measure performance without regenerating data.
```powershell
.\stress-test.ps1 -BenchmarkOnly -TotalRecords 1000000 -WorkOrders 100000 -IncludeActivity -IncludeBackup
```

### 7. View Stress Report
Check the results of the stress tests.
```powershell
Get-Content ".\release\stress-runs\GMM-Full-Production\StressReport.txt"
```

### 8. Production Certification
Run the final suite to certify that the application is ready for production.
```powershell
.\certify-production.ps1
```

### 9. View Certification Results
Check if the certification passed.
```powershell
Get-Content ".\CERTIFICATION_RESULTS.txt"
```

### 10. Publish Windows Release
Generate the final binaries and deployment packages.
```powershell
.\publish-windows.ps1
```

### 11. View Release Files
Check the output directory for your compiled packages.
```powershell
Get-ChildItem ".\release" -Recurse | Select-Object FullName, Length
Get-ChildItem ".\release\packages" -Recurse -ErrorAction SilentlyContinue | Select-Object FullName, Length
```