using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Handles local and cloud integration for Xbox (Xbox PC local installations, OpenXBL achievements).
    /// </summary>
    public static class XboxPlatformProvider
    {
        private static readonly HttpClient Http = new();

        /// <summary>
        /// Queries the OpenXBL API for Xbox achievements, gamerscore, and completion percentages.
        /// </summary>
        public static async Task SyncCloudAsync(Dictionary<string, UnifiedGame> library, string openXblKey)
        {
            if (string.IsNullOrEmpty(openXblKey)) return;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://xbl.io/api/v2/achievements");
                req.Headers.Add("X-Authorization", openXblKey.Trim());
                req.Headers.Add("Accept", "application/json");
                req.Headers.Add("Accept-Language", "en-US");

                var resp = await Http.SendAsync(req);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                JsonElement root = doc.RootElement.TryGetProperty("content", out var c) ? c : doc.RootElement;
                if (!root.TryGetProperty("titles", out var titlesArray)) return;

                foreach (var title in titlesArray.EnumerateArray())
                {
                    string rawName = title.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string displayImg = title.TryGetProperty("displayImage", out var img) ? img.GetString() ?? "" : "";

                    int currentAch = 0, totalAch = 0, currentScore = 0, totalScore = 0;
                    if (title.TryGetProperty("achievement", out var ach))
                    {
                        currentAch = ach.TryGetProperty("currentAchievements", out var ca) ? ca.GetInt32() : 0;
                        totalAch = ach.TryGetProperty("totalAchievements", out var ta) ? ta.GetInt32() : 0;
                        currentScore = ach.TryGetProperty("currentGamerscore", out var cs) ? cs.GetInt32() : 0;
                        totalScore = ach.TryGetProperty("totalGamerscore", out var ts) ? ts.GetInt32() : 0;
                    }

                    if (currentAch == 0) continue;

                    int pct = totalScore > 0 ? (int)Math.Round((double)currentScore / totalScore * 100) : 0;
                    var game = PlatformProviders.GetOrCreate(library, rawName);

                    if (string.IsNullOrEmpty(game.CoverUrl) && !string.IsNullOrEmpty(displayImg))
                        game.CoverUrl = displayImg;

                    if (game.Platforms.TryGetValue("Xbox", out var existingXbox) && existingXbox.Percentage >= pct)
                        continue;

                    game.Platforms["Xbox"] = new PlatformCompletion
                    {
                        PlatformName = "Xbox",
                        SubPlatform = "Xbox/PC",
                        Unlocked = currentAch,
                        Total = totalAch,
                        Percentage = pct,
                        IsCompleted100 = pct == 100,
                        ExtraInfo = $"{currentScore}/{totalScore}G"
                    };
                }
            }
            catch { }
        }

        /// <summary>
        /// Scans local drives and Windows GamingServices registry for installed Xbox PC games.
        /// </summary>
        public static HashSet<string> SyncLocal(Dictionary<string, UnifiedGame> library)
        {
            var installedNormalizedIds = new HashSet<string>();

            try
            {
                // 1. Search for XboxGames and ModifiableWindowsApps directories across all fixed drives
                var candidateGameDirs = new List<string>();

                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    string defaultXboxDir = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                    if (Directory.Exists(defaultXboxDir))
                    {
                        candidateGameDirs.AddRange(Directory.GetDirectories(defaultXboxDir));
                    }

                    string moddableDir = Path.Combine(drive.RootDirectory.FullName, "Program Files", "ModifiableWindowsApps");
                    if (Directory.Exists(moddableDir))
                    {
                        candidateGameDirs.AddRange(Directory.GetDirectories(moddableDir));
                    }
                }

                // 2. Check Windows Registry GamingServices PackageRepository to locate custom installation paths
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
                    if (baseKey != null)
                    {
                        foreach (string subKeyName in baseKey.GetSubKeyNames())
                        {
                            using var subKey = baseKey.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            foreach (string pkgKeyName in subKey.GetSubKeyNames())
                            {
                                using var pkgKey = subKey.OpenSubKey(pkgKeyName);
                                string? rootPath = pkgKey?.GetValue("Root")?.ToString();
                                if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
                                {
                                    candidateGameDirs.Add(rootPath);
                                }
                            }
                        }
                    }
                }
                catch { }

                // 3. For each candidate directory, look for MicrosoftGame.config
                foreach (string gameDir in candidateGameDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? configPath = FindMicrosoftGameConfig(gameDir);
                    if (string.IsNullOrEmpty(configPath)) continue;

                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(configPath);
                        string configFolder = Path.GetDirectoryName(configPath)!;

                        // Extract display title from ShellVisuals -> DefaultDisplayName, or fall back to folder name
                        var shellVisuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                        string rawTitle = shellVisuals?.Attribute("DefaultDisplayName")?.Value ?? "";

                        if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        {
                            rawTitle = new DirectoryInfo(gameDir).Name;
                        }

                        // Skip system packages, DLCs, and runtimes
                        if (rawTitle.Contains("Gaming Services", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Add-on", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Content Pack", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Find the first valid <Executable> node
                        var exeNode = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
                        string? relativeExe = exeNode?.Attribute("Name")?.Value;
                        if (string.IsNullOrEmpty(relativeExe)) continue;

                        string fullExePath = Path.Combine(configFolder, relativeExe);
                        string launchCommand = fullExePath;

                        string gamelaunchHelper = Path.Combine(configFolder, "gamelaunchhelper.exe");
                        if (File.Exists(gamelaunchHelper))
                        {
                            launchCommand = gamelaunchHelper;
                        }
                        else if (!File.Exists(fullExePath))
                        {
                            continue;
                        }

                        var (key, cleanTitle) = TitleNormalizer.Normalize(rawTitle);
                        if (string.IsNullOrEmpty(key) || key == "unknown") continue;

                        if (!library.TryGetValue(key, out var game))
                        {
                            game = new UnifiedGame
                            {
                                NormalizedId = key,
                                DisplayTitle = cleanTitle
                            };
                            library[key] = game;
                        }

                        if (!game.Platforms.ContainsKey("Xbox"))
                        {
                            game.Platforms["Xbox"] = new PlatformCompletion
                            {
                                PlatformName = "Xbox",
                                SubPlatform = "PC"
                            };
                        }

                        if (!game.IsCustomGame || string.IsNullOrEmpty(game.LaunchCommand))
                        {
                            game.ActionType = GameActionType.Executable;
                            game.LaunchCommand = launchCommand;
                        }

                        installedNormalizedIds.Add(key);
                    }
                    catch { }
                }
            }
            catch { }

            return installedNormalizedIds;
        }

        private static string? FindMicrosoftGameConfig(string gameDir)
        {
            try
            {
                // Case 1: directly in the game folder
                string direct = Path.Combine(gameDir, "MicrosoftGame.config");
                if (File.Exists(direct)) return direct;

                // Case 2: inside the standard "Content" subfolder of XboxGames
                string inContent = Path.Combine(gameDir, "Content", "MicrosoftGame.config");
                if (File.Exists(inContent)) return inContent;

                // Case 3: scan 1st-level subdirectories
                foreach (var subDir in Directory.GetDirectories(gameDir))
                {
                    string candidate = Path.Combine(subDir, "MicrosoftGame.config");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            return null;
        }
    }
}
