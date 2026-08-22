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

        /// <summary>Mirrors LocLookup's base-dir resolution in RunSimulator.cs -- both walk up from
        /// bin/Debug/net9.0/ to the sts2-cli checkout root, where localization_eng/ lives.</summary>
        private static string DefaultLocalizationDir()
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            return Path.Combine(baseDir, "localization_eng");
        }

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
                var tokenKey = category + ":" + id;
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
            return _tokenMap.TryGetValue(category.ToUpperInvariant() + ":" + id.ToUpperInvariant(), out var token) ? token : UnknownToken;
        }

        public string Decode(int token)
        {
            return _reverseTokenMap.TryGetValue(token, out var key) ? key : "UNKNOWN";
        }

        private void AddCharacterTokens()
        {
            int idx = 0;
            foreach (var character in new[] { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" })
            {
                var tokenKey = "CHARACTERS:" + character;
                _tokenMap[tokenKey] = idx;
                _reverseTokenMap[idx] = tokenKey;
                idx++;
            }
        }
    }
}
