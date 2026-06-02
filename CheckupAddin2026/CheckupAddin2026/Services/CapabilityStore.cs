using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Loads capability sets from two sources and saves user copies to AppData.
    ///
    /// Sources (load order — AppData wins on same Id):
    ///   1. addinDir\Capabilities\  — distribution files (UNC in multi-user deployments).
    ///      Files on a UNC path are auto-locked regardless of their IsLocked JSON value.
    ///   2. AppData\Checkup 2026\Capabilities\  — personal unlocked copies.
    ///
    /// Save() always writes to the AppData directory.
    /// </summary>
    public class CapabilityStore
    {
        public const string CAPABILITY_EXT      = ".capability.json";
        public const string CAPABILITIES_SUBDIR = "Capabilities";
        public const string LEGACY_FILENAME      = "Checkup_Capabilities.json";

        private static readonly JsonSerializerOptions _opts =
            new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private readonly List<CapabilitySet>        _sets                  = new();
        private readonly Dictionary<string, string> _filePaths             = new(); // Id → full path
        private readonly Dictionary<string, string> _distributionFilePaths = new(); // Id → distribution path

        private readonly string _localDir;       // AppData\Checkup 2026\Capabilities\
        private readonly string _distributionDir; // addinDir\Capabilities\ (may be UNC)

        public IReadOnlyList<CapabilitySet> CapabilitySets => _sets;

        /// <summary>True when the set's file lives on a UNC path (\\server\...).</summary>
        public bool IsOnUncPath(string id) =>
            _filePaths.TryGetValue(id, out var p) && IsUncPath(p);

        private CapabilityStore(string localDir, string distributionDir)
        {
            _localDir        = localDir;
            _distributionDir = distributionDir ?? "";
        }

        /// <summary>
        /// Loads all capability sets from the distribution directory (addinDir\Capabilities\)
        /// first, then from the user's AppData directory. AppData wins for the same Id.
        /// Migrates legacy single-file format if still present next to the DLL.
        /// </summary>
        public static CapabilityStore Load(string addinDir, string localDir)
        {
            string localCapDir = Path.Combine(localDir, CAPABILITIES_SUBDIR);
            string distCapDir  = string.IsNullOrEmpty(addinDir)
                ? "" : Path.Combine(addinDir, CAPABILITIES_SUBDIR);

            var store = new CapabilityStore(localCapDir, distCapDir);
            try { Directory.CreateDirectory(localCapDir); } catch { }

            // 1. Distribution files (addinDir\Capabilities\)
            if (!string.IsNullOrEmpty(distCapDir))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(distCapDir, $"*{CAPABILITY_EXT}"))
                        store.LoadFile(f, fromDistribution: true);
                }
                catch { }
            }

            // 2. User AppData files — override same Id from distribution; sync gap detected here
            try
            {
                foreach (var f in Directory.GetFiles(localCapDir, $"*{CAPABILITY_EXT}"))
                    store.LoadFile(f, fromDistribution: false);
            }
            catch { }

            // 3. Legacy single-file migration
            string legacyPath = string.IsNullOrEmpty(addinDir) ? ""
                : Path.Combine(addinDir, LEGACY_FILENAME);
            if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
                store.MigrateFromLegacy(legacyPath);

            return store;
        }

        private void LoadFile(string filePath, bool fromDistribution = false)
        {
            try
            {
                var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                var set  = JsonSerializer.Deserialize<CapabilitySet>(json, _opts);
                if (set == null) return;
                if (string.IsNullOrEmpty(set.Id))
                    set.Id = Guid.NewGuid().ToString("N")[..8];

                MigrateCapabilitySet(set);

                // Derive runtime flags from physical path — never trust the JSON flag.
                set.IsOnUncPath = IsUncPath(filePath);
                if (set.IsOnUncPath)
                    set.IsLocked = true;

                if (fromDistribution)
                {
                    _distributionFilePaths[set.Id] = filePath;
                }
                else
                {
                    // Sync gap: distribution was loaded first; check if it has a newer timestamp.
                    int distIdx = _sets.FindIndex(s => s.Id == set.Id);
                    if (distIdx >= 0 && _distributionFilePaths.ContainsKey(set.Id))
                    {
                        var distVersion = _sets[distIdx];
                        if (distVersion.LastUpdated != default && distVersion.LastUpdated > set.LastUpdated)
                            set.HasUpdateAvailable = true;
                    }
                }

                int existing = _sets.FindIndex(s => s.Id == set.Id);
                if (existing >= 0)
                {
                    _sets[existing]    = set;
                    _filePaths[set.Id] = filePath;
                }
                else
                {
                    _sets.Add(set);
                    _filePaths[set.Id] = filePath;
                }
            }
            catch (Exception ex) { DiagLogger.Log("caps", $"LoadFile failed '{filePath}': {DiagLogger.S(ex.Message)}"); }
        }

        /// <summary>
        /// Deletes the AppData copy and restores the distribution version as the live entry.
        /// Returns the reloaded distribution object, or null if the distribution file is not found.
        /// </summary>
        public CapabilitySet RevertToDistribution(CapabilitySet set)
        {
            if (set == null) return null;

            // Delete AppData file
            if (_filePaths.TryGetValue(set.Id, out string localPath) &&
                !IsUncPath(localPath) &&
                localPath.StartsWith(_localDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(localPath); } catch { }
            }

            // Remove from memory and reload the distribution version
            _sets.RemoveAll(s => s.Id == set.Id);
            _filePaths.Remove(set.Id);

            if (_distributionFilePaths.TryGetValue(set.Id, out string distPath))
            {
                LoadFile(distPath, fromDistribution: true);
                return _sets.FirstOrDefault(s => s.Id == set.Id);
            }
            return null;
        }

        private void MigrateFromLegacy(string legacyPath)
        {
            try
            {
                var json = File.ReadAllText(legacyPath, System.Text.Encoding.UTF8);
                var data = JsonSerializer.Deserialize<LegacyCapabilityStoreData>(json, _opts);
                if (data?.CapabilitySets == null) return;

                foreach (var set in data.CapabilitySets)
                {
                    if (_sets.Any(s => s.Id == set.Id)) continue;
                    if (string.IsNullOrEmpty(set.Id))
                        set.Id = Guid.NewGuid().ToString("N")[..8];
                    MigrateCapabilitySet(set);
                    _sets.Add(set);
                    Save(set);
                }

                try { File.Move(legacyPath, legacyPath + ".migrated"); } catch { }
            }
            catch { }
        }

        /// <summary>
        /// Converts a v1 CapabilitySet (flat CatalogId/TargetFieldKey/Cards) into
        /// a single CardGroup so all consumers work with the unified group model.
        /// No-op when Groups is already populated.
        /// </summary>
        private static void MigrateCapabilitySet(CapabilitySet cs)
        {
            if (cs.Groups.Count > 0) return;

            var group = new CardGroup
            {
                Id             = cs.Id,
                Name           = "",
                TargetFieldKey = cs.TargetFieldKey ?? "",
                Cards          = new List<CapabilityCard>(),
            };

            if (cs.Cards != null)
            {
                foreach (var card in cs.Cards)
                {
                    if (string.IsNullOrEmpty(card.CatalogId) && !string.IsNullOrEmpty(cs.CatalogId))
                        card.CatalogId = cs.CatalogId;
                    group.Cards.Add(card);
                }
            }

            cs.Groups.Add(group);
            cs.CatalogId      = null;
            cs.TargetFieldKey = null;
            cs.Cards          = null;
        }

        /// <summary>
        /// Saves the capability set to the user's AppData directory and bumps LastUpdated.
        /// If the set was previously on a UNC path, it becomes a local AppData copy.
        /// </summary>
        public void Save(CapabilitySet set)
        {
            if (set == null) return;
            if (!_sets.Any(s => s.Id == set.Id))
                _sets.Add(set);

            try { Directory.CreateDirectory(_localDir); } catch { }

            set.LastUpdated = DateTime.UtcNow;
            set.IsOnUncPath = false; // it's in AppData from here on

            string baseName = SanitizeName(set.Name, set.Id);
            string newPath  = BuildFilePath(_localDir, baseName, set.Id, CAPABILITY_EXT);

            // Delete old AppData file if renamed. Never delete distribution files.
            if (_filePaths.TryGetValue(set.Id, out string oldPath) &&
                !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) &&
                !IsUncPath(oldPath) &&
                oldPath.StartsWith(_localDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(oldPath); } catch { }
            }

            try
            {
                var json = JsonSerializer.Serialize(set, _opts);
                File.WriteAllText(newPath, json, System.Text.Encoding.UTF8);
                _filePaths[set.Id] = newPath;
            }
            catch (Exception ex) { DiagLogger.Log("caps", $"Save failed for '{set?.Name}': {DiagLogger.S(ex.Message)}"); }
        }

        /// <summary>
        /// Copies a distribution (UNC) capability set to AppData and marks it unlocked.
        /// No-op if already in AppData.
        /// </summary>
        public void UnlockToLocal(CapabilitySet set)
        {
            if (set == null) return;
            set.IsLocked = false;
            Save(set);
        }

        /// <summary>Deletes the set's AppData file and removes it from the in-memory list.
        /// Distribution files are never deleted.</summary>
        public void Delete(CapabilitySet set)
        {
            if (_filePaths.TryGetValue(set.Id, out string path))
            {
                if (!IsUncPath(path))
                    try { File.Delete(path); } catch { }
                _filePaths.Remove(set.Id);
            }
            _sets.Remove(set);
        }

        public void AddGroup(CapabilitySet cs, CardGroup group)    => cs.Groups.Add(group);
        public void RemoveGroup(CapabilitySet cs, CardGroup group)  => cs.Groups.Remove(group);

        /// <summary>Writes a capability set to an arbitrary path as standalone JSON (admin export).</summary>
        public void ExportSet(CapabilitySet set, string filePath)
        {
            var json = JsonSerializer.Serialize(set, _opts);
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Reads a capability set JSON file, assigns a new Id on collision,
        /// marks it local+unlocked, saves it to AppData, and returns it.
        /// </summary>
        public CapabilitySet ImportSet(string filePath)
        {
            var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var set  = JsonSerializer.Deserialize<CapabilitySet>(json, _opts)
                       ?? throw new InvalidOperationException("Empty or invalid capability set file.");

            MigrateCapabilitySet(set);

            if (_sets.Any(s => s.Id == set.Id))
                set.Id = Guid.NewGuid().ToString("N")[..8];

            set.IsLocked    = false;
            set.IsOnUncPath = false;
            _sets.Add(set);
            Save(set);
            return set;
        }

        /// <summary>
        /// Searches all CapabilitySets and their Groups for a CardGroup by Id.
        /// Returns null when not found.
        /// </summary>
        public (CapabilitySet CapSet, CardGroup Group)? FindGroup(string groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return null;
            foreach (var cs in _sets)
                foreach (var g in cs.Groups)
                    if (string.Equals(g.Id, groupId, StringComparison.Ordinal))
                        return (cs, g);
            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\")) return true;
            try
            {
                string root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) &&
                       new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (IOException) { return false; }
        }

        private static string SanitizeName(string name, string fallbackId)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallbackId;
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (invalid.Contains(c)) continue;
                switch (c)
                {
                    case ' ': sb.Append('_'); break;
                    case 'ä': sb.Append("ae"); break;
                    case 'ö': sb.Append("oe"); break;
                    case 'ü': sb.Append("ue"); break;
                    case 'Ä': sb.Append("Ae"); break;
                    case 'Ö': sb.Append("Oe"); break;
                    case 'Ü': sb.Append("Ue"); break;
                    case 'ß': sb.Append("ss"); break;
                    default:
                        if (c > 127) break;
                        if ("@#&+~()[]{}".IndexOf(c) >= 0) break;
                        sb.Append(c);
                        break;
                }
            }
            var cleaned = sb.ToString().Trim('_');
            if (cleaned.Length == 0) return fallbackId;
            return cleaned.Length > 60 ? cleaned[..60].TrimEnd('_') : cleaned;
        }

        private string BuildFilePath(string dir, string baseName, string ownId, string ext)
        {
            string candidate = Path.Combine(dir, baseName + ext);
            foreach (var kvp in _filePaths)
                if (!string.Equals(kvp.Key, ownId, StringComparison.Ordinal) &&
                    string.Equals(kvp.Value, candidate, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir, $"{baseName}_{ownId[..Math.Min(6, ownId.Length)]}{ext}");
            return candidate;
        }
    }

    internal class LegacyCapabilityStoreData
    {
        public List<CapabilitySet> CapabilitySets { get; set; } = new();
    }
}
