using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CheckupAddIn.Models;
using Newtonsoft.Json;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Top-level settings object loaded from <c>Checkup_Settings.json</c> placed next to the .addin file.
    /// Because the .addin lives in each user's own AppData folder, every user has their own copy.
    /// Combines StylePurger configuration and preset factory defaults in one file.
    /// </summary>
    /// <remarks>
    /// <b>File location (2024):</b> same folder as <c>Autodesk.CheckupAddIn2025.addin</c>
    ///   (typically <c>%APPDATA%\Autodesk\ApplicationPlugins\</c> per user).
    ///   StandardAddInServer.FindAddinDirectory() resolves the path by iterating app.ApplicationAddIns.
    ///
    /// <b>User customizations (per-user):</b> stored in the Windows Registry at
    ///   <c>HKCU\Software\Checkup 2025\Presets</c> (JSON blob, REG_SZ).
    ///   Written by <see cref="PresetsManager"/> when the user saves a preset (right-click).
    ///   NOT related to <c>Checkup_Settings.json</c>; managed entirely by PresetsManager.
    ///
    /// <b>Fallback chain (presets):</b>
    ///   1. Registry <c>HKCU\Software\Checkup 2025\Presets</c> — user's saved customizations (takes priority).
    ///   2. <c>Checkup_Settings.json → Presets</c> — factory defaults; also what Reset button restores.
    ///   3. Hardcoded <c>_hardcodedFallback</c> in PresetsManager — emergency only, file missing.
    ///
    /// <b>To change factory defaults:</b> edit <c>Checkup_Settings.json</c> — no rebuild required.
    /// <b>To reset a user:</b> delete <c>HKCU\Software\Checkup 2025\Presets</c> from their registry.
    /// </remarks>
    public class UserSettings
    {
        public StylePurgeSection StylePurge              { get; set; } = new StylePurgeSection();
        public List<PresetData>  Presets                 { get; set; } = new List<PresetData>();

        [JsonIgnore]
        public string LoadedFrom { get; private set; } = "(not loaded)";

        public static UserSettings Load(string addinDirectory)
        {
            var path = Path.Combine(addinDirectory, "Checkup_Settings.json");
            if (!File.Exists(path))
                return new UserSettings { LoadedFrom = $"JSON not found at: {path}" };
            try
            {
                var json     = NormalizeWindowsPaths(File.ReadAllText(path));
                var settings = JsonConvert.DeserializeObject<UserSettings>(json) ?? new UserSettings();
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
        // is invalid and fails the WHOLE parse (silently reverting StylePurge + presets to defaults).
        // This doubles any lone backslash so naive entries load, while preserving genuine escapes:
        //   kept as-is → already-doubled "\\", escaped quote/slash "\" "\/", and unicode "\uXXXX".
        //   doubled    → everything else, including "\t" "\n" etc. (always a path char here, never a
        //                real tab/newline in this config), so "Z:\templates" loads correctly too.
        // Tip for admins: forward slashes ("Z:/Checkup/x.dll") work in every Inventor path and never
        // need escaping — the foolproof option.
        private static readonly Regex _loneBackslash =
            new Regex(@"\\(u[0-9A-Fa-f]{4}|[""\\/])|\\", RegexOptions.Compiled);

        internal static string NormalizeWindowsPaths(string json) =>
            _loneBackslash.Replace(json, m => m.Groups[1].Success ? m.Value : @"\\");

        public class StylePurgeSection
        {
            public string       TemplateFilePath        { get; set; } = @"V:\CAD\INV\Templates\Standard.idw";
            public List<string> BorderDefinitions       { get; set; } = new List<string>();
            public List<string> TitleBlockDefinitions   { get; set; } = new List<string>();
            public List<string> SketchedSymbolsToCopy   { get; set; } = new List<string>();
            public List<string> SketchedSymbolsToDelete { get; set; } = new List<string>();
        }
    }
}
