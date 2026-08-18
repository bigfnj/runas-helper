using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunAsHelper.Settings
{
    internal enum WindowsState { Normal = 0, Minimized = 1, Maximized = 2, Hidden = 3 }

    internal sealed record SavedApplication
    {
        public string       Name             { get; init; } = string.Empty;
        public string       Location         { get; init; } = string.Empty;  // exe / file path
        public string       Parameter        { get; init; } = string.Empty;  // arguments
        public uint         Priority         { get; init; } = 0x20;          // NORMAL_PRIORITY_CLASS
        public string       WorkingDirectory { get; init; } = string.Empty;
        public WindowsState WindowsState     { get; init; } = WindowsState.Normal;
        public string       Account          { get; init; } = "ti";          // "ti" | "system"

        // Legacy field from pre-1.2 settings.json; migrated to Location/Parameter on load.
        public string? CommandLine { get; init; }

        /// <summary>Full command line for the service: quoted location + parameter.</summary>
        [JsonIgnore]
        public string EffectiveCommandLine
        {
            get
            {
                string loc = Location.Contains(' ') && !Location.StartsWith('"') ? $"\"{Location}\"" : Location;
                return string.IsNullOrWhiteSpace(Parameter) ? loc : $"{loc} {Parameter}";
            }
        }

        /// <summary>Win32 SW_* value derived from the window state.</summary>
        [JsonIgnore]
        public int ShowWindow => WindowsState switch
        {
            WindowsState.Hidden    => 0,   // SW_HIDE
            WindowsState.Minimized => 7,   // SW_SHOWMINNOACTIVE
            WindowsState.Maximized => 3,   // SW_SHOWMAXIMIZED
            _                      => 1,   // SW_SHOWNORMAL
        };

        /// <summary>Splits a legacy command line into (location, parameter).</summary>
        public static (string location, string parameter) SplitCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return (commandLine ?? string.Empty, string.Empty);
            string s = commandLine.Trim();
            if (s.StartsWith('"'))
            {
                int close = s.IndexOf('"', 1);
                if (close < 0) return (s[1..], string.Empty);
                return (s[1..close], s[(close + 1)..].TrimStart());
            }
            int sp = s.IndexOf(' ');
            return sp < 0 ? (s, string.Empty) : (s[..sp], s[(sp + 1)..]);
        }
    }

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
        public bool StartWithWindows { get; set; } = true;   // auto-start tray at login
        public bool ShowTrayNotifications { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public int MaxMruEntries { get; set; } = 20;

        // Security: whether the CLI may drive the service. Session-only — never
        // persisted, so it resets to OFF on every launch. The tray pushes this
        // to the service; the service is the authoritative gate.
        [JsonIgnore]
        public bool AllowCommandLine { get; set; }

        // How long an opened CLI gate stays open before the service auto-closes it.
        // Persisted (it is a preference, not the gate itself). 0 = no expiry.
        public int CliGateMinutes { get; set; } = 30;
        public List<SavedApplication> SavedApplications { get; set; } = [];

        // ── Persistence ──────────────────────────────────────────────────────

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                        ?? new AppSettings();
                    loaded.MigrateLegacy();
                    return loaded;
                }
            }
            catch { }
            return new AppSettings();
        }

        // Upgrade pre-1.2 saved apps (which stored a single CommandLine) into the
        // Location + Parameter shape. Runs once on load; re-saving persists it.
        private void MigrateLegacy()
        {
            for (int i = 0; i < SavedApplications.Count; i++)
            {
                var a = SavedApplications[i];
                if (string.IsNullOrEmpty(a.Location) && !string.IsNullOrEmpty(a.CommandLine))
                {
                    var (loc, param) = SavedApplication.SplitCommandLine(a.CommandLine!);
                    SavedApplications[i] = a with { Location = loc, Parameter = param, CommandLine = null };
                }
            }
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
    [JsonSourceGenerationOptions(WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }
}
