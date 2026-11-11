using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Application settings and cloud credentials, persisted in %AppData%\GameStartMenu\settings.json.
    /// </summary>
    public class AppConfig
    {
        // =====================================================================
        // Cloud API Credentials
        // =====================================================================

        public string SteamApiKey { get; set; } = "";
        public string SteamId64 { get; set; } = "";
        public string PsnNpsso { get; set; } = "";
        public string OpenXblApiKey { get; set; } = "";
        public string SteamGridDbApiKey { get; set; } = "";
        public DateTime LastCloudSyncUtc { get; set; } = DateTime.MinValue;

        // =====================================================================
        // View & Sorting Preferences
        // =====================================================================

        /// <summary>
        /// Active library filter(s) as a comma-separated list (OR union).
        /// Supported tokens: "All", "Executable", "Steam", "PlayStation", "Xbox", "Completed100".
        /// </summary>
        public string ShowFilter { get; set; } = "All";

        /// <summary>
        /// Library sort order. Supported values: "Default" (Executable first), "Alphabetical", "Platinums", "CompletionPct".
        /// </summary>
        public string OrderBy { get; set; } = "Default";

        /// <summary>
        /// Comma-separated platform priority order for 100% completion overlays (e.g. "Xbox,PlayStation,Steam" or "None").
        /// </summary>
        public string OverlayPriority { get; set; } = "Xbox,PlayStation,Steam";

        // =====================================================================
        // Synchronization & Hidden Items
        // =====================================================================

        /// <summary>
        /// Sync schedule mode: "Hourly" (every 60 minutes), "Startup" (on launch only), "Manual" (user-triggered only).
        /// </summary>
        public string SyncFrequency { get; set; } = "Hourly";

        /// <summary>
        /// Set of normalized IDs explicitly removed by the user to prevent cloud sync from re-adding them.
        /// </summary>
        public HashSet<string> HiddenGames { get; set; } = new();

        /// <summary>
        /// Controller icon customizations mapped by platform or device key.
        /// </summary>
        public Dictionary<string, string> ControllerIcons { get; set; } = new();

        // =====================================================================
        // Storage Paths
        // =====================================================================

        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameStartMenu");

        public static string ConfigFilePath => Path.Combine(AppDataFolder, "settings.json");
        public static string CacheFilePath => Path.Combine(AppDataFolder, "library_cache.json");

        // =====================================================================
        // Persistence
        // =====================================================================

        /// <summary>
        /// Loads settings from disk, or creates and returns a default instance if the file is missing or invalid.
        /// </summary>
        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch { }

            var defaultConfig = new AppConfig();
            defaultConfig.Save();
            return defaultConfig;
        }

        /// <summary>
        /// Persists current settings to %AppData%\GameStartMenu\settings.json with indented formatting.
        /// </summary>
        public void Save()
        {
            Directory.CreateDirectory(AppDataFolder);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, options));
        }
    }
}
