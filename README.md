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
