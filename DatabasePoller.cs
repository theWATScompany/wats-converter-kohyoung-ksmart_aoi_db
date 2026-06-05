using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.KohYoung
{
    // ================================================================
    // JSON model for TB_PcbToKsmart.JSONData
    // ================================================================

    public class KsmartResult
    {
        public KsmartResultData? ResultData { get; set; }
    }

    public class KsmartResultData
    {
        public string? PcbModel { get; set; }
        public string? PcbResult { get; set; }
        public string? PcbID { get; set; }
        public string? PcbGuid { get; set; }
        public string? Barcode { get; set; }
        public string? User { get; set; }
        public string? UserName { get; set; }
        public string? RunMode { get; set; }
        public string? MachineID { get; set; }
        public string? MachineIP { get; set; }
        public string? PcbStartTime { get; set; }
        public string? JudgmentStartTime { get; set; }
        public string? JudgmentTime { get; set; }
        public List<PanelInfoItem>? PanelInfo { get; set; }
        public List<ComponentResult>? PassComp { get; set; }
        public List<ComponentResult>? NgComp { get; set; }
        public List<ComponentResult>? IgnoreList { get; set; }
    }

    public class PanelInfoItem
    {
        public string? PanelIndex { get; set; }
        public string? PanelBarcode { get; set; }
    }

    public class ComponentResult
    {
        public string? PkgName { get; set; }
        public string? CompName { get; set; }
        public string? PartName { get; set; }
        public string? InspCondName { get; set; }
        public string? InspNgData { get; set; }
        public string? NgDetail { get; set; }
        public string? CompGuid { get; set; }
        public string? Array { get; set; }
        public string? InspCondRev { get; set; }
        public string? Result { get; set; }
    }

    // ================================================================
    // Main poller
    // ================================================================

    /// <summary>
    /// Polls dbo.TB_AOIPCB (joined with TB_PcbToKsmart for inspection JSON)
    /// for completed AOI records, creates WATS UUT reports for inspections
    /// and UUR (Unit Under Repair) reports for repairs, then submits them.
    /// </summary>
    public class DatabasePoller
    {
        private readonly AppConfig _config;
        private Checkpoint _checkpoint;
        private TDM? _api;

        // WATS test operation codes (from config)
        private string OP_AOI_TOP => _config.ProcessCodeAoiTop;
        private string OP_AOI_BOTTOM => _config.ProcessCodeAoiBottom;
        private string OP_AOI_REWORK => _config.ProcessCodeRepair;

        // TB_FailureType result codes
        private const int RESULT_GOOD = 11000000;
        private const int RESULT_PASS = 12000000;
        private const int RESULT_NG = 13000000;

        // Cached lookups
        private RepairType? _repairType;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public event Action<string>? OnLog;
        public event Action<int>? OnBatchCompleted;

        public DatabasePoller(AppConfig config)
        {
            _config = config;
            _checkpoint = Checkpoint.Load(_config.ResolveDataPath(_config.CheckpointFile));
        }

        // ----------------------------------------------------------------
        // Connection helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Tests the database connection. Returns null on success, or an error message.
        /// </summary>
        public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                await using var conn = new SqlConnection(_config.BuildConnectionString());
                await conn.OpenAsync(ct);
                Log("Database connection successful.");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Lists all databases on the server (for GUI picker).</summary>
        public async Task<List<string>> ListDatabasesAsync(CancellationToken ct = default)
        {
            var result = new List<string>();
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT name FROM sys.databases ORDER BY name", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetString(0));
            return result;
        }

        /// <summary>Lists user tables in the configured database.</summary>
        public async Task<List<string>> ListTablesAsync(CancellationToken ct = default)
        {
            var result = new List<string>();
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                "WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetString(0));
            return result;
        }

        // ----------------------------------------------------------------
        // Import loop
        // ----------------------------------------------------------------

        /// <summary>
        /// Initialises the WATS API and caches repair type lookup.
        /// </summary>
        public void InitializeApi()
        {
            _api = new TDM();
            if (_config.OfflineMode || string.IsNullOrWhiteSpace(_config.WATSServerUrl))
            {
                _api.InitializeAPI(true);
                Log("WATS API initialised in OFFLINE mode.");
            }
            else
            {
                _api.SetupAPI(null, _config.WATSServerUrl, _config.WATSApiToken, true);
                _api.InitializeAPI(false);
                Log($"WATS API initialised â†’ {_config.WATSServerUrl}");
            }

            // Look up repair type for UUR creation (code 5555)
            try
            {
                short reworkCode = short.Parse(OP_AOI_REWORK);
                _repairType = _api.GetRepairTypes()
                    .FirstOrDefault(rt => rt.Code == reworkCode);

                if (_repairType != null)
                    Log($"Repair type '{_repairType.Name}' (code {_repairType.Code}) loaded for UUR reports.");
                else
                    Log($"WARNING: Repair type code {OP_AOI_REWORK} not found. UUR reports will be skipped until configured.");
            }
            catch (Exception ex)
            {
                Log($"WARNING: Could not look up repair types: {ex.Message}. UUR reports will be skipped.");
            }
        }

        /// <summary>
        /// Runs the polling loop until cancellation is requested.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            InitializeApi();
            Log($"Polling every {_config.PollIntervalSeconds}s  |  Batch size {_config.BatchSize}");
            Log($"Checkpoint: PCBID > {_checkpoint.LastId}");

            if (TEST_MODE)
            {
                Log($"âš ï¸  TEST MODE: will fetch ALL JSON records + latest {TEST_MODE_NO_JSON_COUNT} non-JSON records ONCE then stop.");
                try
                {
                    int count = await ImportBatchAsync(ct);
                    Log($"âœ… TEST MODE complete: imported {count} report(s). Stopped. Press Start to run again.");
                    OnBatchCompleted?.Invoke(count);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"ERROR: {ex.Message}"); }
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int count = await ImportBatchAsync(ct);
                    if (count > 0)
                    {
                        Log($"Imported {count} report(s).  Checkpoint â†’ PCBID {_checkpoint.LastId}");
                        OnBatchCompleted?.Invoke(count);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"ERROR: {ex.Message}");
                }

                try { await Task.Delay(_config.PollIntervalSeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // ----------------------------------------------------------------
        // Single batch
        // ----------------------------------------------------------------

        // â”€â”€ TEST MODE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Set to true to fetch the latest 100 records every poll cycle
        // (ignores checkpoint). Set back to false for production.
        private const bool TEST_MODE = false;
        private const int TEST_MODE_NO_JSON_COUNT = 2000; // latest N records without JSON for test pass 2
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// SQL query joins TB_AOIPCB with TB_PcbToKsmart (via PCBGUID) to
        /// include per-component inspection JSON data.
        /// Uses OUTER APPLY for initial JSON (Type=0) so ALL records are returned,
        /// including boards without component-level data (passing boards).
        /// Uses OUTER APPLY for review JSON (Type=1) since it's optional.
        /// InitialJSON has the original AOI scan results (NgComp populated).
        /// ReviewJSON has post-operator-review results (NgComp usually cleared).
        /// </summary>
        private const string BATCH_SQL = @"
            SELECT TOP(@batchSize)
                a.PCBID, a.BarCode, a.ALLBarCode, a.PCBModel, a.Lot, a.MachineID,
                a.StartDateTime, a.EndDateTime, a.UserID,
                a.PCBResultBefore, a.PCBResultAfter, a.PCBResultRepair,
                a.PCBTotalComp, a.PCBTotalInsp, a.TB, a.Lane, a.ArrayCnt,
                a.RepairStartDateTime, a.RepairEndDateTime,
                a.RepairUserID, a.ReviewUserID,
                a.ResultDBName, a.JobFileIDLocal, a.PCBComment,
                k_initial.JSONData  AS InitialJSON,
                k_review.JSONData   AS ReviewJSON
            FROM dbo.TB_AOIPCB a
            OUTER APPLY (
                SELECT TOP 1 CAST(k2.JSONData AS NVARCHAR(MAX)) AS JSONData
                FROM dbo.TB_PcbToKsmart k2
                WHERE k2.PCBGUID = a.PCBGUID AND k2.Type = 0
                ORDER BY k2.ID DESC
            ) k_initial
            OUTER APPLY (
                SELECT TOP 1 CAST(k2.JSONData AS NVARCHAR(MAX)) AS JSONData
                FROM dbo.TB_PcbToKsmart k2
                WHERE k2.PCBGUID = a.PCBGUID AND k2.Type = 1
                ORDER BY k2.ID DESC
            ) k_review
            WHERE a.PCBID > @lastId
              AND a.SaveDone = 1
            ORDER BY a.PCBID";

        /// <summary>
        /// Test-mode SQL pass 1: fetches ALL records that have component-level JSON in
        /// TB_PcbToKsmart (CROSS APPLY so only the ~615 matched rows are returned).
        /// </summary>
        private const string TEST_SQL_JSON = @"
            SELECT
                a.PCBID, a.BarCode, a.ALLBarCode, a.PCBModel, a.Lot, a.MachineID,
                a.StartDateTime, a.EndDateTime, a.UserID,
                a.PCBResultBefore, a.PCBResultAfter, a.PCBResultRepair,
                a.PCBTotalComp, a.PCBTotalInsp, a.TB, a.Lane, a.ArrayCnt,
                a.RepairStartDateTime, a.RepairEndDateTime,
                a.RepairUserID, a.ReviewUserID,
                a.ResultDBName, a.JobFileIDLocal, a.PCBComment,
                k_initial.JSONData  AS InitialJSON,
                k_review.JSONData   AS ReviewJSON
            FROM dbo.TB_AOIPCB a
            CROSS APPLY (
                SELECT TOP 1 CAST(k2.JSONData AS NVARCHAR(MAX)) AS JSONData
                FROM dbo.TB_PcbToKsmart k2
                WHERE k2.PCBGUID = a.PCBGUID AND k2.Type = 0
                ORDER BY k2.ID DESC
            ) k_initial
            OUTER APPLY (
                SELECT TOP 1 CAST(k2.JSONData AS NVARCHAR(MAX)) AS JSONData
                FROM dbo.TB_PcbToKsmart k2
                WHERE k2.PCBGUID = a.PCBGUID AND k2.Type = 1
                ORDER BY k2.ID DESC
            ) k_review
            WHERE a.SaveDone = 1
            ORDER BY a.PCBID";

        /// <summary>
        /// Test-mode SQL pass 2: fetches the latest N records that do NOT have any
        /// TB_PcbToKsmart entry, filtered to the 289-0222 product family (~1989 records
        /// across 5 variants) so the test dataset is concentrated on one product.
        /// Remove the PCBModel filter before use in a new deployment.
        /// </summary>
        private const string TEST_SQL_NO_JSON = @"
            SELECT TOP(@batchSize)
                a.PCBID, a.BarCode, a.ALLBarCode, a.PCBModel, a.Lot, a.MachineID,
                a.StartDateTime, a.EndDateTime, a.UserID,
                a.PCBResultBefore, a.PCBResultAfter, a.PCBResultRepair,
                a.PCBTotalComp, a.PCBTotalInsp, a.TB, a.Lane, a.ArrayCnt,
                a.RepairStartDateTime, a.RepairEndDateTime,
                a.RepairUserID, a.ReviewUserID,
                a.ResultDBName, a.JobFileIDLocal, a.PCBComment,
                NULL AS InitialJSON,
                NULL AS ReviewJSON
            FROM dbo.TB_AOIPCB a
            WHERE a.SaveDone = 1
              AND a.PCBModel IN (
                  '289-0222BS-3.1',
                  '289-0222BS-4.0',
                  '289-0222TS-4.0_DEV-25042',
                  '289-0222TM-2.2',
                  '289-0222BM-2.2'
              )
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.TB_PcbToKsmart k2
                  WHERE k2.PCBGUID = a.PCBGUID AND k2.Type = 0
              )
            ORDER BY a.StartDateTime DESC";

        private async Task<int> ImportBatchAsync(CancellationToken ct)
        {
            if (TEST_MODE)
                return await ImportTestBatchAsync(ct);

            // â”€â”€ Production path â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(BATCH_SQL, conn);
            cmd.Parameters.AddWithValue("@batchSize", _config.BatchSize);
            cmd.Parameters.AddWithValue("@lastId", _checkpoint.LastId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int count = 0;
            try
            {
                count = await ProcessReaderAsync(reader, ct);
            }
            finally
            {
                // Persist even on mid-batch cancellation so restart resumes from the right position
                if (count > 0)
                    _checkpoint.Save(_config.ResolveDataPath(_config.CheckpointFile));
            }
            return count;
        }

        /// <summary>
        /// Test-mode two-pass import:
        ///   Pass 1 â€” ALL records with PcbToKsmart JSON (component-level detail).
        ///   Pass 2 â€” Latest <see cref="TEST_MODE_NO_JSON_COUNT"/> records without JSON.
        /// Checkpoint is not updated in test mode.
        /// </summary>
        private async Task<int> ImportTestBatchAsync(CancellationToken ct)
        {
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct);

            Log($"âš ï¸  TEST MODE pass 1 â€” fetching ALL records with PcbToKsmart JSON");
            int count = 0;
            await using (var cmd = new SqlCommand(TEST_SQL_JSON, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                count = await ProcessReaderAsync(reader, ct);
            Log($"  Pass 1 complete: {count} report(s) from JSON records.");

            Log($"âš ï¸  TEST MODE pass 2 â€” fetching latest {TEST_MODE_NO_JSON_COUNT} records without JSON");
            int pass2 = 0;
            await using (var cmd2 = new SqlCommand(TEST_SQL_NO_JSON, conn))
            {
                cmd2.Parameters.AddWithValue("@batchSize", TEST_MODE_NO_JSON_COUNT);
                await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
                pass2 = await ProcessReaderAsync(reader2, ct);
            }
            Log($"  Pass 2 complete: {pass2} report(s) from non-JSON records.");

            return count + pass2;
        }

        /// <summary>
        /// Processes all rows from <paramref name="reader"/>, creating and submitting WATS reports.
        /// Updates <see cref="_checkpoint"/> per row in production mode.
        /// </summary>
        private async Task<int> ProcessReaderAsync(SqlDataReader reader, CancellationToken ct)
        {
            int count = 0;
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long pcbId = reader.GetInt64(reader.GetOrdinal("PCBID"));

                    // Parse both initial (Type=0) and review (Type=1) JSON
                    var initialKsmart = ParseKsmartJson(GetString(reader, "InitialJSON"), pcbId, "initial");
                    var reviewKsmart = ParseKsmartJson(GetString(reader, "ReviewJSON"), pcbId, "review");

                    // Use review JSON when available (has operator decisions), initial as fallback
                    var displayKsmart = reviewKsmart ?? initialKsmart;

                    // Log component counts for debugging
                    LogComponentCounts(pcbId, initialKsmart, "initial");
                    LogComponentCounts(pcbId, reviewKsmart, "review");

                    // 1) Always create the AOI inspection UUT
                    //    Uses initialKsmart for NG components (what AOI actually detected)
                    var aoiReport = CreateAoiReport(reader, displayKsmart, initialKsmart, out var failureStepMap);
                    SubmitReport(aoiReport);
                    count++;

                    // 2) If repair was done, create a UUR linked to the AOI UUT
                    //    Uses initialKsmart for failures (NgComp populated before operator review)
                    int? repairResult = GetNullableInt(reader, "PCBResultRepair");
                    if (repairResult.HasValue)
                    {
                        var uur = CreateRepairUUR(reader, initialKsmart, aoiReport, failureStepMap);
                        if (uur != null)
                        {
                            SubmitReport(uur);
                            count++;
                        }
                    }

                    // 3) If AOI scan failed and a review/re-inspection result exists,
                    //    create a second UUT representing the operator review outcome
                    int? resultAfter = GetNullableInt(reader, "PCBResultAfter");
                    if (aoiReport.Status == UUTStatusType.Failed && resultAfter.HasValue)
                    {
                        var reviewReport = CreateReviewReport(reader, displayKsmart, resultAfter.Value);
                        SubmitReport(reviewReport);
                        count++;
                    }

                    if (!TEST_MODE)
                        _checkpoint.LastId = pcbId;
                }
                catch (Exception ex)
                {
                    Log($"Row convert error (PCBID={GetSafePcbId(reader)}): {ex.Message}");
                }
            }
            return count;
        }

        // ----------------------------------------------------------------
        // AOI inspection UUT
        // ----------------------------------------------------------------

        /// <summary>
        /// Creates a WATS UUT report for the AOI inspection scan.
        /// Includes component-level pass/fail results from JSON data.
        /// </summary>
        /// <param name="ksmart">Display/header JSON (review if available, else initial).</param>
        /// <param name="initialKsmart">Initial scan JSON with original NgComp for failed steps.</param>
        /// <param name="failureStepMap">Output: maps defect code (e.g. F1_M) to the StepOrderNumber of its failed step in this UUT.</param>
        private UUTReport CreateAoiReport(SqlDataReader row, KsmartResultData? ksmart, KsmartResultData? initialKsmart, out Dictionary<string, int> failureStepMap)
        {
            string serial = GetSerialNumber(row, ksmart);
            string rawPartNumber = GetRawPartNumber(row, ksmart);
            var (partNumber, revision, partSide) = ParsePartNumber(rawPartNumber);
            string operatorName = GetString(row, "UserID") ?? ksmart?.User ?? "";
            string tb = GetString(row, "TB") ?? "";

            string operationType = tb switch
            {
                "T" => OP_AOI_TOP,
                "B" => OP_AOI_BOTTOM,
                _ => OP_AOI_TOP
            };

            string stationName = GetString(row, "MachineID") ?? "";

            var uut = _api!.CreateUUTReport(
                operatorName, partNumber, revision, serial,
                operationType, stationName, "");

            // Timestamps
            DateTime? start = AdjustTime(GetDateTime(row, "StartDateTime"));
            DateTime? end = AdjustTime(GetDateTime(row, "EndDateTime"));
            if (start.HasValue)
                uut.StartDateTime = start.Value;
            if (start.HasValue && end.HasValue)
                uut.ExecutionTime = (end.Value - start.Value).TotalSeconds;

            // Status from PCBResultBefore
            int? resultBefore = GetNullableInt(row, "PCBResultBefore");
            uut.Status = MapResultToStatus(resultBefore);

            // TestSocketIndex from PanelInfo
            if (ksmart?.PanelInfo != null && ksmart.PanelInfo.Count > 0)
            {
                var firstPanel = ksmart.PanelInfo.OrderBy(p => p.PanelIndex).First();
                if (short.TryParse(firstPanel.PanelIndex, out short socketIdx))
                    uut.TestSocketIndex = socketIdx;
            }

            // Add Side as MiscUUTInfo if derived from part number suffix
            if (partSide != null)
                uut.AddMiscUUTInfo("Side", partSide);

            var root = uut.GetRootSequenceCall();

            // --- Board Info ---
            var infoSeq = root.AddSequenceCall("Board Info");
            AddStringStep(infoSeq, "BarCode", serial);
            AddStringStep(infoSeq, "PCBModel", rawPartNumber);
            AddStringStep(infoSeq, "Side", tb == "T" ? "Top" : tb == "B" ? "Bottom" : tb);
            AddStringStep(infoSeq, "MachineID", stationName);

            string? lot = GetString(row, "Lot");
            if (!string.IsNullOrWhiteSpace(lot))
                AddStringStep(infoSeq, "Lot", lot);

            string? jobFile = GetString(row, "JobFileIDLocal");
            if (!string.IsNullOrWhiteSpace(jobFile))
                AddStringStep(infoSeq, "JobFile", jobFile);

            string? comment = GetString(row, "PCBComment");
            if (!string.IsNullOrWhiteSpace(comment))
                AddStringStep(infoSeq, "Comment", comment);

            // --- Panel Info (multi-panel boards) ---
            if (ksmart?.PanelInfo != null && ksmart.PanelInfo.Count > 0)
            {
                var panelSeq = root.AddSequenceCall("Panel Info");
                foreach (var panel in ksmart.PanelInfo)
                    AddStringStep(panelSeq, $"Panel {panel.PanelIndex}", panel.PanelBarcode ?? "");
            }

            // --- Inspection Summary ---
            var inspSeq = root.AddSequenceCall("Inspection Summary");
            int? totalComp = GetNullableInt(row, "PCBTotalComp");
            int? totalInsp = GetNullableInt(row, "PCBTotalInsp");
            int? arrayCnt = GetNullableInt(row, "ArrayCnt");

            if (totalComp.HasValue)
            {
                var step = inspSeq.AddNumericLimitStep("TotalComponents");
                step.AddTest(totalComp.Value, CompOperatorType.LOG, 0, 0, "");
                step.Status = StepStatusType.Passed;
            }
            if (totalInsp.HasValue)
            {
                var step = inspSeq.AddNumericLimitStep("TotalInspections");
                step.AddTest(totalInsp.Value, CompOperatorType.LOG, 0, 0, "");
                step.Status = StepStatusType.Passed;
            }
            if (arrayCnt.HasValue && arrayCnt.Value > 0)
            {
                var step = inspSeq.AddNumericLimitStep("ArrayCount");
                step.AddTest(arrayCnt.Value, CompOperatorType.LOG, 0, 0, "");
                step.Status = StepStatusType.Passed;
            }

            // --- Component-level inspection results from JSON ---
            // Use initial scan JSON for NG components (what AOI actually found)
            // The review JSON may have cleared NgComp after operator review
            var compSource = initialKsmart ?? ksmart;
            bool hasFailedSteps = false;
            if (compSource != null)
                hasFailedSteps = AddComponentSteps(root, compSource);

            // --- Failure Code Steps (linked from UUR) ---
            // For each unique defect code found in NgComp, add a failed step at the root.
            // The UUR references these steps via StepOrderNumber.
            failureStepMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (uut.Status == UUTStatusType.Failed && compSource != null)
            {
                var failedComps = new List<ComponentResult>();
                if (compSource.NgComp != null) failedComps.AddRange(compSource.NgComp);
                if (compSource.IgnoreList != null) failedComps.AddRange(compSource.IgnoreList);
                if (compSource.PassComp != null) failedComps.AddRange(compSource.PassComp.Where(c => !IsComponentPassed(c)));

                var uniqueDefects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ng in failedComps)
                {
                    if (string.IsNullOrEmpty(ng.NgDetail)) continue;
                    foreach (var entry in ng.NgDetail.Split(','))
                    {
                        string[] parts = entry.Split('|');
                        string defectKey = parts.Length > 1 ? parts[1] : parts[0];
                        if (defectKey.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                            || defectKey.Equals("GOOD", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (uniqueDefects.Add(defectKey))
                        {
                            var failStep = root.AddPassFailStep(defectKey);
                            failStep.AddTest(false);
                            failStep.Status = StepStatusType.Failed;
                            failureStepMap[defectKey] = failStep.StepOrderNumber;
                            Log($"  Added failure step '{defectKey}' (StepOrder={failStep.StepOrderNumber})");
                        }
                    }
                }
            }

            // --- AOI Result (PCBResultBefore) ---
            // This is the primary AOI scan verdict. Must FAIL when NG.
            bool resultPassed = uut.Status == UUTStatusType.Passed;
            var resultStep = root.AddPassFailStep("PCBResultBefore");
            resultStep.AddTest(resultPassed);
            resultStep.Status = resultPassed ? StepStatusType.Passed : StepStatusType.Failed;

            // --- Misc Info (unmapped fields + conversion timestamp) ---
            AddMiscInfo(uut, row, ksmart);

            return uut;
        }

        /// <summary>
        /// Adds component-level inspection results as pass/fail steps,
        /// grouped by array number. Determines component pass/fail from
        /// the individual Result code AND the list membership.
        /// Returns true if at least one failed step was added.
        /// </summary>
        private bool AddComponentSteps(SequenceCall root, KsmartResultData ksmart)
        {
            var allComponents = new List<(ComponentResult comp, bool passed)>();

            if (ksmart.PassComp != null)
                foreach (var c in ksmart.PassComp)
                    allComponents.Add((c, IsComponentPassed(c)));

            if (ksmart.NgComp != null)
                foreach (var c in ksmart.NgComp)
                    allComponents.Add((c, false));  // NgComp = always failed

            // IgnoreList: components flagged by AOI
            if (ksmart.IgnoreList != null)
                foreach (var c in ksmart.IgnoreList)
                    allComponents.Add((c, false));  // IgnoreList = flagged = failed

            if (allComponents.Count == 0) return false;

            bool hasFailedStep = false;

            // Group by Array, then sort by CompName
            var grouped = allComponents
                .GroupBy(c => c.comp.Array ?? "1")
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                var arraySeq = root.AddSequenceCall($"Array {group.Key}");

                foreach (var (comp, passed) in group.OrderBy(c => c.comp.CompName))
                {
                    string stepName = comp.CompName ?? "Unknown";

                    var step = arraySeq.AddPassFailStep(stepName);
                    step.AddTest(passed);
                    step.Status = passed ? StepStatusType.Passed : StepStatusType.Failed;

                    if (!passed) hasFailedStep = true;

                    // Add package/part info
                    if (!string.IsNullOrEmpty(comp.PkgName))
                    {
                        var pkgStep = arraySeq.AddStringValueStep($"{stepName}_Package");
                        pkgStep.AddTest(comp.PkgName);
                        pkgStep.Status = StepStatusType.Done;
                    }

                    // Add NG detail for failed components
                    if (!passed && !string.IsNullOrEmpty(comp.NgDetail))
                    {
                        var ngStep = arraySeq.AddStringValueStep($"{stepName}_Defect");
                        ngStep.AddTest(comp.NgDetail);
                        ngStep.Status = StepStatusType.Done;
                    }
                }
            }

            return hasFailedStep;
        }

        // ----------------------------------------------------------------
        // Repair UUR (Unit Under Repair)
        // ----------------------------------------------------------------

        /// <summary>
        /// Creates a WATS UUR report for the repair/review process.
        /// Links to the AOI UUT report, adds failures from NgComp and IgnoreList.
        /// Returns null if repair type is not configured in WATS.
        /// </summary>
        /// <param name="failureStepMap">Maps defect code to StepOrderNumber in the AOI UUT, for UUR failure linking.</param>
        private UURReport? CreateRepairUUR(SqlDataReader row, KsmartResultData? ksmart, UUTReport aoiReport, Dictionary<string, int> failureStepMap)
        {
            if (_repairType == null)
            {
                Log("  Skipping UUR: repair type not configured in WATS.");
                return null;
            }

            string repairUser = GetString(row, "RepairUserID") ?? "";
            string reviewUser = GetString(row, "ReviewUserID") ?? "";
            string operatorName = !string.IsNullOrWhiteSpace(repairUser) ? repairUser : reviewUser;

            // Create UUR linked to the AOI UUT report (copies SN/PN/Rev and sets UUTGuid)
            var uur = _api!.CreateUURReport(operatorName, _repairType, aoiReport);

            // Timestamps
            DateTime? repairStart = AdjustTime(GetDateTime(row, "RepairStartDateTime"));
            DateTime? repairEnd = AdjustTime(GetDateTime(row, "RepairEndDateTime"));
            DateTime? aoiStart = AdjustTime(GetDateTime(row, "StartDateTime"));
            uur.StartDateTime = repairStart ?? aoiStart ?? DateTime.Now;
            if (repairStart.HasValue && repairEnd.HasValue)
                uur.ExecutionTime = (repairEnd.Value - repairStart.Value).TotalSeconds;

            // Comment with repair result summary
            int? repairResult = GetNullableInt(row, "PCBResultRepair");
            int? resultAfter = GetNullableInt(row, "PCBResultAfter");
            uur.Comment = $"Repair: {MapResultCodeName(repairResult)}, After: {MapResultCodeName(resultAfter)}, "
                        + $"RepairUser: {repairUser}, ReviewUser: {reviewUser}";

            // Collect failures: components from NgComp/IgnoreList,
            // PLUS any component in PassComp whose Result code is NG.
            var failedComponents = new List<ComponentResult>();
            if (ksmart?.NgComp != null)
                failedComponents.AddRange(ksmart.NgComp);
            if (ksmart?.IgnoreList != null)
                failedComponents.AddRange(ksmart.IgnoreList);
            if (ksmart?.PassComp != null)
                failedComponents.AddRange(ksmart.PassComp.Where(c => !IsComponentPassed(c)));

            if (failedComponents.Count > 0)
                AddFailures(uur, failedComponents, failureStepMap);

            // If UUR still has no failures but repair was triggered, add a generic one
            if (failedComponents.Count == 0)
            {
                Log("  No component-level failures found, adding board-level failure.");
                AddBoardLevelFailure(uur);
            }

            return uur;
        }

        /// <summary>
        /// Creates a UUT report representing the operator review/re-inspection
        /// after the initial AOI scan failed. Status reflects PCBResultAfter.
        /// </summary>
        private UUTReport CreateReviewReport(SqlDataReader row, KsmartResultData? ksmart, int resultAfterCode)
        {
            string serial = GetSerialNumber(row, ksmart);
            string rawPartNumber = GetRawPartNumber(row, ksmart);
            var (partNumber, revision, partSide) = ParsePartNumber(rawPartNumber);
            string tb = GetString(row, "TB") ?? "";

            // Use repair/review operator if available, fallback to AOI operator
            string repairUser = GetString(row, "RepairUserID") ?? "";
            string reviewUser = GetString(row, "ReviewUserID") ?? "";
            string operatorName = !string.IsNullOrWhiteSpace(reviewUser) ? reviewUser
                                : !string.IsNullOrWhiteSpace(repairUser) ? repairUser
                                : GetString(row, "UserID") ?? ksmart?.User ?? "";

            // Same AOI operation type as the original scan
            string operationType = tb switch
            {
                "T" => OP_AOI_TOP,
                "B" => OP_AOI_BOTTOM,
                _ => OP_AOI_TOP
            };

            string stationName = GetString(row, "MachineID") ?? "";

            var uut = _api!.CreateUUTReport(
                operatorName, partNumber, revision, serial,
                operationType, stationName, "");

            // Use repair/review timestamps
            DateTime? repairEnd = AdjustTime(GetDateTime(row, "RepairEndDateTime"));
            DateTime? endTime = AdjustTime(GetDateTime(row, "EndDateTime"));
            uut.StartDateTime = repairEnd ?? endTime ?? DateTime.Now;

            // Status reflects the actual PCBResultAfter code
            uut.Status = MapResultToStatus(resultAfterCode);

            // TestSocketIndex from PanelInfo
            if (ksmart?.PanelInfo != null && ksmart.PanelInfo.Count > 0)
            {
                var firstPanel = ksmart.PanelInfo.OrderBy(p => p.PanelIndex).First();
                if (short.TryParse(firstPanel.PanelIndex, out short socketIdx))
                    uut.TestSocketIndex = socketIdx;
            }

            // Add Side as MiscUUTInfo if derived from part number suffix
            if (partSide != null)
                uut.AddMiscUUTInfo("Side", partSide);

            var root = uut.GetRootSequenceCall();
            var infoSeq = root.AddSequenceCall("Review Info");
            AddStringStep(infoSeq, "BarCode", serial);
            AddStringStep(infoSeq, "PCBModel", rawPartNumber);
            AddStringStep(infoSeq, "Side", tb == "T" ? "Top" : tb == "B" ? "Bottom" : tb);

            AddStringStep(infoSeq, "PCBResultAfter", MapResultCodeName(resultAfterCode));

            if (!string.IsNullOrWhiteSpace(repairUser))
                AddStringStep(infoSeq, "RepairUser", repairUser);
            if (!string.IsNullOrWhiteSpace(reviewUser))
                AddStringStep(infoSeq, "ReviewUser", reviewUser);

            // --- PCBResultAfter (root-level verdict step) ---
            // Must be a Failed step when the review outcome is NG, so WATS
            // has at least one failing step to drive the report status.
            bool reviewPassed = uut.Status == UUTStatusType.Passed;
            var resultStep = root.AddPassFailStep("PCBResultAfter");
            resultStep.AddTest(reviewPassed);
            resultStep.Status = reviewPassed ? StepStatusType.Passed : StepStatusType.Failed;

            Log($"  Created review UUT ({uut.Status}) for {serial} (PCBResultAfter={MapResultCodeName(resultAfterCode)}).");

            // --- Misc Info (unmapped fields + conversion timestamp) ---
            AddMiscInfo(uut, row, ksmart);

            return uut;
        }

        /// <summary>
        /// Adds failures from NG/IgnoreList components to the UUR report.
        /// Tries to match NgDetail defect codes to configured fail codes.
        /// Links each failure to its corresponding failed step in the UUT via failureStepMap.
        /// </summary>
        private void AddFailures(UURReport uur, List<ComponentResult> ngComponents, Dictionary<string, int> failureStepMap)
        {
            FailCode[]? rootCodes = null;
            try
            {
                rootCodes = uur.GetRootFailcodes();
            }
            catch (Exception ex)
            {
                Log($"  Could not load fail codes: {ex.Message}");
            }

            // Log available categories and codes for debugging
            if (rootCodes != null)
            {
                foreach (var cat in rootCodes)
                {
                    var children = uur.GetChildFailCodes(cat);
                    Log($"  Fail code category '{cat.Description}': [{string.Join(", ", children.Select(c => c.Description))}]");
                }
            }

            foreach (var ng in ngComponents)
            {
                string compRef = ng.CompName ?? "";
                string comment = FormatNgComment(ng);

                // NgDetail can be comma-separated: "30000006|F6_LB|0,30000003|F3_IC|0"
                // Each entry is "DefectCode|DefectName|PadIndex"
                var defectEntries = string.IsNullOrEmpty(ng.NgDetail)
                    ? Array.Empty<string>()
                    : ng.NgDetail.Split(',');

                // Deduplicate by defect name (element [1]) â€” same defect on multiple pads
                // should only produce one failure entry
                var uniqueDefects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyMatched = false;

                if (rootCodes != null)
                {
                    foreach (var entry in defectEntries)
                    {
                        string[] parts = entry.Split('|');
                        string defectKey = parts.Length > 1 ? parts[1] : parts[0];

                        // Skip duplicates (e.g. "F6_LL|1,F6_LL|2,F6_LL|3" â†’ one failure)
                        if (!uniqueDefects.Add(defectKey))
                            continue;

                        // Skip PASS entries mixed in (e.g. "12000000|PASS|0,30000004|F3_IM|0")
                        if (defectKey.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                            || defectKey.Equals("GOOD", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var matchedCode = FindFailCode(rootCodes, uur, defectKey);

                        if (matchedCode != null)
                        {
                            int stepOrder = failureStepMap.TryGetValue(defectKey, out int so) ? so : 0;
                            uur.AddFailure(matchedCode, compRef, comment, stepOrder);
                            Log($"  Matched '{defectKey}' â†’ fail code '{matchedCode.Description}' for {compRef} (step={stepOrder})");
                            anyMatched = true;
                        }
                        else
                        {
                            Log($"  No match for defect key '{defectKey}' on {compRef}");
                        }
                    }
                }

                // Fallback if no defects matched (or no NgDetail at all)
                if (!anyMatched)
                {
                    if (rootCodes != null && rootCodes.Length > 0)
                    {
                        var children = uur.GetChildFailCodes(rootCodes[0]);
                        if (children.Length > 0)
                        {
                            uur.AddFailure(children[0], compRef, comment, 0);
                            Log($"  Used fallback fail code '{children[0].Description}' for {compRef} (NgDetail='{ng.NgDetail}')");
                        }
                        else
                        {
                            Log($"  No child fail codes available for {compRef}.");
                        }
                    }
                    else
                    {
                        Log($"  Cannot add failure for {compRef}: no fail codes configured.");
                    }
                }
            }
        }

        /// <summary>
        /// Searches the fail code tree for a match by description.
        /// Tries exact match first, then prefix/contains fallback.
        /// </summary>
        private FailCode? FindFailCode(FailCode[] rootCodes, UURReport uur, string searchKey)
        {
            // Pass 1: exact or normalized match
            foreach (var category in rootCodes)
            {
                var children = uur.GetChildFailCodes(category);
                foreach (var child in children)
                {
                    string desc = child.Description ?? "";
                    if (desc.Equals(searchKey, StringComparison.OrdinalIgnoreCase)
                        || desc.Replace(" ", "_").Equals(searchKey, StringComparison.OrdinalIgnoreCase))
                        return child;
                }
            }

            // Pass 2: description starts with search key (e.g. "F1_M â€” Missing Component")
            foreach (var category in rootCodes)
            {
                var children = uur.GetChildFailCodes(category);
                foreach (var child in children)
                {
                    string desc = child.Description ?? "";
                    if (desc.StartsWith(searchKey, StringComparison.OrdinalIgnoreCase))
                        return child;
                }
            }

            // Pass 3: search key appears anywhere in description
            foreach (var category in rootCodes)
            {
                var children = uur.GetChildFailCodes(category);
                foreach (var child in children)
                {
                    if ((child.Description ?? "").IndexOf(searchKey, StringComparison.OrdinalIgnoreCase) >= 0)
                        return child;
                }
            }

            return null;
        }

        /// <summary>
        /// Determines if a component passed based on its individual Result code.
        /// Result 11000000 = GOOD, 12000000 = PASS. Anything else = failed.
        /// </summary>
        private static bool IsComponentPassed(ComponentResult comp)
        {
            if (string.IsNullOrEmpty(comp.Result))
                return true; // No result code = assume pass (don't create false failures)

            if (int.TryParse(comp.Result, out int resultCode))
                return resultCode == RESULT_GOOD || resultCode == RESULT_PASS;

            return true; // Unparseable = assume pass
        }

        /// <summary>
        /// Adds a generic board-level failure to a UUR when no component-level
        /// failures could be found. Uses the "Unclassified" fail code (F14).
        /// </summary>
        private void AddBoardLevelFailure(UURReport uur)
        {
            FailCode[]? rootCodes = null;
            try { rootCodes = uur.GetRootFailcodes(); }
            catch { /* already logged elsewhere */ }

            if (rootCodes == null || rootCodes.Length == 0)
            {
                Log("  Cannot add board-level failure: no fail codes configured.");
                return;
            }

            // Use the first available child code as the generic fallback
            FailCode? fallback = null;
            foreach (var cat in rootCodes)
            {
                var children = uur.GetChildFailCodes(cat);
                if (children.Length > 0)
                {
                    fallback = children[0];
                    break;
                }
            }

            if (fallback != null)
            {
                uur.AddFailure(fallback, "", "AOI board inspection failed (no component detail available)", 0);
                Log($"  Added board-level failure with code '{fallback.Description}'");
            }
        }

        private static string FormatNgComment(ComponentResult ng)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ng.PkgName)) parts.Add($"Package: {ng.PkgName}");
            if (!string.IsNullOrEmpty(ng.InspNgData)) parts.Add($"InspData: {ng.InspNgData}");
            if (!string.IsNullOrEmpty(ng.NgDetail)) parts.Add($"NgDetail: {ng.NgDetail}");
            if (!string.IsNullOrEmpty(ng.Array)) parts.Add($"Array: {ng.Array}");
            return string.Join(", ", parts);
        }

        // ----------------------------------------------------------------
        // Mapping helpers
        // ----------------------------------------------------------------

        /// <summary>Maps AOI result code to WATS UUT status.</summary>
        private static UUTStatusType MapResultToStatus(int? resultCode)
        {
            return resultCode switch
            {
                RESULT_GOOD => UUTStatusType.Passed,
                RESULT_PASS => UUTStatusType.Passed,
                RESULT_NG => UUTStatusType.Failed,
                null => UUTStatusType.Error,
                _ => UUTStatusType.Failed
            };
        }

        /// <summary>Maps a numeric result code to its display name.</summary>
        private static string MapResultCodeName(int? code)
        {
            return code switch
            {
                RESULT_GOOD => "GOOD",
                RESULT_PASS => "PASS",
                RESULT_NG => "NG",
                null => "N/A",
                _ => $"Unknown ({code})"
            };
        }

        /// <summary>
        /// Derives a serial number with fallback priority:
        /// 1. BarCode from TB_AOIPCB (if set and not "Fail")
        /// 2. Barcode from JSON (if set)
        /// 3. First PanelBarcode from JSON PanelInfo
        /// 4. First entry from ALLBarCode (comma-separated)
        /// 5. Fallback: PCBID-{id}
        /// </summary>
        private static string GetSerialNumber(SqlDataReader row, KsmartResultData? ksmart)
        {
            // 1. BarCode from DB
            string? barcode = GetString(row, "BarCode");
            if (!string.IsNullOrWhiteSpace(barcode)
                && !barcode.Equals("Fail", StringComparison.OrdinalIgnoreCase))
                return barcode.Trim();

            // 2. Barcode from JSON
            if (ksmart != null && !string.IsNullOrWhiteSpace(ksmart.Barcode))
                return ksmart.Barcode.Trim();

            // 3. First PanelBarcode from JSON
            if (ksmart?.PanelInfo != null && ksmart.PanelInfo.Count > 0)
            {
                var first = ksmart.PanelInfo
                    .OrderBy(p => p.PanelIndex)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PanelBarcode));
                if (first != null)
                    return first.PanelBarcode!.Trim();
            }

            // 4. First valid entry from ALLBarCode (comma-separated); skip "Fail" literals
            string? allBarcode = GetString(row, "ALLBarCode");
            if (!string.IsNullOrWhiteSpace(allBarcode))
            {
                var firstValid = allBarcode.Split(',')
                    .Select(s => s.Trim())
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)
                                        && !s.Equals("Fail", StringComparison.OrdinalIgnoreCase));
                if (firstValid != null)
                    return firstValid;
            }

            // 5. Fallback
            try
            {
                long pcbId = row.GetInt64(row.GetOrdinal("PCBID"));
                return $"PCBID-{pcbId}";
            }
            catch { return "UNKNOWN"; }
        }

        /// <summary>
        /// Gets raw part number. Prefers PcbModel from JSON, falls back to DB.
        /// </summary>
        private static string GetRawPartNumber(SqlDataReader row, KsmartResultData? ksmart)
        {
            if (ksmart != null && !string.IsNullOrWhiteSpace(ksmart.PcbModel))
                return ksmart.PcbModel.Trim();

            return GetString(row, "PCBModel") ?? "UNKNOWN";
        }

        /// <summary>
        /// Parses a part number that may contain a BS/TS (Bottom Side/Top Side) suffix
        /// and a revision after the last hyphen.
        /// Example: "289-0148BS-8.1" â†’ basePart="289-0148", revision="8.1", side="Bottom"
        /// Example: "289-0148TS-8.1" â†’ basePart="289-0148", revision="8.1", side="Top"
        /// If no BS/TS suffix is found, returns the raw part number with no revision or side.
        /// </summary>
        private static (string basePart, string revision, string? side) ParsePartNumber(string rawPartNumber)
        {
            // Pattern: anything, then BS or TS, then hyphen, then revision
            var match = Regex.Match(rawPartNumber, @"^(.+?)(BS|TS)-(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string basePart = match.Groups[1].Value;
                string bsTs = match.Groups[2].Value.ToUpperInvariant();
                string revision = match.Groups[3].Value;
                string side = bsTs == "BS" ? "Bottom" : "Top";
                return (basePart, revision, side);
            }

            return (rawPartNumber, "", null);
        }

        /// <summary>Adds a StringValueStep to a sequence.</summary>
        private static void AddStringStep(SequenceCall seq, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var step = seq.AddStringValueStep(name);
            step.AddTest(value);
            step.Status = StepStatusType.Done;
        }

        /// <summary>
        /// Adds unmapped DB and JSON fields as MiscUUTInfo key-value pairs
        /// on the UUT header, plus a conversion timestamp.
        /// </summary>
        private static void AddMiscInfo(UUTReport uut, SqlDataReader row, KsmartResultData? ksmart)
        {
            // Conversion timestamp (so user can tell old from new reports)
            uut.AddMiscUUTInfo("ConvertedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // Unmapped DB fields
            string? lane = GetString(row, "Lane");
            if (!string.IsNullOrWhiteSpace(lane))
                uut.AddMiscUUTInfo("Lane", lane);

            string? resultDbName = GetString(row, "ResultDBName");
            if (!string.IsNullOrWhiteSpace(resultDbName))
                uut.AddMiscUUTInfo("ResultDBName", resultDbName);

            string? allBarCode = GetString(row, "ALLBarCode");
            if (!string.IsNullOrWhiteSpace(allBarCode))
                uut.AddMiscUUTInfo("ALLBarCode", allBarCode);

            int? pcbResultRepair = GetNullableInt(row, "PCBResultRepair");
            if (pcbResultRepair.HasValue)
                uut.AddMiscUUTInfo("PCBResultRepair", MapResultCodeName(pcbResultRepair));

            // Unmapped JSON fields
            if (ksmart != null)
            {
                if (!string.IsNullOrWhiteSpace(ksmart.PcbResult))
                    uut.AddMiscUUTInfo("JSON_PcbResult", ksmart.PcbResult);
                if (!string.IsNullOrWhiteSpace(ksmart.PcbID))
                    uut.AddMiscUUTInfo("JSON_PcbID", ksmart.PcbID);
                if (!string.IsNullOrWhiteSpace(ksmart.PcbGuid))
                    uut.AddMiscUUTInfo("JSON_PcbGuid", ksmart.PcbGuid);
                if (!string.IsNullOrWhiteSpace(ksmart.UserName))
                    uut.AddMiscUUTInfo("JSON_UserName", ksmart.UserName);
                if (!string.IsNullOrWhiteSpace(ksmart.RunMode))
                    uut.AddMiscUUTInfo("JSON_RunMode", ksmart.RunMode);
                if (!string.IsNullOrWhiteSpace(ksmart.MachineIP))
                    uut.AddMiscUUTInfo("JSON_MachineIP", ksmart.MachineIP);
                if (!string.IsNullOrWhiteSpace(ksmart.PcbStartTime))
                    uut.AddMiscUUTInfo("JSON_PcbStartTime", ksmart.PcbStartTime);
                if (!string.IsNullOrWhiteSpace(ksmart.JudgmentStartTime))
                    uut.AddMiscUUTInfo("JSON_JudgmentStartTime", ksmart.JudgmentStartTime);
                if (!string.IsNullOrWhiteSpace(ksmart.JudgmentTime))
                    uut.AddMiscUUTInfo("JSON_JudgmentTime", ksmart.JudgmentTime);
            }
        }

        // ----------------------------------------------------------------
        // Submit
        // ----------------------------------------------------------------

        private void SubmitReport(Report report)
        {
            if (_api == null) return;
            _api.Submit(report);
        }

        // ----------------------------------------------------------------
        // Data reader helpers
        // ----------------------------------------------------------------

        private static string? GetString(SqlDataReader r, string col)
        {
            try
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetString(ord);
            }
            catch { return null; }
        }

        /// <summary>
        /// Applies <see cref="AppConfig.TimestampOffsetHours"/> to a nullable DateTime.
        /// Returns null unchanged. No-op when offset is 0.
        /// </summary>
        private DateTime? AdjustTime(DateTime? dt)
            => dt.HasValue && _config.TimestampOffsetHours != 0
                ? dt.Value.AddHours(_config.TimestampOffsetHours)
                : dt;

        private static DateTime? GetDateTime(SqlDataReader r, string col)
        {
            try
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetDateTime(ord);
            }
            catch { return null; }
        }

        private static int? GetNullableInt(SqlDataReader r, string col)
        {
            try
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetInt32(ord);
            }
            catch { return null; }
        }

        private static long GetSafePcbId(SqlDataReader r)
        {
            try { return r.GetInt64(r.GetOrdinal("PCBID")); }
            catch { return -1; }
        }

        // ----------------------------------------------------------------
        // JSON parsing helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses a KsmartResult JSON string. Returns null if empty or invalid.
        /// </summary>
        private KsmartResultData? ParseKsmartJson(string? jsonData, long pcbId, string label)
        {
            if (string.IsNullOrWhiteSpace(jsonData))
                return null;

            try
            {
                var parsed = JsonSerializer.Deserialize<KsmartResult>(jsonData, JsonOpts);
                return parsed?.ResultData;
            }
            catch (JsonException jex)
            {
                Log($"  JSON parse warning ({label}, PCBID={pcbId}): {jex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Logs component counts from a KsmartResultData for debugging.
        /// </summary>
        private void LogComponentCounts(long pcbId, KsmartResultData? ksmart, string label)
        {
            if (ksmart == null)
            {
                Log($"  PCBID={pcbId} {label}: no JSON data");
                return;
            }

            int pass = ksmart.PassComp?.Count ?? 0;
            int ng = ksmart.NgComp?.Count ?? 0;
            int ignore = ksmart.IgnoreList?.Count ?? 0;
            Log($"  PCBID={pcbId} {label}: Pass={pass}, NG={ng}, Ignore={ignore}");
        }

        // ----------------------------------------------------------------
        // Checkpoint management
        // ----------------------------------------------------------------

        public void ResetCheckpoint(long fromId = 0)
        {
            _checkpoint = new Checkpoint { LastId = fromId };
            _checkpoint.Save(_config.ResolveDataPath(_config.CheckpointFile));
            Log($"Checkpoint reset to PCBID > {fromId}");
        }

        private void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            try { System.IO.File.AppendAllText("ksmart_aoi_debug.log", line + Environment.NewLine); } catch { }
            OnLog?.Invoke(line);
        }
    }
}
