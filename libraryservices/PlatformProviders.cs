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

        public static HashSet<string> SyncXboxLocal(Dictionary<string, UnifiedGame> library)
        {
            var installedNormalizedIds = new HashSet<string>();

            try
            {
                // 1. Cerca le cartelle "XboxGames" (e sottocartelle custom) su tutti i dischi fissi (C:\, D:\, E:\...)
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

                // 2. Controlla anche nel Registro di Windows (GamingServices\PackageRepository)
                // così trova i giochi Xbox anche se l'utente li ha installati in una cartella con nome personalizzato!
                try
                {
                    using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
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

                // 3. Per ogni cartella gioco trovata, cerca MicrosoftGame.config (al massimo al 1° livello, es. \Content\)
                // Questo evita di scansionare le 50 sottocartelle di Call of Duty!
                foreach (string gameDir in candidateGameDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? configPath = FindMicrosoftGameConfig(gameDir);
                    if (string.IsNullOrEmpty(configPath)) continue;

                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(configPath);
                        string configFolder = Path.GetDirectoryName(configPath)!;

                        // Estrae il nome del gioco (da ShellVisuals -> DefaultDisplayName, oppure usa il nome della cartella principale)
                        var shellVisuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                        string rawTitle = shellVisuals?.Attribute("DefaultDisplayName")?.Value ?? "";

                        // Se DefaultDisplayName è una risorsa ms-resource:// o è vuoto, usiamo il nome pulito della cartella in XboxGames!
                        if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        {
                            rawTitle = new DirectoryInfo(gameDir).Name;
                        }

                        // Ignora pacchetti di sistema / DLC / Runtime
                        if (rawTitle.Contains("Gaming Services", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Add-on", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Content Pack", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Trova il primo nodo <Executable> valido dentro MicrosoftGame.config
                        var exeNode = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
                        string? relativeExe = exeNode?.Attribute("Name")?.Value;
                        if (string.IsNullOrEmpty(relativeExe)) continue;

                        string fullExePath = Path.Combine(configFolder, relativeExe);

                        // Verifica se possiamo costruire l'URI di avvio UWP/AppX (più affidabile per i giochi Xbox con DRM)
                        // oppure usiamo il percorso diretto del gamelaunchhelper.exe / .exe principale
                        string launchCommand = fullExePath;

                        string gamelaunchHelper = Path.Combine(configFolder, "gamelaunchhelper.exe");
                        if (File.Exists(gamelaunchHelper))
                        {
                            // Su Xbox PC quasi tutti i giochi moderni (incluso COD) usano gamelaunchhelper.exe nella root Content!
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

                        // Assicura che compaia il badge Xbox sul gioco
                        if (!game.Platforms.ContainsKey("Xbox"))
                        {
                            game.Platforms["Xbox"] = new PlatformCompletion
                            {
                                PlatformName = "Xbox",
                                SubPlatform = "PC"
                            };
                        }

                        // Imposta come Eseguibile (a meno che l'utente non abbia già personalizzato l'eseguibile in un gioco custom)
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
                // Caso 1: direttamente nella cartella (es. registro GamingServices punta già a \Content)
                string direct = Path.Combine(gameDir, "MicrosoftGame.config");
                if (File.Exists(direct)) return direct;

                // Caso 2: dentro la sottocartella standard "Content" di XboxGames (es. C:\XboxGames\Halo Infinite\Content\MicrosoftGame.config)
                string inContent = Path.Combine(gameDir, "Content", "MicrosoftGame.config");
                if (File.Exists(inContent)) return inContent;

                // Caso 3: controlla solo le sottocartelle di 1° livello
                foreach (var subDir in Directory.GetDirectories(gameDir))
                {
                    string candidate = Path.Combine(subDir, "MicrosoftGame.config");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            return null;
        }

        // Verifica tutti i giochi segnati come Executable: se sono stati disinstallati da Steam o da Xbox PC,
        // li riporta automaticamente a DisplayOnly!
        public static void CleanupUninstalledGames(
            Dictionary<string, UnifiedGame> library,
            HashSet<string> installedSteamIds,
            HashSet<string> installedXboxIds)
        {
            foreach (var game in library.Values)
            {
                if (game.ActionType != GameActionType.Executable)
                    continue;

                // Se è un gioco aggiunto manualmente dall'utente, verifichiamo solo se il file .exe esiste ancora
                if (game.IsCustomGame)
                {
                    if (!string.IsNullOrEmpty(game.LaunchCommand) &&
                        !game.LaunchCommand.Contains("://") &&
                        !File.Exists(game.LaunchCommand))
                    {
                        game.ActionType = GameActionType.DisplayOnly;
                        game.LaunchCommand = null;
                    }
                    continue;
                }

                string cmd = game.LaunchCommand ?? "";

                // CASO 1: Gioco Steam (usa l'URI steam://rungameid/...)
                if (cmd.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                {
                    string steamAppId = cmd.Split('/').LastOrDefault() ?? "";
                    if (!installedSteamIds.Contains(steamAppId) && !installedSteamIds.Contains(game.NormalizedId))
                    {
                        game.ActionType = GameActionType.DisplayOnly;
                        game.LaunchCommand = null;
                    }
                }
                // CASO 2: Gioco Xbox PC o eseguibile locale su disco
                else if (!string.IsNullOrEmpty(cmd))
                {
                    // Se non è più tra i giochi Xbox rilevati E il file .exe non esiste più su disco, è stato disinstallato!
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