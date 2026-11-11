using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Handles local and cloud integration for Steam (installed manifest parsing, owned games, achievements).
    /// </summary>
    public static class SteamPlatformProvider
    {
        private static readonly HttpClient Http = new();
        private static readonly HashSet<string> IgnoredSteamIds = new() { "228980", "250820", "431960" };

        /// <summary>
        /// Fast local scan of Steam installations from Registry and libraryfolders.vdf (~15ms, zero network).
        /// </summary>
        public static HashSet<string> SyncLocal(Dictionary<string, UnifiedGame> library)
        {
            var installedAppIds = new HashSet<string>();
            if (!OperatingSystem.IsWindows()) return installedAppIds;

            // Reset only games managed by Steam before scanning
            foreach (var g in library.Values.Where(x => x.LaunchCommand != null && x.LaunchCommand.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)))
            {
                g.ActionType = GameActionType.DisplayOnly;
            }

            string? steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return installedAppIds;

            var libraryFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(Path.Combine(steamPath, "steamapps"))
            };

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                string vdfContent = File.ReadAllText(vdfPath);
                foreach (Match m in Regex.Matches(vdfContent, "\"path\"\\s+\"([^\"]+)\""))
                {
                    string extraPath = Path.GetFullPath(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps"));
                    if (Directory.Exists(extraPath)) libraryFolders.Add(extraPath);
                }
            }

            foreach (string folder in libraryFolders)
            {
                if (!Directory.Exists(folder)) continue;

                foreach (string acfFile in Directory.GetFiles(folder, "appmanifest_*.acf"))
                {
                    string content = File.ReadAllText(acfFile);
                    string appId = Regex.Match(content, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    string name = Regex.Match(content, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;

                    if (IgnoredSteamIds.Contains(appId)) continue;
                    installedAppIds.Add(appId);

                    var game = PlatformProviders.GetOrCreate(library, name);
                    game.ActionType = GameActionType.Executable;
                    game.LaunchCommand = $"steam://rungameid/{appId}";
                    game.CoverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg";

                    if (!game.Platforms.ContainsKey("Steam"))
                    {
                        game.Platforms["Steam"] = new PlatformCompletion
                        {
                            PlatformName = "Steam",
                            SubPlatform = "PC (Installed)",
                            ExtraInfo = appId
                        };
                    }
                    else
                    {
                        game.Platforms["Steam"].SubPlatform = "PC (Installed)";
                        game.Platforms["Steam"].ExtraInfo = appId;
                    }
                }
            }
            return installedAppIds;
        }

        /// <summary>
        /// Queries the Steam Web API for owned games and player achievement stats.
        /// </summary>
        public static async Task SyncCloudAsync(
            Dictionary<string, UnifiedGame> library,
            string apiKey,
            string steamId,
            HashSet<string> installedAppIds)
        {
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(steamId)) return;

            var gamesToQuery = new List<(string appId, string name, bool isInstalled)>();
            try
            {
                string ownedUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=true&include_played_free_games=true";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(ownedUrl));

                if (doc.RootElement.TryGetProperty("response", out var resp) && resp.TryGetProperty("games", out var gamesArr))
                {
                    foreach (var g in gamesArr.EnumerateArray())
                    {
                        string appId = g.GetProperty("appid").GetInt32().ToString();
                        if (IgnoredSteamIds.Contains(appId)) continue;

                        string name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : $"Steam App {appId}";
                        int playtime = g.TryGetProperty("playtime_forever", out var pt) ? pt.GetInt32() : 0;
                        bool isInst = installedAppIds.Contains(appId);

                        if (isInst || playtime > 0)
                            gamesToQuery.Add((appId, name, isInst));
                    }
                }
            }
            catch { }

            foreach (var g in library.Values.Where(x => x.Platforms.ContainsKey("Steam")))
            {
                string appId = g.Platforms["Steam"].ExtraInfo;
                if (!string.IsNullOrEmpty(appId) && !gamesToQuery.Any(q => q.appId == appId))
                    gamesToQuery.Add((appId, g.DisplayTitle, true));
            }

            using var semaphore = new SemaphoreSlim(6);
            var tasks = gamesToQuery.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var (hasAch, unlocked, total) = await FetchAchievementsAsync(apiKey, steamId, item.appId);
                    if (!item.isInstalled && unlocked == 0) return;

                    lock (library)
                    {
                        var game = PlatformProviders.GetOrCreate(library, item.name);
                        if (string.IsNullOrEmpty(game.CoverUrl))
                            game.CoverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.appId}/library_600x900.jpg";

                        int pct = total > 0 ? (int)Math.Round((double)unlocked / total * 100) : 0;
                        game.Platforms["Steam"] = new PlatformCompletion
                        {
                            PlatformName = "Steam",
                            SubPlatform = item.isInstalled ? "PC (Installed)" : "PC (Cloud)",
                            Unlocked = unlocked,
                            Total = total,
                            Percentage = pct,
                            IsCompleted100 = total > 0 && unlocked == total,
                            ExtraInfo = item.appId
                        };
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task<(bool hasAch, int unlocked, int total)> FetchAchievementsAsync(string apiKey, string steamId, string appId)
        {
            try
            {
                var resp = await Http.GetAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={apiKey}&steamid={steamId}&appid={appId}");
                if (!resp.IsSuccessStatusCode) return (false, 0, 0);

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("playerstats", out var stats) && stats.TryGetProperty("achievements", out var achArray))
                {
                    int total = 0, unlocked = 0;
                    foreach (var a in achArray.EnumerateArray())
                    {
                        total++;
                        if (a.TryGetProperty("achieved", out var ach) && ach.GetInt32() == 1) unlocked++;
                    }
                    return (total > 0, unlocked, total);
                }
            }
            catch { }
            return (false, 0, 0);
        }
    }
}
