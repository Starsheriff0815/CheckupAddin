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
            new PresetData { Name = "Bauteil",   FieldKeys = new() },
            new PresetData { Name = "Baugruppe", FieldKeys = new() },
            new PresetData { Name = "Allgemein", FieldKeys = new() },
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
        /// Upserts <paramref name="preset"/> into the library file at <paramref name="path"/> by Name.
        /// Existing file: match by name → overwrite; no match → append. New file: create with one entry.
        /// Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public void ExportPresetToLibrary(PresetData preset, string path)
        {
            var library = File.Exists(path) ? ReadLibrary(path) : new List<PresetData>();
            int idx = library.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) library[idx] = preset; else library.Add(preset);
            WriteLibrary(library, path);
        }

        /// <summary>
        /// Upserts all entries in <paramref name="presets"/> into the library file at <paramref name="path"/>.
        /// Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public void ExportAllPresetsToLibrary(List<PresetData> presets, string path)
        {
            var library = File.Exists(path) ? ReadLibrary(path) : new List<PresetData>();
            foreach (var preset in presets)
            {
                int idx = library.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) library[idx] = preset; else library.Add(preset);
            }
            WriteLibrary(library, path);
        }

        /// <summary>
        /// Reads all preset entries from a library file.
        /// Throws <see cref="InvalidOperationException"/> if the file cannot be parsed.
        /// Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public List<PresetData> ReadLibrary(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var library = JsonSerializer.Deserialize<List<PresetData>>(json);
            if (library == null) throw new InvalidOperationException("File is not a valid preset library.");
            return library;
        }

        private void WriteLibrary(List<PresetData> library, string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(library, options), Encoding.UTF8);
        }

        /// <summary>Returns a deep copy of the factory defaults (safe to mutate).</summary>
        public List<PresetData> GetDefaults() =>
            _defaults.Select(p => new PresetData
            {
                Name      = p.Name,
                FieldKeys = new List<string>(p.FieldKeys),
                IsDemo    = p.IsDemo
            }).ToList();
    }
}
