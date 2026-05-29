using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Loads translated strings from Addin_Language_File_{CODE}.json (placed next to the DLL) and makes them
    /// available via DynamicResource bindings in XAML and via Get() in C# code.
    ///
    /// To add a new language: copy Addin_Language_File_EN.json to Addin_Language_File_FR.json, translate the values,
    /// place the file alongside the DLL — no recompile required.
    /// </summary>
    internal static class LanguageLoader
    {
        private static ResourceDictionary _loaded;

        /// <summary>Two-letter ISO language code active for this session (e.g. "de", "en").</summary>
        public static string TwoLetterCode { get; private set; } = "en";

        /// <summary>
        /// Detects the Inventor / OS language and loads the matching Addin_Language_File_{CODE}.json file.
        /// Call once from StandardAddInServer.Activate() before creating any ViewModel.
        /// Fallback chain: detected language → Addin_Language_File_EN.json → Addin_Language_File_DE.json → empty dict.
        /// </summary>
        public static void Detect(Inventor.Application app = null)
        {
            TwoLetterCode = DetectCode(app);

            string dir = "";
            try { dir = Path.GetDirectoryName(typeof(LanguageLoader).Assembly.Location) ?? ""; }
            catch { }

            string langDir = Path.Combine(dir, "Languages");
            bool ok = TryLoad(Path.Combine(langDir, $"Addin_Language_File_{TwoLetterCode.ToUpper()}.json"))
                   || TryLoad(Path.Combine(langDir, "Addin_Language_File_EN.json"))
                   || TryLoad(Path.Combine(langDir, "Addin_Language_File_DE.json"));

            if (!ok) _loaded = new ResourceDictionary();
        }

        /// <summary>
        /// Merges the loaded string dictionary into a window's MergedDictionaries so that
        /// {DynamicResource key} bindings resolve to translated strings.
        /// Call from each window's code-behind after InitializeComponent().
        /// </summary>
        public static void ApplyTo(Window window)
        {
            if (_loaded == null || window == null) return;
            var dicts = window.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                if (dicts[i].Contains("_LanguageMarker"))
                    dicts.RemoveAt(i);
            }
            dicts.Add(_loaded);
        }

        /// <summary>
        /// Returns the translated string for key, or the key itself when no translation exists.
        /// Use this in C# code wherever DynamicResource bindings are not available.
        /// </summary>
        public static string Get(string key)
        {
            if (_loaded != null && _loaded.Contains(key))
                return _loaded[key] as string ?? key;
            return key;
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        private static bool TryLoad(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var strings = ParseJson(text);
                var dict = new ResourceDictionary();
                foreach (var kv in strings)
                    dict[kv.Key] = kv.Value;
                dict["_LanguageMarker"] = true;
                _loaded = dict;
                return true;
            }
            catch { return false; }
        }

        // Matches "key": "value" pairs; skips keys starting with _ (used as comments/markers).
        private static readonly Regex _jsonEntry = new Regex(
            @"""(?<k>[^""\\]+)""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);

        private static Dictionary<string, string> ParseJson(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in _jsonEntry.Matches(json))
            {
                string k = m.Groups["k"].Value;
                if (k.StartsWith("_")) continue;
                string v = m.Groups["v"].Value
                    .Replace("\\n", "\n")
                    .Replace("\\t", "\t")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
                result[k] = v;
            }
            return result;
        }

        private static string DetectCode(Inventor.Application app)
        {
            try
            {
                if (app != null)
                {
                    int lcid = (int)((dynamic)app).Locale;
                    return new CultureInfo(lcid).TwoLetterISOLanguageName;
                }
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }
    }
}
