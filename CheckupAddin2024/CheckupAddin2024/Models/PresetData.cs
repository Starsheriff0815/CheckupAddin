using System.Collections.Generic;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// Serialized form of one preset (name + ordered list of field keys).
    /// User-saved presets are persisted to HKCU\Software\Checkup 2024\Presets.
    /// Factory defaults come from Checkup_Settings.json via PresetsManager.
    /// Field keys use the same prefix conventions as FieldItem.Key.
    /// </summary>
    public class PresetData
    {
        public string Name { get; set; } = "";
        public List<string> FieldKeys { get; set; } = new List<string>();
        /// <summary>
        /// True when this preset still contains the shipped demo configuration.
        /// Cleared automatically by SavePreset() when the user changes both the
        /// name and the field keys away from the demo defaults.
        /// Controls the demo-mode warning dialog in CheckupViewModel.
        /// </summary>
        public bool IsDemo { get; set; } = false;
    }
}
