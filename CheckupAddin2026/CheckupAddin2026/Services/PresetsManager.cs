using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CheckupAddIn.Models;
using Microsoft.Win32;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Loads and saves the three custom field presets to the Windows Registry.
    /// Factory defaults come from Checkup_Settings.json (passed via constructor).
    /// Falls back to empty placeholder presets if UserSettings failed to load.
    /// </summary>
    /// <remarks>
    /// Registry location: HKCU\Software\Checkup 2026\Presets  (REG_SZ — JSON blob)
    ///
    /// Preset content (field key lists) is stored in the registry.
    /// ResetToDefaults() deletes that registry value; the next Load() returns the external
    /// defaults from Checkup_Settings.json — not hardcoded C# values.
    ///
    /// Export / Import: use ExportToFile / ImportFromFile to move preset data
    /// between machines or users via a plain JSON file.
    ///
    /// Always exactly 3 presets — the JSON must deserialise to exactly 3 entries or it is discarded.
    /// </remarks>
    public class PresetsManager
    {
        private const string RegKey   = @"Software\Checkup 2026";
        private const string RegValue = "Presets";

        // Emergency fallback — only used when Checkup_Settings.json itself fails to load.
        private static readonly List<PresetData> _hardcodedFallback = new()
        {
            new PresetData { Name = "Bauteil",       FieldKeys = new() },
            new PresetData { Name = "Baugruppe",     FieldKeys = new() },
            new PresetData { Name = "Gehrungslücke", FieldKeys = new() },
        };

        private readonly List<PresetData> _defaults;

        public PresetsManager(List<PresetData> externalDefaults = null)
        {
            _defaults = (externalDefaults != null && externalDefaults.Count == 3)
                ? externalDefaults
                : _hardcodedFallback;
        }

        /// <summary>Loads saved presets from the registry, or returns external defaults if the value is missing or invalid.</summary>
        public List<PresetData> Load()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var json = key?.GetValue(RegValue) as string;
                    if (!string.IsNullOrEmpty(json))
                    {
                        var loaded = JsonSerializer.Deserialize<List<PresetData>>(json);
                        if (loaded != null && loaded.Count == 3) return loaded;
                    }
                }
            }
            catch { }

            return GetDefaults();
        }

        /// <summary>Persists all three presets to the registry.</summary>
        public void Save(List<PresetData> presets)
        {
            try
            {
                string json = JsonSerializer.Serialize(presets);
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue(RegValue, json, RegistryValueKind.String);
            }
            catch { }
        }

        /// <summary>Deletes the registry value — next Load() will return external defaults from Checkup_Settings.json.</summary>
        public void ResetToDefaults()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true))
                    key?.DeleteValue(RegValue, throwOnMissingValue: false);
            }
            catch { }
        }

        /// <summary>
        /// Writes the currently saved presets to a JSON file at <paramref name="path"/>.
        /// Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public void ExportToFile(string path)
        {
            var presets = Load();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            string json = JsonSerializer.Serialize(presets, options);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        /// <summary>
        /// Reads presets from a JSON file, validates that it contains exactly 3 entries,
        /// saves them to the registry, and returns the imported list.
        /// Throws <see cref="InvalidOperationException"/> if the file is invalid.
        /// Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public List<PresetData> ImportFromFile(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var imported = JsonSerializer.Deserialize<List<PresetData>>(json);
            if (imported == null || imported.Count != 3)
                throw new InvalidOperationException("File must contain exactly 3 presets.");
            Save(imported);
            return imported;
        }

        /// <summary>Returns a deep copy of the factory defaults (safe to mutate).</summary>
        public List<PresetData> GetDefaults() =>
            _defaults.Select(p => new PresetData
            {
                Name      = p.Name,
                FieldKeys = new List<string>(p.FieldKeys)
            }).ToList();
    }
}
