using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace GameBox.Library
{
    /// <summary>
    /// Detects Blizzard games installed via Battle.net by scanning the Windows Uninstall registry.
    /// </summary>
    internal static class BattleNetPlatformProvider
    {
        private const string UninstallKey =
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>
        /// Display names of Blizzard registry entries that represent the launcher or tools,
        /// not actual games, and must be excluded from the library.
        /// </summary>
        private static readonly HashSet<string> LauncherEntries = new(StringComparer.OrdinalIgnoreCase)
        {
            "Battle.net",
            "Battle.net App",
            "Battle.net Update Agent",
            "Blizzard Battle.net",
        };

        /// <summary>
        /// Maps known Blizzard game display names to their Battle.net protocol product codes.
        /// </summary>
        private static readonly Dictionary<string, string> ProductCodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "World of Warcraft",          "WoW"  },
                { "Diablo IV",                  "Fen"  },
                { "Diablo III",                 "D3"   },
                { "Diablo Immortal",            "ANBS" },
                { "Overwatch 2",                "Pro"  },
                { "Hearthstone",                "WTCG" },
                { "StarCraft II",               "S2"   },
                { "StarCraft Remastered",       "S1"   },
                { "Heroes of the Storm",        "HB1"  },
                { "Call of Duty",               "VIPR" },
                { "Warcraft Rumble",            "GRY"  },
                { "Warcraft III: Reforged",     "W3"   },
            };

        public static List<UnifiedGame> GetInstalledGames()
        {
            var games = new List<UnifiedGame>();

            try
            {
                using RegistryKey? uninstallRoot = Registry.LocalMachine.OpenSubKey(UninstallKey);
                if (uninstallRoot == null)
                    return games;

                foreach (string subKeyName in uninstallRoot.GetSubKeyNames())
                {
                    try
                    {
                        UnifiedGame? game = ReadGame(uninstallRoot, subKeyName);
                        if (game != null)
                            games.Add(game);
                    }
                    catch
                    {
                        // Skip entries that cannot be read.
                    }
                }
            }
            catch
            {
                // Provider failure must not crash the application.
            }

            return games;
        }

        private static UnifiedGame? ReadGame(RegistryKey uninstallRoot, string subKeyName)
        {
            using RegistryKey? entry = uninstallRoot.OpenSubKey(subKeyName);
            if (entry == null)
                return null;

            string? publisher = entry.GetValue("Publisher") as string;
            if (publisher == null ||
                !publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
                return null;

            string? displayName = entry.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            displayName = displayName.Trim();

            // Exclude the launcher itself and other Blizzard tools.
            if (LauncherEntries.Contains(displayName))
                return null;

            string launchCommand = ResolvelaunchCommand(displayName);

            var (normalizedId, cleanTitle) = TitleNormalizer.Normalize(displayName);
            if (string.IsNullOrEmpty(normalizedId) || normalizedId == "unknown")
                normalizedId = "battlenet_" + subKeyName;

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

        private static string ResolvelaunchCommand(string displayName)
        {
            // Check for an exact match first.
            if (ProductCodes.TryGetValue(displayName, out string? code))
                return $"battlenet://{code}";

            // Check for a partial match (e.g. "Call of Duty: Modern Warfare" → VIPR).
            foreach (KeyValuePair<string, string> pair in ProductCodes)
            {
                if (displayName.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    return $"battlenet://{pair.Value}";
            }

            // No known product code — open the Battle.net launcher as a fallback.
            return "battlenet://";
        }
    }
}
