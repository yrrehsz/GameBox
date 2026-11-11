using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Facade coordinating local and cloud synchronizations across gaming platforms
    /// (Steam, PlayStation Network, Xbox Live) and performing post-sync cleanups.
    /// </summary>
    public static class PlatformProviders
    {
        // =====================================================================
        // Steam Provider Delegations
        // =====================================================================

        /// <summary>Performs a fast local scan of installed Steam games from Registry and ACF manifests.</summary>
        public static HashSet<string> SyncSteamLocal(Dictionary<string, UnifiedGame> library) =>
            SteamPlatformProvider.SyncLocal(library);

        /// <summary>Queries the Steam Web API for owned games and achievement statistics.</summary>
        public static Task SyncSteamCloudAsync(
            Dictionary<string, UnifiedGame> library,
            string apiKey,
            string steamId,
            HashSet<string> installedAppIds) =>
            SteamPlatformProvider.SyncCloudAsync(library, apiKey, steamId, installedAppIds);

        // =====================================================================
        // PlayStation Provider Delegations
        // =====================================================================

        /// <summary>Queries the PlayStation Network Trophy API using NPSSO credentials.</summary>
        public static Task SyncPlayStationAsync(Dictionary<string, UnifiedGame> library, string npsso) =>
            PlayStationPlatformProvider.SyncCloudAsync(library, npsso);

        // =====================================================================
        // Xbox Provider Delegations
        // =====================================================================

        /// <summary>Scans local drives and Windows GamingServices registry for installed Xbox PC games.</summary>
        public static HashSet<string> SyncXboxLocal(Dictionary<string, UnifiedGame> library) =>
            XboxPlatformProvider.SyncLocal(library);

        /// <summary>Queries the OpenXBL API for Xbox achievements, gamerscore, and completion percentages.</summary>
        public static Task SyncXboxAsync(Dictionary<string, UnifiedGame> library, string openXblKey) =>
            XboxPlatformProvider.SyncCloudAsync(library, openXblKey);

        // =====================================================================
        // Library Helpers & Maintenance
        // =====================================================================

        /// <summary>
        /// Retrieves an existing game entry or creates a new one using the normalized key.
        /// </summary>
        internal static UnifiedGame GetOrCreate(Dictionary<string, UnifiedGame> library, string rawTitle)
        {
            var (key, cleanTitle) = TitleNormalizer.Normalize(rawTitle);
            if (!library.TryGetValue(key, out var game))
            {
                game = new UnifiedGame { NormalizedId = key, DisplayTitle = cleanTitle };
                library[key] = game;
            }
            return game;
        }

        /// <summary>
        /// Checks all games marked as Executable: if they were uninstalled from Steam or Xbox PC,
        /// demotes them to DisplayOnly. User-managed custom games are preserved.
        /// </summary>
        public static void CleanupUninstalledGames(
            Dictionary<string, UnifiedGame> library,
            HashSet<string> installedSteamIds,
            HashSet<string> installedXboxIds)
        {
            foreach (var game in library.Values)
            {
                if (game.ActionType != GameActionType.Executable)
                    continue;

                // Custom games are managed entirely by the user through settings.
                // ActionType is re-derived from LaunchCommand on load, so skip them here.
                if (game.IsCustomGame)
                    continue;

                string cmd = game.LaunchCommand ?? "";

                // Case 1: Steam game (uses steam://rungameid/...)
                if (cmd.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                {
                    string steamAppId = cmd.Split('/').LastOrDefault() ?? "";
                    if (!installedSteamIds.Contains(steamAppId) && !installedSteamIds.Contains(game.NormalizedId))
                    {
                        game.ActionType = GameActionType.DisplayOnly;
                        game.LaunchCommand = null;
                    }
                }
                // Case 2: Xbox PC game or local executable
                else if (!string.IsNullOrEmpty(cmd))
                {
                    if (!installedXboxIds.Contains(game.NormalizedId) && !File.Exists(cmd))
                    {
                        game.ActionType = GameActionType.DisplayOnly;
                        game.LaunchCommand = null;
                    }
                }
            }
        }
    }
}
