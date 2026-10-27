using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameStartMenu.Library
{
    public static class PlatformProviders
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly HashSet<string> IgnoredSteamIds = new() { "228980", "250820", "431960" };

        // 1. SCANSIONE VELOCE LOCALE STEAM (Impiega ~15ms, zero rete)
        public static HashSet<string> SyncSteamLocal(Dictionary<string, UnifiedGame> library)
        {
            var installedAppIds = new HashSet<string>();
            if (!OperatingSystem.IsWindows()) return installedAppIds;

            // Resetta solo i giochi automatici di Steam, senza MAI toccare i giochi esterni aggiunti a mano!
            foreach (var g in library.Values.Where(x => !x.IsCustomGame))
                g.ActionType = GameActionType.DisplayOnly;

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
                foreach (string acfFile in Directory.GetFiles(folder, "appmanifest_*.acf"))
                {
                    string content = File.ReadAllText(acfFile);
                    string appId = Regex.Match(content, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    string name = Regex.Match(content, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;

                    if (IgnoredSteamIds.Contains(appId)) continue;
                    installedAppIds.Add(appId);

                    var game = GetOrCreate(library, name);
                    game.ActionType = GameActionType.Executable;
                    game.LaunchCommand = $"steam://rungameid/{appId}";
                    game.CoverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg";

                    if (!game.Platforms.ContainsKey("Steam"))
                    {
                        game.Platforms["Steam"] = new PlatformCompletion
                        {
                            PlatformName = "Steam",
                            SubPlatform = "PC (Installato)",
                            ExtraInfo = appId
                        };
                    }
                    else
                    {
                        game.Platforms["Steam"].SubPlatform = "PC (Installato)";
                        game.Platforms["Steam"].ExtraInfo = appId;
                    }
                }
            }
            return installedAppIds;
        }

        // 2. SCANSIONE CLOUD STEAM
        public static async Task SyncSteamCloudAsync(Dictionary<string, UnifiedGame> library, string apiKey, string steamId, HashSet<string> installedAppIds)
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
                    var (hasAch, unlocked, total) = await FetchSteamAchAsync(apiKey, steamId, item.appId);
                    if (!item.isInstalled && unlocked == 0) return;

                    lock (library)
                    {
                        var game = GetOrCreate(library, item.name);
                        if (string.IsNullOrEmpty(game.CoverUrl))
                            game.CoverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.appId}/library_600x900.jpg";

                        int pct = total > 0 ? (int)Math.Round((double)unlocked / total * 100) : 0;
                        game.Platforms["Steam"] = new PlatformCompletion
                        {
                            PlatformName = "Steam",
                            SubPlatform = item.isInstalled ? "PC (Installato)" : "PC (Cloud)",
                            Unlocked = unlocked,
                            Total = total,
                            Percentage = pct,
                            IsCompleted100 = total > 0 && unlocked == total,
                            ExtraInfo = item.appId
                        };
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task<(bool hasAch, int unlocked, int total)> FetchSteamAchAsync(string apiKey, string steamId, string appId)
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

        // 3. SCANSIONE CLOUD PLAYSTATION NETWORK
        public static async Task SyncPlayStationAsync(Dictionary<string, UnifiedGame> library, string npsso)
        {
            if (string.IsNullOrEmpty(npsso)) return;
            try
            {
                string? accessToken = await GetPsnTokenAsync(npsso);
                if (string.IsNullOrEmpty(accessToken)) return;

                var req = new HttpRequestMessage(HttpMethod.Get, "https://m.np.playstation.com/api/trophy/v1/users/me/trophyTitles?limit=100");
                
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.Add("User-Agent", "PlayStation/21090100 CFNetwork/1126 Darwin/19.5.0");
                req.Headers.Add("Accept-Language", "en-US");

                var resp = await Http.SendAsync(req);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("trophyTitles", out var titlesArray)) return;

                foreach (var title in titlesArray.EnumerateArray())
                {
                    string npCommId = title.TryGetProperty("npCommunicationId", out var npc) ? npc.GetString() ?? "" : "";
                    string name = title.TryGetProperty("trophyTitleName", out var n) ? n.GetString() ?? "" : "Sconosciuto";
                    string platform = title.TryGetProperty("trophyTitlePlatform", out var p) ? p.GetString() ?? "PSN" : "PSN";
                    int progress = title.TryGetProperty("progress", out var pr) ? pr.GetInt32() : 0;
                    string iconUrl = title.TryGetProperty("trophyTitleIconUrl", out var ic) ? ic.GetString() ?? "" : "";

                    int total = 0, unlocked = 0, earnedPlatinum = 0;
                    if (title.TryGetProperty("definedTrophies", out var def))
                        total = def.GetProperty("bronze").GetInt32() + def.GetProperty("silver").GetInt32() + def.GetProperty("gold").GetInt32() + def.GetProperty("platinum").GetInt32();

                    if (title.TryGetProperty("earnedTrophies", out var earn))
                    {
                        earnedPlatinum = earn.GetProperty("platinum").GetInt32();
                        unlocked = earn.GetProperty("bronze").GetInt32() + earn.GetProperty("silver").GetInt32() + earn.GetProperty("gold").GetInt32() + earnedPlatinum;
                    }

                    if (unlocked == 0) continue;

                    if (earnedPlatinum > 0 && progress < 100 && !string.IsNullOrEmpty(npCommId))
                    {
                        var (bUnl, bTot, bPct) = await GetPsnBaseStatsAsync(accessToken, npCommId, platform);
                        if (bTot > 0) { unlocked = bUnl; total = bTot; progress = bPct; }
                    }

                    var game = GetOrCreate(library, name);
                    if (string.IsNullOrEmpty(game.CoverUrl) && !string.IsNullOrEmpty(iconUrl))
                        game.CoverUrl = iconUrl;

                    game.Platforms["PlayStation"] = new PlatformCompletion
                    {
                        PlatformName = "PlayStation",
                        SubPlatform = platform,
                        Unlocked = unlocked,
                        Total = total,
                        Percentage = progress,
                        IsCompleted100 = earnedPlatinum > 0 || progress == 100,
                        ExtraInfo = earnedPlatinum > 0 ? "🏆 Platino" : ""
                    };
                }
            }
            catch { }
        }

        private static async Task<(int unlocked, int total, int pct)> GetPsnBaseStatsAsync(string token, string npCommId, string platform)
        {
            try
            {
                string param = platform.Contains("PS5") ? "" : "?npServiceName=trophy";
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://m.np.playstation.com/api/trophy/v1/users/me/npCommunicationIds/{npCommId}/trophyGroups{param}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "PlayStation/21090100 CFNetwork/1126 Darwin/19.5.0");

                var resp = await Http.SendAsync(req);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("trophyGroups", out var groups))
                {
                    foreach (var g in groups.EnumerateArray())
                    {
                        if (g.TryGetProperty("trophyGroupId", out var id) && id.GetString() == "default")
                        {
                            int prog = g.TryGetProperty("progress", out var p) ? p.GetInt32() : 100;
                            var def = g.GetProperty("definedTrophies");
                            var earn = g.GetProperty("earnedTrophies");
                            int tot = def.GetProperty("bronze").GetInt32() + def.GetProperty("silver").GetInt32() + def.GetProperty("gold").GetInt32() + def.GetProperty("platinum").GetInt32();
                            int unl = earn.GetProperty("bronze").GetInt32() + earn.GetProperty("silver").GetInt32() + earn.GetProperty("gold").GetInt32() + earn.GetProperty("platinum").GetInt32();
                            return (unl, tot, prog);
                        }
                    }
                }
            }
            catch { }
            return (0, 0, 100);
        }

        private static async Task<string?> GetPsnTokenAsync(string npsso)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Cookie", $"npsso={npsso.Trim()}");

            string redirectUri = "com.scee.psxandroid.scecompcall://redirect";
            var authResp = await client.GetAsync(
                $"https://ca.account.sony.com/api/authz/v3/oauth/authorize?access_type=offline&client_id=09515159-7237-4370-9b40-3806e67c0891&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope=psn%3Amobile.v2.core%20psn%3Aclientapp");

            string code = Regex.Match(authResp.Headers.Location?.ToString() ?? "", "code=([^&]+)").Groups[1].Value;
            if (string.IsNullOrEmpty(code)) return null;

            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://ca.account.sony.com/api/authz/v3/oauth/token");
            tokenReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", "MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=");
            tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["token_format"] = "jwt"
            });

            var tokenResp = await Http.SendAsync(tokenReq);
            using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
            return tokenDoc.RootElement.TryGetProperty("access_token", out var prop) ? prop.GetString() : null;
        }

        // 4. SCANSIONE CLOUD XBOX LIVE
        public static async Task SyncXboxAsync(Dictionary<string, UnifiedGame> library, string openXblKey)
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
                    var game = GetOrCreate(library, rawName);

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

        private static UnifiedGame GetOrCreate(Dictionary<string, UnifiedGame> library, string rawTitle)
        {
            var (key, cleanTitle) = TitleNormalizer.Normalize(rawTitle);
            if (!library.TryGetValue(key, out var game))
            {
                game = new UnifiedGame { NormalizedId = key, DisplayTitle = cleanTitle };
                library[key] = game;
            }
            return game;
        }
    }
}