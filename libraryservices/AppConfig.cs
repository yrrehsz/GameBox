using System;
using System.IO;
using System.Text.Json;

namespace GameStartMenu.Library
{
    public class AppConfig
    {
        public string SteamApiKey { get; set; } = "";
        public string SteamId64 { get; set; } = "";
        public string PsnNpsso { get; set; } = "";
        public string OpenXblApiKey { get; set; } = "";
        public string SteamGridDbApiKey { get; set; } = "";
        public DateTime LastCloudSyncUtc { get; set; } = DateTime.MinValue;

        // --- NUOVE IMPOSTAZIONI DI VISUALIZZAZIONE E ORDINAMENTO ---
        // Valori possibili: "All", "Executable", "Steam", "PlayStation", "Xbox", "Completed100"
        public string ShowFilter { get; set; } = "All";

        // Valori possibili: "Default" (Eseguibili prima), "Alphabetical", "Platinums", "CompletionPct"
        public string OrderBy { get; set; } = "Default";

        // Modalità di sincronizzazione: "Hourly" (Ogni ora), "Startup" (Solo all'avvio), "Manual" (Solo manuale)
        public string SyncFrequency { get; set; } = "Hourly";

        // Lista dei NormalizedId rimossi dall'utente, così il Sync non li fa riapparire!
        public HashSet<string> HiddenGames { get; set; } = new();

        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameStartMenu");

        public static string ConfigFilePath => Path.Combine(AppDataFolder, "settings.json");
        public static string CacheFilePath => Path.Combine(AppDataFolder, "library_cache.json");

        public Dictionary<string, string> ControllerIcons { get; set; } = new();

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
            defaultConfig.Save(); // Crea il file vuoto al primo avvio
            return defaultConfig;
        }

        public void Save()
        {
            Directory.CreateDirectory(AppDataFolder);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, options));
        }
    }
}