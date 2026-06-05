using System;
using System.IO;
using System.Text.Json;

namespace Virinco.WATS.Converter.KohYoung
{
    /// <summary>
    /// Application configuration. Persisted to appsettings.json in the user's LocalAppData folder.
    /// </summary>
    public class AppConfig
    {
        // --- Database ---
        public string SqlServer { get; set; } = "";
        public string SqlDatabase { get; set; } = "";
        public string SqlUser { get; set; } = "";
        public string SqlPassword { get; set; } = "";
        public bool TrustServerCertificate { get; set; } = true;

        // --- Polling ---
        public int PollIntervalSeconds { get; set; } = 30;
        public int BatchSize { get; set; } = 100;

        // --- WATS ---
        public string WATSServerUrl { get; set; } = "";
        public string WATSApiToken { get; set; } = "";
        public bool OfflineMode { get; set; } = true;

        // --- Checkpoint ---
        public string CheckpointFile { get; set; } = "checkpoint.json";

        // --- Process Codes ---
        // These MUST be configured to match the operation type codes in the target WATS server.
        public string ProcessCodeAoiTop { get; set; } = "";
        public string ProcessCodeAoiBottom { get; set; } = "";
        public string ProcessCodeRepair { get; set; } = "";

        // --- Timestamp adjustment ---
        // Hours to add to all timestamps read from the database.
        // The Koh Young KSMART machine records times in local machine time.
        // Adjust if the machine clock uses a different timezone than the WATS server.
        // Set to 0 (default) if no adjustment is needed.
        public double TimestampOffsetHours { get; set; } = 0;

        // --- Startup ---
        public bool AutoStart { get; set; } = true;

        // --- Logging ---
        public bool VerboseLogging { get; set; } = false;
        public bool LogStepDetails { get; set; } = false;
        public bool LogMeasurements { get; set; } = false;
        public bool LogSkippedRecords { get; set; } = true;
        public bool LogBatchSummary { get; set; } = true;

        // -----------------------------------------------------------------

        /// <summary>Returns true when the minimum required settings are present to start polling.</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(SqlServer) &&
            !string.IsNullOrWhiteSpace(SqlDatabase) &&
            !string.IsNullOrWhiteSpace(SqlUser) &&
            !string.IsNullOrWhiteSpace(ProcessCodeAoiTop);

        /// <summary>Builds a Microsoft.Data.SqlClient connection string from the individual properties.</summary>
        public string BuildConnectionString()
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = SqlServer,
                UserID = SqlUser,
                Password = SqlPassword,
                TrustServerCertificate = TrustServerCertificate,
                ConnectTimeout = 15,
                Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional
            };
            if (!string.IsNullOrWhiteSpace(SqlDatabase))
                b.InitialCatalog = SqlDatabase;
            return b.ConnectionString;
        }

        // -----------------------------------------------------------------

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true
        };

        private static string DataDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Virinco", "KSMART_AOI_DB");

        private static string DefaultPath => Path.Combine(DataDirectory, "appsettings.json");

        /// <summary>Resolves a relative path (e.g. checkpoint file) to the app data directory.</summary>
        public string ResolveDataPath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath)) return relativePath;
            return Path.Combine(DataDirectory, relativePath);
        }

        public static AppConfig Load(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path))
            {
                var defaultCfg = new AppConfig();
                defaultCfg.Save(path);
                return defaultCfg;
            }
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }

        public void Save(string? path = null)
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, _jsonOpts));
        }
    }
}
