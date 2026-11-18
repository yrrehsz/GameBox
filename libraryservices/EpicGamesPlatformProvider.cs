using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace GameBox.Library
{
    /// <summary>
    /// Detects games installed via the Epic Games Store by reading .item manifest files.
    /// </summary>
    internal static class EpicGamesPlatformProvider
    {
        private const string RegistryKey = @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher";
        private const string FallbackManifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

        public static List<UnifiedGame> GetInstalledGames()
        {
            var games = new List<UnifiedGame>();

            try
            {
                string manifestsFolder = ResolveManifestsFolder();
                if (string.IsNullOrEmpty(manifestsFolder) || !Directory.Exists(manifestsFolder))
                    return games;

                foreach (string itemFile in Directory.GetFiles(manifestsFolder, "*.item"))
                {
                    try
                    {
                        UnifiedGame? game = ParseManifest(itemFile);
                        if (game != null)
                            games.Add(game);
                    }
                    catch
                    {
                        // Skip malformed manifests silently.
                    }
                }
            }
            catch
            {
                // Provider failure must not crash the application.
            }

            return games;
        }

        private static string ResolveManifestsFolder()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKey);
                if (key != null)
                {
                    string? appDataPath = key.GetValue("AppDataPath") as string;
                    if (!string.IsNullOrEmpty(appDataPath))
                    {
                        string path = Path.Combine(appDataPath, "Manifests");
                        if (Directory.Exists(path))
                            return path;
                    }
                }
            }
            catch { }

            return FallbackManifestsPath;
        }

        private static UnifiedGame? ParseManifest(string filePath)
        {
            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Skip incomplete installs.
            if (root.TryGetProperty("bIsIncompleteInstall", out JsonElement incomplete) &&
                incomplete.GetBoolean())
                return null;

            // Retrieve required fields.
            string displayName = root.TryGetProperty("DisplayName", out JsonElement dn)
                ? dn.GetString() ?? string.Empty
                : string.Empty;

            string appName = root.TryGetProperty("AppName", out JsonElement an)
                ? an.GetString() ?? string.Empty
                : string.Empty;

            string mainGameAppName = root.TryGetProperty("MainGameAppName", out JsonElement mg)
                ? mg.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(appName))
                return null;

            // Skip DLC and add-ons: they share a parent's AppName.
            if (!string.IsNullOrEmpty(mainGameAppName) && mainGameAppName != appName)
                return null;

            // Skip entries not categorised as games.
            if (root.TryGetProperty("AppCategories", out JsonElement categories))
            {
                bool isGame = false;
                foreach (JsonElement cat in categories.EnumerateArray())
                {
                    string? catStr = cat.GetString();
                    if (catStr != null && catStr.Contains("games", StringComparison.OrdinalIgnoreCase))
                    {
                        isGame = true;
                        break;
                    }
                }
                if (!isGame)
                    return null;
            }

            string launchCommand = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true";
            var (normalizedId, cleanTitle) = TitleNormalizer.Normalize(displayName);
            if (string.IsNullOrEmpty(normalizedId) || normalizedId == "unknown")
                normalizedId = "epic_" + appName;

            return new UnifiedGame
            {
                NormalizedId = normalizedId,
                DisplayTitle = cleanTitle,
                ActionType = GameActionType.Executable,
                LaunchCommand = launchCommand,
                Platforms = new Dictionary<string, PlatformCompletion>(),
                IsCustomGame = false
            };
        }
    }
}
