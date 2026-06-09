using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunAsHelper.Settings
{
    internal sealed class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RunAsHelper", "settings.json");

        // ── Persisted settings ───────────────────────────────────────────────

        /// <summary>Most-recently-used paths, newest first.</summary>
        public List<string> MruList { get; set; } = [];

        /// <summary>Index into the priority ComboBox (0=Idle … 5=Realtime).</summary>
        public int PriorityIndex { get; set; } = 2; // Normal

        /// <summary>Start with the main window hidden (tray-only).</summary>
        public bool StartMinimized { get; set; } = false;

        /// <summary>Closing/minimizing the window sends it to the tray rather than exiting.</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>Show balloon notifications when minimizing to tray.</summary>
        public bool ShowTrayNotifications { get; set; } = true;

        /// <summary>Maximum number of MRU entries to keep.</summary>
        public int MaxMruEntries { get; set; } = 20;

        // ── Persistence ──────────────────────────────────────────────────────

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                        ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
            }
            catch { }
        }

        // ── MRU helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Adds <paramref name="entry"/> to the front of the MRU list,
        /// deduplicating and trimming to <see cref="MaxMruEntries"/>.
        /// Saves immediately.
        /// </summary>
        public void AddMru(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            MruList.Remove(entry);
            MruList.Insert(0, entry);
            if (MruList.Count > MaxMruEntries)
                MruList.RemoveRange(MaxMruEntries, MruList.Count - MaxMruEntries);
            Save();
        }
    }

    // Compile-time JSON serialization — no reflection at runtime.
    [JsonSerializable(typeof(AppSettings))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }
}
