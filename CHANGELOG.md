# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.0] — 2026-05-15

### Added

- Initial public release of Koh Young KSMART AOI DB Converter
- Polls `dbo.TB_AOIPCB` and `dbo.TB_PcbToKsmart` tables from KSMART SQL Server database
- Maps AOI inspection and repair data to WATS UUT and UUR reports
- Configurable operation type codes for AOI Top, AOI Bottom, and Repair
- Timestamp offset parameter for timezone differences between AOI machine and WATS server
- Windows Forms GUI with Dashboard, Options dialog (Database, WATS, Process Codes, Advanced tabs)
- Auto-start on Windows startup option
- MSI installer with .NET 8 Desktop Runtime prerequisite detection
- `%LOCALAPPDATA%\Virinco\KSMART_AOI_DB\` for configuration and checkpoint storage
