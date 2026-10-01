using System.IO;
using System.Text;
using System.Text.RegularExpressions;
#if NET48
using Newtonsoft.Json;
#else
using System.Text.Encodings.Web;
using System.Text.Json;
#endif
using CheckupAddIn.Models;
using Microsoft.Win32;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Loads and saves the user's preset list (1–12 presets) to the Windows Registry.
    /// Factory defaults come from Checkup_Settings.json (passed via constructor).
    /// Falls back to a single empty "Demo" preset if UserSettings failed to load.
    /// </summary>
    /// <remarks>
    /// Registry location: HKCU\Software\Checkup 2026\Presets  (REG_SZ — JSON blob)
    ///
    /// Preset content (ID, name, field key list) is stored in the registry.
    /// ResetToDefaults() deletes that registry value; the next Load() returns the external
    /// defaults from Checkup_Settings.json — not hardcoded C# values.
    ///
    /// Export / Import: use ExportPresetToLibrary / ReadLibrary to move preset data
    /// between machines or users via a plain JSON file.
    ///
    /// T47 (TDD §10.5): the list holds 1 to <see cref="MaxPresets"/> entries, each with a stable
    /// <see cref="PresetData.Id"/>. A pre-T47 registry value (exactly 3 entries, no IDs) is migrated
    /// on load: three untouched demo presets collapse to one; anything else keeps all three.
    /// The static helpers hold all list rules so they can be unit-tested without Inventor.
    /// </remarks>
    public class PresetsManager
    {
        private const string RegKey   = AppConstants.RegistryBaseKey;
        private const string RegValue = "Presets";

        /// <summary>Hard limit of presets per user (TDD §10.5 D5).</summary>
        public const int MaxPresets = 12;

        /// <summary>Name of the shipped demo preset.</summary>
        public const string DemoPresetName = "Demo";

        /// <summary>Field keys of the shipped demo preset — content-based demo detection for pre-IsDemo registry data.</summary>
        public static readonly string[] DemoDefaultFieldKeys =
        {
            "IPROP|Description", "IPROP|Part Number", "DOC:Material", "DOC:Appearance",
            "IPROP|Revision Number", "SPECIAL:LOGIC:demo-g03",
            "SPECIAL:LOGIC:demo-g01", "SPECIAL:LOGIC:demo-g08"
        };

        // Emergency fallback — only used when Checkup_Settings.json itself fails to load.
        private static readonly List<PresetData> _hardcodedFallback = new()
        {
            new PresetData { Id = "default-1", Name = DemoPresetName, FieldKeys = new() },
        };

        private readonly List<PresetData> _defaults;

        public PresetsManager(List<PresetData> externalDefaults = null)
        {
            _defaults = (externalDefaults != null && externalDefaults.Count > 0)
                ? NormalizeDefaults(externalDefaults)
                : _hardcodedFallback;
        }

        /// <summary>
        /// Loads saved presets from the registry, or returns external defaults if the value is missing or invalid.
        /// Migrates pre-T47 data and persists the result immediately so generated IDs stay stable.
        /// </summary>
        public List<PresetData> Load()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var json = key?.GetValue(RegValue) as string;
                    if (!string.IsNullOrEmpty(json))
                    {
#if NET48
                        var loaded = JsonConvert.DeserializeObject<List<PresetData>>(json);
#else
                        var loaded = JsonSerializer.Deserialize<List<PresetData>>(json);
#endif
                        var normalized = NormalizeLoaded(loaded, out bool changed);
                        if (normalized != null)
                        {
                            if (changed) Save(normalized);
                            return normalized;
                        }
                    }
                }
            }
            catch { }

            return GetDefaults();
        }

        /// <summary>Persists the preset list to the registry.</summary>
        public void Save(List<PresetData> presets)
        {
            try
            {
#if NET48
                string json = JsonConvert.SerializeObject(presets);
#else
                string json = JsonSerializer.Serialize(presets);
#endif
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
        /// Upserts <paramref name="preset"/> into the library file at <paramref name="path"/> (see <see cref="UpsertIntoLibrary"/>).
        /// New file: create with one entry. Throws on I/O error — caller should catch and show a message.
        /// </summary>
        public void ExportPresetToLibrary(PresetData preset, string path)
        {
            var library = File.Exists(path) ? ReadLibrary(path) : new List<PresetData>();
            UpsertIntoLibrary(library, preset);
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
                UpsertIntoLibrary(library, preset);
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
#if NET48
            var library = JsonConvert.DeserializeObject<List<PresetData>>(json);
#else
            var library = JsonSerializer.Deserialize<List<PresetData>>(json);
#endif
            if (library == null) throw new InvalidOperationException("File is not a valid preset library.");
            return library;
        }

        private void WriteLibrary(List<PresetData> library, string path)
        {
#if NET48
            File.WriteAllText(path, JsonConvert.SerializeObject(library, Formatting.Indented), Encoding.UTF8);
#else
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(library, options), Encoding.UTF8);
#endif
        }

        /// <summary>Returns a deep copy of the factory defaults (safe to mutate). Copies EVERY field.</summary>
        public List<PresetData> GetDefaults() => _defaults.Select(Clone).ToList();

        // ══════════════════════════════════════════════
        //  LIST RULES (static — unit-tested, TDD §10.5)
        // ══════════════════════════════════════════════

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static PresetData Clone(PresetData p) => new PresetData
        {
            Id        = p.Id,
            Name      = p.Name,
            FieldKeys = new List<string>(p.FieldKeys ?? new List<string>()),
            IsDemo    = p.IsDemo
        };

        /// <summary>
        /// Admin/factory defaults: keep the first 12, missing or duplicate IDs get the deterministic
        /// "default-&lt;position&gt;" so the active preset survives a window reopen without a registry save.
        /// </summary>
        public static List<PresetData> NormalizeDefaults(List<PresetData> source)
        {
            var result = source.Where(p => p != null).Take(MaxPresets).Select(Clone).ToList();
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < result.Count; i++)
            {
                var p = result[i];
                p.Name ??= "";
                if (string.IsNullOrEmpty(p.Id) || used.Contains(p.Id))
                    p.Id = "default-" + (i + 1);
                used.Add(p.Id);
            }
            return result;
        }

        /// <summary>
        /// Validates a preset list read from the registry. Returns null when unusable (→ defaults).
        /// Keeps the first 12, fills missing/duplicate IDs with new GUIDs, and migrates a pre-T47
        /// list (exactly 3 entries, none with an ID): three untouched demo presets collapse to one.
        /// <paramref name="changed"/> is true when the result differs from the input and must be saved.
        /// </summary>
        public static List<PresetData> NormalizeLoaded(List<PresetData> loaded, out bool changed)
        {
            changed = false;
            if (loaded == null) return null;
            var list = loaded.Where(p => p != null).Select(Clone).ToList();
            if (list.Count == 0) return null;
            if (list.Count != loaded.Count) changed = true;

            bool isLegacy = list.Count == 3 && list.All(p => string.IsNullOrEmpty(p.Id));
            if (isLegacy && IsUntouchedDemo(list))
            {
                list = new List<PresetData> { list[0] };
                changed = true;
            }

            if (list.Count > MaxPresets)
            {
                list = list.Take(MaxPresets).ToList();
                changed = true;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in list)
            {
                p.Name ??= "";
                if (string.IsNullOrEmpty(p.Id) || used.Contains(p.Id))
                {
                    p.Id = NewId();
                    changed = true;
                }
                used.Add(p.Id);
            }
            return list;
        }

        /// <summary>True when every preset is still a demo preset (flag, or name + field keys for pre-IsDemo data).</summary>
        public static bool IsUntouchedDemo(IReadOnlyCollection<PresetData> presets)
        {
            if (presets == null || presets.Count == 0) return false;
            if (presets.All(p => p.IsDemo)) return true;
            return presets.All(p =>
                string.Equals(p.Name, DemoPresetName, StringComparison.Ordinal) &&
                (p.FieldKeys ?? new List<string>()).SequenceEqual(DemoDefaultFieldKeys));
        }

        private static readonly Regex _copySuffix = new Regex(@"^(.*) \((\d+)\)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Name for a copy made by the "+" button: "&lt;base&gt; (n)" with the smallest n ≥ 2 not yet used.
        /// A trailing " (n)" on the source name is stripped first, so a copy of "Demo (2)" becomes "Demo (3)".
        /// </summary>
        public static string MakeCopyName(string sourceName, IEnumerable<string> existingNames)
        {
            string baseName = sourceName ?? "";
            var m = _copySuffix.Match(baseName);
            if (m.Success) baseName = m.Groups[1].Value;

            var names = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            for (int n = 2; ; n++)
            {
                string candidate = $"{baseName} ({n})";
                if (!names.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Builds the preset added by the "+" button: a copy of <paramref name="source"/> carrying the
        /// LIVE row keys (incl. unsaved changes), a new ID and a unique copy name; inherits IsDemo.
        /// </summary>
        public static PresetData CreateCopy(PresetData source, IEnumerable<string> liveFieldKeys, IEnumerable<string> existingNames)
            => new PresetData
            {
                Id        = NewId(),
                Name      = MakeCopyName(source?.Name, existingNames),
                FieldKeys = new List<string>(liveFieldKeys ?? Enumerable.Empty<string>()),
                IsDemo    = source?.IsDemo ?? false
            };

        /// <summary>Index that becomes active after deleting the active preset at <paramref name="deletedIndex"/>: its left neighbour, else the new first.</summary>
        public static int IndexAfterDelete(int deletedIndex) => Math.Max(0, deletedIndex - 1);

        /// <summary>
        /// Moves the preset at <paramref name="fromIndex"/> so that it lands before the element that was at
        /// <paramref name="insertIndex"/> (0..Count; Count = end). Returns false when nothing changes.
        /// </summary>
        public static bool Move(List<PresetData> list, int fromIndex, int insertIndex)
        {
            if (list == null || fromIndex < 0 || fromIndex >= list.Count) return false;
            insertIndex = Math.Max(0, Math.Min(insertIndex, list.Count));
            int target = insertIndex > fromIndex ? insertIndex - 1 : insertIndex;
            if (target == fromIndex) return false;
            var item = list[fromIndex];
            list.RemoveAt(fromIndex);
            list.Insert(target, item);
            return true;
        }

        /// <summary>
        /// ID the target preset carries after an import (D12): the imported ID when present and not used by
        /// ANOTHER preset; otherwise the target keeps its own ID. Never creates a duplicate.
        /// </summary>
        public static string ResolveImportId(string targetId, string importedId, IEnumerable<string> otherIds)
        {
            if (string.IsNullOrEmpty(importedId)) return targetId;
            return (otherIds ?? Enumerable.Empty<string>()).Contains(importedId, StringComparer.Ordinal)
                ? targetId
                : importedId;
        }

        // ── Multi-import (D15) ──

        /// <summary>
        /// Index of the existing preset an imported entry conflicts with: same ID first, else same name
        /// (case-insensitive, first match). -1 when there is no conflict.
        /// </summary>
        public static int FindImportConflict(IReadOnlyList<PresetData> existing, PresetData incoming)
        {
            if (existing == null || incoming == null) return -1;
            if (!string.IsNullOrEmpty(incoming.Id))
                for (int i = 0; i < existing.Count; i++)
                    if (string.Equals(existing[i].Id, incoming.Id, StringComparison.Ordinal)) return i;
            for (int i = 0; i < existing.Count; i++)
                if (string.Equals(existing[i].Name, incoming.Name ?? "", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Ticked entries that match no existing preset — they need a new slot whatever the user answers.</summary>
        public static int CountGuaranteedNew(IReadOnlyList<PresetData> existing, IEnumerable<PresetData> incoming)
            => (incoming ?? Enumerable.Empty<PresetData>()).Count(p => FindImportConflict(existing, p) < 0);

        /// <summary>
        /// Applies a multi-import (D15). Each item either overwrites the preset with ID
        /// <c>OverwriteTargetId</c> (name + field keys; ID per <see cref="ResolveImportId"/>) or, when that is
        /// null, is appended as a new preset (file ID when free, else a new one). All-or-nothing: returns false
        /// without changing <paramref name="presets"/> when the additions would exceed <see cref="MaxPresets"/>.
        /// <paramref name="renamedIds"/> maps overwritten presets whose ID changed (old → new).
        /// </summary>
        public static bool ApplyMultiImport(List<PresetData> presets,
            IReadOnlyList<(PresetData Data, string OverwriteTargetId)> items,
            out Dictionary<string, string> renamedIds)
        {
            renamedIds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (presets == null || items == null) return false;

            int additions = items.Count(i => i.OverwriteTargetId == null || !presets.Any(p => p.Id == i.OverwriteTargetId));
            if (presets.Count + additions > MaxPresets) return false;

            foreach (var (data, targetId) in items)
            {
                if (data == null) continue;
                var keys = new List<string>(data.FieldKeys ?? new List<string>());
                // A target already overwritten earlier in this batch may carry its new ID by now.
                string current = targetId != null && renamedIds.TryGetValue(targetId, out var moved) ? moved : targetId;
                int idx  = current == null ? -1 : presets.FindIndex(p => p.Id == current);
                if (idx >= 0)
                {
                    string newId = ResolveImportId(current, data.Id, presets.Where((p, i) => i != idx).Select(p => p.Id));
                    presets[idx] = new PresetData { Id = newId, Name = data.Name ?? "", FieldKeys = keys };
                    if (newId != current) renamedIds[targetId] = newId;
                }
                else
                {
                    bool idFree = !string.IsNullOrEmpty(data.Id) && !presets.Any(p => p.Id == data.Id);
                    presets.Add(new PresetData { Id = idFree ? data.Id : NewId(), Name = data.Name ?? "", FieldKeys = keys });
                }
            }
            return true;
        }

        /// <summary>
        /// Export upsert (D13): match by ID; library entries without an ID (older files) match by name.
        /// Match → overwrite; no match → append. The written entry always carries the ID.
        /// </summary>
        public static void UpsertIntoLibrary(List<PresetData> library, PresetData preset)
        {
            var copy = Clone(preset);
            int idx = string.IsNullOrEmpty(copy.Id) ? -1
                : library.FindIndex(p => string.Equals(p?.Id, copy.Id, StringComparison.Ordinal));
            if (idx < 0)
                idx = library.FindIndex(p => p != null && string.IsNullOrEmpty(p.Id) &&
                                             string.Equals(p.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) library[idx] = copy; else library.Add(copy);
        }
    }
}
