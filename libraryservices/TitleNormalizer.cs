using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Utility class that normalizes game titles into alphanumeric lookup keys
    /// and clean English display names, stripping platform-specific suffixes
    /// and fixing localized titles.
    /// </summary>
    public static class TitleNormalizer
    {
        private static readonly string[] SuffixesToRemove = new[]
        {
            @"-\s*Xbox Series X\|S",
            @"\(Xbox Series X\|S\)",
            @"\(Xbox One\)",
            @"Xbox One Edition",
            @"Xbox 360 Edition",
            @"Xbox 360",
            @"for Windows",
            @"per Android",
            @"-\s*Windows 10",
            @":\s*Save the World",
            @":\s*Edizione Cuore di Vuoto", // Hollow Knight: Voidheart Edition (Italian Xbox)
            @":\s*Voidheart Edition",
            @":\s*Definitive Edition",
            @"Definitive Edition",
            @":\s*20\d\d Edition"
        };

        /// <summary>
        /// Mapping to restore original English titles when Xbox Live returns localized Italian names.
        /// </summary>
        private static readonly Dictionary<string, string> ItalianToEnglishFixes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Crash Bandicoot Trilogia N. Sane"] = "Crash Bandicoot N. Sane Trilogy",
            ["Injustice: Dei fra noi"] = "Injustice: Gods Among Us",
            ["SOUTH PARK: SCONTRI DI-RETTI"] = "South Park: The Fractured But Whole",
            ["La Mia Amica Peppa Pig"] = "My Friend Peppa Pig",
            ["Il Dominio Sui Mutanti"] = "Mutant Year Zero: Road to Eden",
            ["Le fantastiche avventure di Captain Spirit"] = "The Awesome Adventures of Captain Spirit"
        };

        /// <summary>
        /// Normalizes a raw game title, producing an alphanumeric key and a clean display title.
        /// </summary>
        /// <param name="rawTitle">The raw game title from platform APIs or filenames.</param>
        /// <returns>A tuple of (normalizedKey, cleanDisplayTitle).</returns>
        public static (string normalizedKey, string cleanDisplayTitle) Normalize(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return ("unknown", "Unknown");

            string title = rawTitle.Trim()
                .Replace("®", "")
                .Replace("™", "")
                .Replace("©", "")
                .Replace("’", "'")
                .Replace("‘", "'");

            title = Regex.Replace(title, @"^COD\s*:\s*", "Call of Duty: ", RegexOptions.IgnoreCase);

            foreach (var pattern in SuffixesToRemove)
            {
                title = Regex.Replace(title, pattern, "", RegexOptions.IgnoreCase).Trim();
            }

            title = title.TrimEnd('-', ':', ' ');

            // Restore the original English title if Xbox returned a translated title
            if (ItalianToEnglishFixes.TryGetValue(title, out string? englishTitle))
            {
                title = englishTitle;
            }

            string key = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]", "");
            return (key, title);
        }
    }
}
