namespace CheckupAddIn.Models
{
    /// <summary>
    /// Serialized form of one preset (ID + name + ordered list of field keys).
    /// User-saved presets are persisted to HKCU\Software\Checkup 2026\Presets.
    /// Factory defaults come from Checkup_Settings.json via PresetsManager.
    /// Field keys use the same prefix conventions as FieldItem.Key.
    /// </summary>
    public class PresetData
    {
        /// <summary>
        /// Stable unique identifier (T47) — independent of the preset's position and label.
        /// Persisted together with <see cref="Name"/> in the registry, the settings file and export files.
        /// Empty in files written before T47; <see cref="Services.PresetsManager"/> fills it on load.
        /// </summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> FieldKeys { get; set; } = new();
        /// <summary>
        /// True when this preset still contains the shipped demo configuration.
        /// Cleared automatically by SavePreset() when the user changes both the
        /// name and the field keys away from the demo defaults.
        /// Controls the demo-mode warning dialog in CheckupViewModel.
        /// </summary>
        public bool IsDemo { get; set; } = false;
    }
}
