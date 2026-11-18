using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace GameBox.Library
{
    /// <summary>
    /// Detects games installed via Ubisoft Connect by reading the launcher's registry entries.
    /// </summary>
    internal static class UbisoftPlatformProvider
    {
        private const string InstallsKey = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
        private const string UninstallKeyBase = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<UnifiedGame> GetInstalledGames()
        {
            var games = new List<UnifiedGame>();

            try
            {
                using RegistryKey? installsRoot = Registry.LocalMachine.OpenSubKey(InstallsKey);
                if (installsRoot == null)
                    return games;

                foreach (string gameId in installsRoot.GetSubKeyNames())
                {
                    try
                    {
                        UnifiedGame? game = ReadGame(installsRoot, gameId);
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

        private static UnifiedGame? ReadGame(RegistryKey installsRoot, string gameId)
        {
            using RegistryKey? gameKey = installsRoot.OpenSubKey(gameId);
            if (gameKey == null)
                return null;

            string? installDir = gameKey.GetValue("InstallDir") as string;
            if (string.IsNullOrEmpty(installDir))
                return null;

            string displayName = ResolveDisplayName(gameId, installDir);
            if (string.IsNullOrEmpty(displayName))
                return null;

            string launchCommand = $"uplay://launch/{gameId}/0";
            var (normalizedId, cleanTitle) = TitleNormalizer.Normalize(displayName);
            if (string.IsNullOrEmpty(normalizedId) || normalizedId == "unknown")
                normalizedId = "ubisoft_" + gameId;

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

        private static string ResolveDisplayName(string gameId, string installDir)
        {
            // Primary source: Windows Uninstall registry entry written by the Ubisoft installer.
            try
            {
                string uninstallSubKey = $@"{UninstallKeyBase}\Uplay Install {gameId}";
                using RegistryKey? uninstallKey = Registry.LocalMachine.OpenSubKey(uninstallSubKey);
                if (uninstallKey != null)
                {
                    string? name = uninstallKey.GetValue("DisplayName") as string;
                    if (!string.IsNullOrWhiteSpace(name))
                        return name.Trim();
                }
            }
            catch { }

            // Fallback: derive a name from the install directory's folder name.
            if (!string.IsNullOrEmpty(installDir))
            {
                string folderName = Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar,
                                                                         Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(folderName))
                    return folderName;
            }

            return string.Empty;
        }
    }
}
