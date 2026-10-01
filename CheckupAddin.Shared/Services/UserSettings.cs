using System.IO;
#if NET48
using Newtonsoft.Json;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif
using System.Text.RegularExpressions;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Top-level settings object loaded from <c>Checkup_Settings.json</c> placed next to the add-in DLL.
    /// Holds the preset factory defaults.
    /// </summary>
    /// <remarks>
    /// <b>File location:</b> same folder as <c>CheckupAddIn.dll</c> (the deployed add-in directory).
    ///   This file ships with the add-in and is loaded once at Inventor startup. Admins can edit it
    ///   to pre-configure the default preset field lists for all users.
    ///
    /// <b>User customizations (per-user):</b> stored in the Windows Registry at
    ///   <c>HKCU\Software\Checkup 2026\Presets</c> (JSON blob, REG_SZ).
    ///   Written by <see cref="PresetsManager"/> when the user saves a preset (right-click).
    ///   NOT related to <c>Checkup_Settings.json</c>; managed entirely by PresetsManager.
    ///
    /// <b>Fallback chain (presets):</b>
    /// <list type="number">
    ///   <item>Registry <c>HKCU\Software\Checkup 2026\Presets</c> — user's saved customizations (takes priority).</item>
    ///   <item><c>Checkup_Settings.json → Presets</c> — factory defaults from the deployed file.</item>
    ///   <item>Hardcoded <c>_hardcodedFallback</c> in PresetsManager — emergency only, file missing.</item>
    /// </list>
    /// <b>To change factory defaults:</b> edit <c>Checkup_Settings.json</c> — no rebuild required.
    /// <b>To reset a user:</b> delete <c>HKCU\Software\Checkup 2026\Presets</c> from their registry.
    /// </remarks>
    public class UserSettings
    {
        public List<PresetData>  Presets                 { get; set; } = new();
        // SharedRootPath removed — distribution path is always addinDir (DLL location).
        // StylePurge section removed (T46 — Style Purger replaced by "Run iLogic Rule"; its settings
        // now live in the shipped purge rule). Old Checkup_Settings.json files that still contain
        // "SharedRootPath" or "StylePurge" are read without error: both JSON libraries silently
        // ignore unknown properties.

#if NET48
        [Newtonsoft.Json.JsonIgnore]
#else
        [JsonIgnore]
#endif
        public string LoadedFrom { get; private set; } = "(not loaded)";

        public static UserSettings Load(string addinDirectory)
        {
            var path = Path.Combine(addinDirectory, "Checkup_Settings.json");
            if (!File.Exists(path))
                return new UserSettings { LoadedFrom = $"JSON not found at: {path}" };
            try
            {
                var json     = NormalizeWindowsPaths(File.ReadAllText(path));
#if NET48
                var settings = JsonConvert.DeserializeObject<UserSettings>(json) ?? new UserSettings();
#else
                var settings = JsonSerializer.Deserialize<UserSettings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true })
                    ?? new UserSettings();
#endif
                settings.LoadedFrom = path;
                return settings;
            }
            catch (Exception ex)
            {
                return new UserSettings { LoadedFrom = $"JSON parse error ({path}): {ex.Message}" };
            }
        }

        // Tolerate Windows paths typed with single backslashes, e.g. "Z:\Checkup\CheckupAddIn.dll".
        // Strict JSON requires "\\" for a literal backslash; a lone "\" before a non-escape character
        // is invalid and fails the WHOLE parse (silently reverting the presets to defaults). Kept after
        // T46: old deployed files still carry a single-backslash StylePurge.TemplateFilePath.
        // This doubles any lone backslash so naive entries load, while preserving genuine escapes:
        //   kept as-is → already-doubled "\\", escaped quote/slash "\" "\/", and unicode "\uXXXX".
        //   doubled    → everything else, including "\t" "\n" etc. (always a path char here, never a
        //                real tab/newline in this config), so "Z:\templates" loads correctly too.
        // Tip for admins: forward slashes ("Z:/Checkup/x.dll") work in every Inventor path and never
        // need escaping — the foolproof option.
        private static readonly Regex _loneBackslash =
            new(@"\\(u[0-9A-Fa-f]{4}|[""\\/])|\\", RegexOptions.Compiled);

        internal static string NormalizeWindowsPaths(string json) =>
            _loneBackslash.Replace(json, m => m.Groups[1].Success ? m.Value : @"\\");
    }
}
