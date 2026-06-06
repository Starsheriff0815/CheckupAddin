using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Top-level settings object loaded from <c>Checkup_Settings.json</c> placed next to the add-in DLL.
    /// Combines StylePurger configuration and preset factory defaults in one file.
    /// </summary>
    /// <remarks>
    /// <b>File location:</b> same folder as <c>CheckupAddIn.dll</c> (the deployed add-in directory).
    ///   This file ships with the add-in and is loaded once at Inventor startup. Admins can edit it
    ///   to pre-configure StylePurge paths and default preset field lists for all users.
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
        public StylePurgeSection StylePurge              { get; set; } = new();
        public List<PresetData>  Presets                 { get; set; } = new();
        // SharedRootPath removed — distribution path is always addinDir (DLL location).
        // Old Checkup_Settings.json files that still contain "SharedRootPath" are read
        // without error; System.Text.Json silently ignores unknown properties.

        [JsonIgnore]
        public string LoadedFrom { get; private set; } = "(not loaded)";

        public static UserSettings Load(string addinDirectory)
        {
            var path = Path.Combine(addinDirectory, "Checkup_Settings.json");
            if (!File.Exists(path))
                return new UserSettings { LoadedFrom = $"JSON not found at: {path}" };
            try
            {
                var json     = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<UserSettings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true })
                    ?? new UserSettings();
                settings.LoadedFrom = path;
                return settings;
            }
            catch (Exception ex)
            {
                return new UserSettings { LoadedFrom = $"JSON parse error ({path}): {ex.Message}" };
            }
        }

        public class StylePurgeSection
        {
            public string       TemplateFilePath        { get; set; } = @"V:\CAD\INV\Templates\Standard.idw";
            public List<string> BorderDefinitions       { get; set; } = new();
            public List<string> TitleBlockDefinitions   { get; set; } = new();
            public List<string> SketchedSymbolsToCopy   { get; set; } = new();
            public List<string> SketchedSymbolsToDelete { get; set; } = new();
        }
    }
}
