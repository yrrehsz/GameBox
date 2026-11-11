using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Helper responsible for locating, downloading, and locally caching 600x900 vertical game covers
    /// from Steam CDN, SteamGridDB API, or remote URLs.
    /// </summary>
    public static class CoverCacheHelper
    {
        private static readonly HttpClient Http = new();

        /// <summary>
        /// Ensures a local 600x900 vertical cover image exists for the specified game entry,
        /// trying Steam CDN, SteamGridDB API, and original platform URLs in priority order.
        /// </summary>
        public static async Task EnsureCoverCachedAsync(UnifiedGame game, string steamGridDbKey = "")
        {
            if (!string.IsNullOrEmpty(game.LocalCoverPath) && File.Exists(game.LocalCoverPath))
                return;

            string coversDir = Path.Combine(AppConfig.AppDataFolder, "Covers");
            Directory.CreateDirectory(coversDir);

            string localPath = Path.Combine(coversDir, $"{game.NormalizedId}.jpg");

            // 1. If a valid cached file already exists on disk (>1 KB), use it
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 1024)
            {
                game.LocalCoverPath = localPath;
                return;
            }

            // Step A: If the game is already in the Steam platform dictionary, use its AppID
            string? steamAppId = null;
            if (game.Platforms.TryGetValue("Steam", out var steamPlat) &&
                int.TryParse(steamPlat.ExtraInfo, out _))
            {
                steamAppId = steamPlat.ExtraInfo;
            }

            // Step B: For Xbox/PS games without a Steam AppID, search Steam Store with exact title matching
            if (string.IsNullOrEmpty(steamAppId))
            {
                var (foundId, englishName) = await FindExactSteamGameAsync(game.DisplayTitle, game.NormalizedId);
                steamAppId = foundId;

                // If found on Steam, update DisplayTitle with the official English name
                if (!string.IsNullOrEmpty(englishName))
                    game.DisplayTitle = englishName;
            }

            // Step C: Try downloading the vertical 600x900 cover from Steam CDN
            if (!string.IsNullOrEmpty(steamAppId))
            {
                if (await TryDownloadSteamVerticalCoverAsync(steamAppId, localPath))
                {
                    game.LocalCoverPath = localPath;
                    return;
                }
            }

            // Step D: If the game is not on Steam (e.g. Minecraft, Bloodborne), query SteamGridDB
            if (!string.IsNullOrEmpty(steamGridDbKey))
            {
                if (await TryDownloadFromSteamGridDbAsync(game.DisplayTitle, steamGridDbKey, localPath))
                {
                    game.LocalCoverPath = localPath;
                    return;
                }
            }

            // Step E: Final fallback to original platform image URL (forcing HTTPS)
            if (!string.IsNullOrEmpty(game.CoverUrl))
            {
                string safeUrl = game.CoverUrl.Replace("http://", "https://");
                if (await TryDownloadUrlAsync(safeUrl, localPath))
                {
                    game.LocalCoverPath = localPath;
                    return;
                }
            }
        }

        /// <summary>
        /// Searches the Steam Store API for an exact match against targetNormalizedId,
        /// avoiding false-positive sequel matches (e.g. matching Hades II when searching for Hades).
        /// </summary>
        private static async Task<(string? appId, string? officialEnglishName)> FindExactSteamGameAsync(string title, string targetNormalizedId)
        {
            try
            {
                string query = Uri.EscapeDataString(title.Trim());
                string url = $"https://store.steampowered.com/api/storesearch/?term={query}&l=english&cc=US";

                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                    return (null, null);

                // Pass 1: exact normalized key match (e.g. "hades" == "hades", rejecting "hadesii")
                foreach (var item in items.EnumerateArray())
                {
                    string itemName = item.GetProperty("name").GetString() ?? "";
                    var (itemKey, cleanEnglish) = TitleNormalizer.Normalize(itemName);

                    if (itemKey == targetNormalizedId)
                    {
                        return (item.GetProperty("id").GetInt32().ToString(), cleanEnglish);
                    }
                }

                // Pass 2: prefix match only if difference is within 3 characters and no conflicting suffix
                foreach (var item in items.EnumerateArray())
                {
                    string itemName = item.GetProperty("name").GetString() ?? "";
                    var (itemKey, cleanEnglish) = TitleNormalizer.Normalize(itemName);

                    if (itemKey.StartsWith(targetNormalizedId) && Math.Abs(itemKey.Length - targetNormalizedId.Length) <= 3)
                    {
                        return (item.GetProperty("id").GetInt32().ToString(), cleanEnglish);
                    }
                }
            }
            catch { }

            return (null, null);
        }

        /// <summary>
        /// Attempts to download a 600x900 vertical cover image from Steam CDN endpoints (Cloudflare and Akamai).
        /// </summary>
        private static async Task<bool> TryDownloadSteamVerticalCoverAsync(string appId, string localPath)
        {
            string[] candidateUrls = new[]
            {
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg"
            };

            foreach (string url in candidateUrls)
            {
                if (await TryDownloadUrlAsync(url, localPath))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Downloads a 600x900 vertical grid artwork from SteamGridDB for non-Steam titles.
        /// </summary>
        private static async Task<bool> TryDownloadFromSteamGridDbAsync(string gameTitle, string apiKey, string localPath)
        {
            try
            {
                // 1. Search for game ID on SteamGridDB
                string searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameTitle)}";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                var searchResp = await Http.SendAsync(searchReq);
                if (!searchResp.IsSuccessStatusCode) return false;

                using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
                if (!searchDoc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.GetArrayLength() == 0)
                    return false;

                int sgdbGameId = dataArr[0].GetProperty("id").GetInt32();

                // 2. Request official/top-rated 600x900 vertical grids
                string gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{sgdbGameId}?dimensions=600x900&mimes=image/jpeg,image/png";
                using var gridsReq = new HttpRequestMessage(HttpMethod.Get, gridsUrl);
                gridsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                var gridsResp = await Http.SendAsync(gridsReq);
                if (!gridsResp.IsSuccessStatusCode) return false;

                using var gridsDoc = JsonDocument.Parse(await gridsResp.Content.ReadAsStringAsync());
                if (gridsDoc.RootElement.TryGetProperty("data", out var gridsArr) && gridsArr.GetArrayLength() > 0)
                {
                    string imageUrl = gridsArr[0].GetProperty("url").GetString() ?? "";
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        return await TryDownloadUrlAsync(imageUrl, localPath);
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Downloads binary data from a URL and saves it to localPath if response is valid (>2 KB).
        /// </summary>
        private static async Task<bool> TryDownloadUrlAsync(string url, string localPath)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return false;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 2048)
                {
                    await File.WriteAllBytesAsync(localPath, bytes);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
