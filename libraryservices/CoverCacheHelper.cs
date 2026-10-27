using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    public static class CoverCacheHelper
    {
        private static readonly HttpClient Http = new HttpClient();

        public static async Task EnsureCoverCachedAsync(UnifiedGame game, string steamGridDbKey = "")
        {
            string coversDir = Path.Combine(AppConfig.AppDataFolder, "Covers");
            Directory.CreateDirectory(coversDir);

            string localPath = Path.Combine(coversDir, $"{game.NormalizedId}.jpg");

            // 1. Se abbiamo già il file valido su disco, usiamo quello
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 1024)
            {
                game.LocalCoverPath = localPath;
                return;
            }

            // PASSO A: Se il gioco è già su Steam, abbiamo già l'AppID esatto in ExtraInfo!
            string? steamAppId = null;
            if (game.Platforms.TryGetValue("Steam", out var steamPlat) &&
                int.TryParse(steamPlat.ExtraInfo, out _))
            {
                steamAppId = steamPlat.ExtraInfo;
            }

            // PASSO B: Se è un gioco Xbox/PS, cerchiamo l'AppID su Steam con MATCH ESATTO (non prende Hades 2 per Hades!)
            if (string.IsNullOrEmpty(steamAppId))
            {
                var (foundId, englishName) = await FindExactSteamGameAsync(game.DisplayTitle, game.NormalizedId);
                steamAppId = foundId;

                // Se Steam ha trovato il gioco, aggiorniamo anche il titolo con il vero nome originale inglese!
                if (!string.IsNullOrEmpty(englishName))
                    game.DisplayTitle = englishName;
            }

            // PASSO C: Proviamo a scaricare la copertina verticale 600x900 aggiornata da Steam
            if (!string.IsNullOrEmpty(steamAppId))
            {
                if (await TryDownloadSteamVerticalCoverAsync(steamAppId, localPath))
                {
                    game.LocalCoverPath = localPath;
                    return;
                }
            }

            // PASSO D: Se il gioco NON esiste su Steam (es. Minecraft, COD Black Ops 4, Bloodborne),
            // usiamo SteamGridDB per ottenere comunque una copertina 600x900 verticale perfetta!
            if (!string.IsNullOrEmpty(steamGridDbKey))
            {
                if (await TryDownloadFromSteamGridDbAsync(game.DisplayTitle, steamGridDbKey, localPath))
                {
                    game.LocalCoverPath = localPath;
                    return;
                }
            }

            // PASSO E: Ultimo fallback sull'immagine originale (forzando HTTPS per Minecraft!)
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

        // Cerca il gioco sullo Store di Steam confrontando la chiave normalizzata (evita Hades -> Hades II)
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

                // 1° Giro: Cerchiamo la corrispondenza ESATTA (es. "hades" == "hades", scarta "hadesii")
                foreach (var item in items.EnumerateArray())
                {
                    string itemName = item.GetProperty("name").GetString() ?? "";
                    var (itemKey, cleanEnglish) = TitleNormalizer.Normalize(itemName);

                    if (itemKey == targetNormalizedId)
                    {
                        return (item.GetProperty("id").GetInt32().ToString(), cleanEnglish);
                    }
                }

                // 2° Giro: Accettiamo un match parziale solo se uno contiene l'altro E non c'è un numero finale diverso (es. 2, 3, II, III, IV)
                foreach (var item in items.EnumerateArray())
                {
                    string itemName = item.GetProperty("name").GetString() ?? "";
                    var (itemKey, cleanEnglish) = TitleNormalizer.Normalize(itemName);

                    // Evita che cercando "Call of Duty Black Ops 4" prenda "Call of Duty Black Ops III"
                    if (itemKey.StartsWith(targetNormalizedId) && Math.Abs(itemKey.Length - targetNormalizedId.Length) <= 3)
                    {
                        return (item.GetProperty("id").GetInt32().ToString(), cleanEnglish);
                    }
                }
            }
            catch { }

            return (null, null);
        }

        // Scarica la copertina 600x900 da Steam usando il CDN Cloudflare (che non blocca con vecchie versioni Early Access)
        private static async Task<bool> TryDownloadSteamVerticalCoverAsync(string appId, string localPath)
        {
            // Proviamo sia il CDN Cloudflare aggiornato (2x HD e standard) sia Akamai
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

        // Scarica una copertina verticale 600x900 da SteamGridDB per giochi non-Steam (Minecraft, BO4, Bloodborne, ecc.)
        private static async Task<bool> TryDownloadFromSteamGridDbAsync(string gameTitle, string apiKey, string localPath)
        {
            try
            {
                // 1. Cerca l'ID del gioco su SteamGridDB
                string searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameTitle)}";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                var searchResp = await Http.SendAsync(searchReq);
                if (!searchResp.IsSuccessStatusCode) return false;

                using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
                if (!searchDoc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.GetArrayLength() == 0)
                    return false;

                int sgdbGameId = dataArr[0].GetProperty("id").GetInt32();

                // 2. Chiede le griglie verticali 600x900 ufficiali/più votate per quell'ID
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

        private static async Task<bool> TryDownloadUrlAsync(string url, string localPath)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return false;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                // Verifica che non sia un'immagine vuota o corrotta (> 2 KB)
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