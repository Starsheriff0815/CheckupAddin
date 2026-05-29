using System.Collections.Generic;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// Serialized form of one preset (name + ordered list of field keys).
    /// Persisted as JSON by PresetsManager to %APPDATA%\Checkup\presets.json.
    /// Field keys use the same prefix conventions as FieldItem.Key.
    /// </summary>
    public class PresetData
    {
        public string Name { get; set; } = "";
        public List<string> FieldKeys { get; set; } = new List<string>();
    }
}
