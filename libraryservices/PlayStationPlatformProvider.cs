using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Handles cloud integration for PlayStation Network (trophy titles, platinum detection, base game progress).
    /// </summary>
    public static class PlayStationPlatformProvider
    {
        private static readonly HttpClient Http = new();

        /// <summary>
        /// Queries the PlayStation Network Trophy API using an NPSSO 64-character token.
        /// </summary>
        public static async Task SyncCloudAsync(Dictionary<string, UnifiedGame> library, string npsso)
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
                    string name = title.TryGetProperty("trophyTitleName", out var n) ? n.GetString() ?? "" : "Unknown";
                    string platform = title.TryGetProperty("trophyTitlePlatform", out var p) ? p.GetString() ?? "PSN" : "PSN";
                    int progress = title.TryGetProperty("progress", out var pr) ? pr.GetInt32() : 0;
                    string iconUrl = title.TryGetProperty("trophyTitleIconUrl", out var ic) ? ic.GetString() ?? "" : "";

                    int total = 0, unlocked = 0, earnedPlatinum = 0;
                    if (title.TryGetProperty("definedTrophies", out var def))
                    {
                        total = def.GetProperty("bronze").GetInt32() +
                                def.GetProperty("silver").GetInt32() +
                                def.GetProperty("gold").GetInt32() +
                                def.GetProperty("platinum").GetInt32();
                    }

                    if (title.TryGetProperty("earnedTrophies", out var earn))
                    {
                        earnedPlatinum = earn.GetProperty("platinum").GetInt32();
                        unlocked = earn.GetProperty("bronze").GetInt32() +
                                   earn.GetProperty("silver").GetInt32() +
                                   earn.GetProperty("gold").GetInt32() +
                                   earnedPlatinum;
                    }

                    if (unlocked == 0) continue;

                    // If a Platinum was earned but progress < 100% due to DLCs, fetch base trophy group stats
                    if (earnedPlatinum > 0 && progress < 100 && !string.IsNullOrEmpty(npCommId))
                    {
                        var (bUnl, bTot, bPct) = await GetBaseStatsAsync(accessToken, npCommId, platform);
                        if (bTot > 0)
                        {
                            unlocked = bUnl;
                            total = bTot;
                            progress = bPct;
                        }
                    }

                    var game = PlatformProviders.GetOrCreate(library, name);
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
                        ExtraInfo = earnedPlatinum > 0 ? "🏆 Platinum" : ""
                    };
                }
            }
            catch { }
        }

        private static async Task<(int unlocked, int total, int pct)> GetBaseStatsAsync(string token, string npCommId, string platform)
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
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["token_format"] = "jwt"
            });

            var tokenResp = await Http.SendAsync(tokenReq);
            using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
            return tokenDoc.RootElement.TryGetProperty("access_token", out var prop) ? prop.GetString() : null;
        }
    }
}
