# Koh Young KSMART AOI Converter

Converts Koh Young KSMART AOI inspection records from a SQL Server database to WATS UUT and repair reports.

## Integration Details

| Property | Value |
|----------|-------|
| **Category** | WATS Client converter |
| **Type** | Application converter |
| **Format** | KSMART SQL Server database |
| **Test type** | AOI, Optical Inspection |

## About

Koh Young KSMART is a smart factory solution for collecting and analyzing inspection and measurement data from production equipment. This converter polls KSMART AOI database tables, maps inspection and repair data to WATS reports, and submits the results through the WATS Client.

## Getting Started

* [What is WATS?](https://wats.com)
* [WATS Client download](https://wats.com/download)
* [Setting up a custom converter](https://support.wats.com/hc/en-us/articles/13344321749788-Setting-up-a-custom-converter)

## Resources

* [Koh Young KSMART Solutions](https://kohyoung.com/en/ksmart-solutions/)
* [WATS Documentation](https://support.wats.com)

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Installation](#installation)
3. [First-Time Configuration](#first-time-configuration)
4. [Running the Application](#running-the-application)
5. [Configuration Reference](#configuration-reference)
6. [Auto-Start on Boot](#auto-start-on-boot)
7. [Logging Options](#logging-options)
8. [Troubleshooting](#troubleshooting)
9. [Known Limitations](#known-limitations)

---

## Prerequisites

### 1. .NET 8 Desktop Runtime (x64)

The application requires the .NET 8 Desktop Runtime. If not already installed:

1. Go to: <https://dotnet.microsoft.com/download/dotnet/8.0>
2. Under **.NET Desktop Runtime 8.x**, download the **Windows x64** installer
3. Run the installer and follow the prompts

To verify:

```
dotnet --list-runtimes
```

Look for `Microsoft.WindowsDesktop.App 8.x.x`.

### 2. WATS Client

The WATS Client must be installed and connected to the WATS server.

1. Download from: <https://download.wats.com>
2. Install and configure the connection to your WATS server
3. Verify the WATS Client shows a green "Connected" status

The converter detects the WATS Client automatically via its API. A warning is shown in the Options → WATS tab if the client is not found.

### 3. Network Access

The machine running the converter needs network access to:

* The Koh Young KSMART SQL Server database (default port 1433)
* The WATS server (via the WATS Client)

### 4. Database

The converter targets the standard Koh Young KSMART database schema:

* Table `dbo.TB_AOIPCB` — main inspection records
* Table `dbo.TB_PcbToKsmart` — per-component JSON inspection results

The database user must have `SELECT` permission on both tables.

---

## Installation

1. Run `KSMART_AOI_DB-{version}-Setup.msi`
2. Accept the license agreement
3. Choose the installation directory (default: `C:\Program Files\Virinco\Koh Young KSMART AOI Converter\`)
4. Optionally select **"Start automatically on Windows startup"**
5. Click **Install**
6. The application appears in the Start Menu under **Koh Young KSMART AOI DB Converter**

---

## First-Time Configuration

Launch the application and open **File → Options** (`Ctrl+O`).

### Database Tab

| Field | Description |
|---|---|
| Server | SQL Server hostname or IP address (e.g. `192.168.1.50` or `MACHINE\SQLEXPRESS`) |
| Database | KSMART database name (e.g. `KSMART_DB`) |
| User | SQL login username |
| Password | SQL login password |
| Trust Server Certificate | Enable for self-signed certificates (typical in production environments) |

Click **Test Connection** on the Dashboard to verify connectivity before starting.

### WATS Tab

The WATS Client connection details are detected automatically. If the status shows ✅, the converter is ready to submit reports. If it shows ❌, install or reconfigure the WATS Client.

### Process Codes Tab

| Field | Description |
|---|---|
| AOI Top | WATS operation type code for top-side AOI inspections |
| AOI Bottom | WATS operation type code for bottom-side AOI inspections |
| Repair Operation | WATS operation type code for repair/rework (UUR reports) |

These codes must match the **Operation Types** configured in your WATS server. Ask your WATS administrator if unsure.

### Advanced Tab

| Field | Description |
|---|---|
| Timestamp Offset (h) | Hours to add to all timestamps from the database. Use if the AOI machine clock is in a different timezone than the WATS server. Set to `0` when both are in the same timezone. |
| Auto-start processing | Automatically begin polling when the application launches. |

---

## Running the Application

1. Launch **Koh Young KSMART AOI DB Converter** from the Start Menu
2. The Dashboard shows the current connection status
3. Click **Start** to begin polling
