using System;
using System.Collections.Generic;
using System.IO;

namespace Sts2Headless
{
    /// <Todo>
    /// Card types (attack/skill/power/curse/quest/...) aren't tokenized yet
    /// </Todo>
    public class Tokenizer
    {
        public const int UnknownToken = 0;

        private static readonly string[] CategoryOrder =
        {
            "afflictions", "acts", "card_keywords", "cards", "enchantments", "encounters",
            "events", "intents", "monsters", "orbs", "potions", "powers", "relics",
        };

        // Both LoadCategory and Encode uppercase category+id before building a key (see
        // ToKey below), so this can stay a plain ordinal dictionary -- every key that reaches it
        // is already normalized, rather than relying on a case-insensitive comparer to paper over
        // mismatched casing between the two sides.
        private readonly Dictionary<string, int> _tokenMap = new();
        private readonly Dictionary<int, string> _reverseTokenMap = new();
        private int _nextToken = 1; // 0 reserved for UNKNOWN

        public int Count => _tokenMap.Count + 1;

        public Tokenizer(string? localizationDir = null)
        {
            _reverseTokenMap[UnknownToken] = "UNKNOWN";
            var dir = localizationDir ?? DefaultLocalizationDir();
            foreach (var category in CategoryOrder)
            {
                LoadCategory(dir, category);
            }
            AddCharacterTokens();
        }

        private static string DefaultLocalizationDir() => RepoPaths.FindContaining("localization_eng");

        /// <summary>Every key this class stores or looks up goes through here, so category and id
        /// casing can never drift out of sync between LoadCategory and Encode.</summary>
        private static string ToKey(string category, string id) => category.ToUpperInvariant() + ":" + id.ToUpperInvariant();

        private void LoadCategory(string dir, string category)
        {
            var path = Path.Combine(dir, category + ".json");
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data == null) return;

            // Sorted so token ids are stable across re-runs regardless of JSON key order.
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var key in data.Keys)
            {
                var dot = key.IndexOf('.');
                if (dot < 0) continue; // e.g. card_keywords.json's "PERIOD" -- a formatting string, not an entry
                ids.Add(key[..dot]);
            }

            foreach (var id in ids)
            {
                var tokenKey = ToKey(category, id);
                if (_tokenMap.ContainsKey(tokenKey)) continue;
                _tokenMap[tokenKey] = _nextToken;
                _reverseTokenMap[_nextToken] = tokenKey;
                _nextToken++;
            }
        }

        /// <summary>Unrecognized or missing ids map to UNKNOWN rather than throwing -- an unseen id
        /// (game patch, a context that genuinely has none) is a runtime input, not a bug to crash
        /// on.</summary>
        public int Encode(string category, string? id)
        {
            if (string.IsNullOrEmpty(id)) return UnknownToken;
            return _tokenMap.TryGetValue(ToKey(category, id), out var token) ? token : UnknownToken;
        }

        public string Decode(int token)
        {
            return _reverseTokenMap.TryGetValue(token, out var key) ? key : "UNKNOWN";
        }

        /// <summary>Bulk-exports the full token -> "CATEGORY:ID" map as JSON, so a buffer-mode
        /// Python caller can build its own local int -> id lookup once at startup instead of
        /// paying a CLR round-trip through Decode() for every token on every step -- see
        /// python/buffer_env.py.</summary>
        public string ExportJson() =>
            System.Text.Json.JsonSerializer.Serialize(_reverseTokenMap);

        private void AddCharacterTokens()
        {
            int idx = 0;
            foreach (var character in new[] { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" })
            {
                var tokenKey = ToKey("characters", character);
                _tokenMap[tokenKey] = idx;
                _reverseTokenMap[idx] = tokenKey;
                idx++;
            }
        }
    }
}
