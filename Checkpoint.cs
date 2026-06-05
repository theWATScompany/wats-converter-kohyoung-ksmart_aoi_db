using System;
using System.IO;
using System.Text.Json;

namespace Virinco.WATS.Converter.KohYoung
{
    /// <summary>
    /// Tracks the last imported record ID so only new rows are fetched on each poll cycle.
    /// </summary>
    public class Checkpoint
    {
        public DateTime LastTimestamp { get; set; } = DateTime.MinValue;
        public long LastId { get; set; } = 0;

        private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        public static Checkpoint Load(string path)
        {
            if (!File.Exists(path))
                return new Checkpoint { LastTimestamp = DateTime.UtcNow.AddDays(-7) };

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Checkpoint>(json) ?? new Checkpoint();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, _jsonOpts));
        }
    }
}
