using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunAsHelper.Settings
{
    internal sealed record SavedApplication(string Name, string CommandLine, uint Priority);

    internal sealed class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RunAsHelper", "settings.json");

        // ── Persisted settings ───────────────────────────────────────────────

        public List<string> MruList { get; set; } = [];
        public int PriorityIndex { get; set; } = 2;
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool ShowTrayNotifications { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public int MaxMruEntries { get; set; } = 20;
        public List<SavedApplication> SavedApplications { get; set; } = [];

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

        public void AddMru(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            MruList.Remove(entry);
            MruList.Insert(0, entry);
            if (MruList.Count > MaxMruEntries)
                MruList.RemoveRange(MaxMruEntries, MruList.Count - MaxMruEntries);
            Save();
        }

        // ── Saved application helpers ─────────────────────────────────────────

        public void SaveApp(SavedApplication app)
        {
            int idx = SavedApplications.FindIndex(a =>
                string.Equals(a.Name, app.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                SavedApplications[idx] = app;
            else
                SavedApplications.Add(app);
            Save();
        }

        public void RemoveSavedApp(string name)
        {
            SavedApplications.RemoveAll(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        public void MoveSavedApp(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= SavedApplications.Count) return;
            toIndex = Math.Clamp(toIndex, 0, SavedApplications.Count - 1);
            if (fromIndex == toIndex) return;
            var item = SavedApplications[fromIndex];
            SavedApplications.RemoveAt(fromIndex);
            SavedApplications.Insert(toIndex, item);
            Save();
        }
    }

    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(List<SavedApplication>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }
}
