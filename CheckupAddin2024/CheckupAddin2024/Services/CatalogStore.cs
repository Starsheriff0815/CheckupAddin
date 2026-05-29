using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Loads catalogs from two sources and saves user copies to AppData.
    ///
    /// Sources (load order — AppData wins on same Id):
    ///   1. addinDir\Catalogs\  — distribution files (UNC in multi-user deployments).
    ///      These are loaded live each startup; files on a UNC path are auto-locked.
    ///   2. AppData\Checkup 2024\Catalogs\  — personal unlocked copies created by the user.
    ///
    /// Save() always writes to the AppData directory.
    /// Admins update the distribution by replacing files in addinDir\Catalogs\ directly.
    /// </summary>
    public class CatalogStore
    {
        public const string CATALOG_EXT      = ".catalog.json";
        public const string CATALOGS_SUBDIR  = "Catalogs";
        public const string LEGACY_FILENAME  = "Checkup_Catalogs.json";

        private readonly List<CatalogData>          _catalogs              = new List<CatalogData>();
        private readonly Dictionary<string, string> _filePaths             = new Dictionary<string, string>(); // Id → full path
        private readonly Dictionary<string, string> _distributionFilePaths = new Dictionary<string, string>(); // Id → distribution path

        private readonly string _localDir;        // AppData\Checkup 2024\Catalogs\
        private readonly string _distributionDir; // addinDir\Catalogs\ (may be UNC)

        public IReadOnlyList<CatalogData> Catalogs => _catalogs;

        /// <summary>True when the catalog's file lives on a UNC path (\\server\...).</summary>
        public bool IsOnUncPath(string id) =>
            _filePaths.TryGetValue(id, out var p) && IsUncPath(p);

        private CatalogStore(string localDir, string distributionDir)
        {
            _localDir        = localDir;
            _distributionDir = distributionDir ?? "";
        }

        /// <summary>
        /// Loads all catalogs from the distribution directory (addinDir\Catalogs\) first,
        /// then from the user's AppData directory. AppData wins for the same Id.
        /// </summary>
        public static CatalogStore Load(string addinDir, string localDir)
        {
            string localCatDir = Path.Combine(localDir, CATALOGS_SUBDIR);
            string distCatDir  = string.IsNullOrEmpty(addinDir)
                ? "" : Path.Combine(addinDir, CATALOGS_SUBDIR);

            var store = new CatalogStore(localCatDir, distCatDir);
            try { Directory.CreateDirectory(localCatDir); } catch { }

            // 1. Distribution files (addinDir\Catalogs\) — UNC path → auto-locked
            if (!string.IsNullOrEmpty(distCatDir))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(distCatDir, $"*{CATALOG_EXT}"))
                        store.LoadFile(f, fromDistribution: true);
                }
                catch { }
            }

            // 2. User AppData files — override same Id from distribution; sync gap detected here
            try
            {
                foreach (var f in Directory.GetFiles(localCatDir, $"*{CATALOG_EXT}"))
                    store.LoadFile(f, fromDistribution: false);
            }
            catch { }

            return store;
        }

        private void LoadFile(string filePath, bool fromDistribution = false)
        {
            try
            {
                var json    = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                var catalog = JsonConvert.DeserializeObject<CatalogData>(json);
                if (catalog == null) return;
                if (string.IsNullOrEmpty(catalog.Id))
                    catalog.Id = Guid.NewGuid().ToString("N").Substring(0, 8);

                MigrateCapabilitySet_Unused(catalog);

                // Derive runtime flags from physical path — never trust the JSON flag.
                catalog.IsOnUncPath = IsUncPath(filePath);
                if (catalog.IsOnUncPath)
                    catalog.IsLocked = true;

                if (fromDistribution)
                {
                    _distributionFilePaths[catalog.Id] = filePath;
                }
                else
                {
                    // Sync gap: distribution was loaded first; check if it has a newer timestamp.
                    int distIdx = _catalogs.FindIndex(c => c.Id == catalog.Id);
                    if (distIdx >= 0 && _distributionFilePaths.ContainsKey(catalog.Id))
                    {
                        var distVersion = _catalogs[distIdx];
                        if (distVersion.LastUpdated != default && distVersion.LastUpdated > catalog.LastUpdated)
                            catalog.HasUpdateAvailable = true;
                    }
                }

                int existing = _catalogs.FindIndex(c => c.Id == catalog.Id);
                if (existing >= 0)
                {
                    _catalogs[existing]    = catalog;
                    _filePaths[catalog.Id] = filePath;
                }
                else
                {
                    _catalogs.Add(catalog);
                    _filePaths[catalog.Id] = filePath;
                }
            }
            catch { }
        }

        /// <summary>
        /// Deletes the AppData copy and restores the distribution version as the live entry.
        /// Returns the reloaded distribution object, or null if the distribution file is not found.
        /// </summary>
        public CatalogData RevertToDistribution(CatalogData catalog)
        {
            if (catalog == null) return null;

            // Delete AppData file
            if (_filePaths.TryGetValue(catalog.Id, out string localPath) &&
                !IsUncPath(localPath) &&
                localPath.StartsWith(_localDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(localPath); } catch { }
            }

            // Remove from memory and reload the distribution version
            _catalogs.RemoveAll(c => c.Id == catalog.Id);
            _filePaths.Remove(catalog.Id);

            if (_distributionFilePaths.TryGetValue(catalog.Id, out string distPath))
            {
                LoadFile(distPath, fromDistribution: true);
                return _catalogs.Find(c => c.Id == catalog.Id);
            }
            return null;
        }

        /// <summary>
        /// Saves the catalog to the user's AppData directory and bumps LastUpdated.
        /// If the catalog was previously on a UNC path, it becomes a local AppData copy.
        /// </summary>
        public void Save(CatalogData catalog)
        {
            if (catalog == null) return;
            if (!_catalogs.Any(c => c.Id == catalog.Id))
                _catalogs.Add(catalog);

            try { Directory.CreateDirectory(_localDir); } catch { }

            catalog.LastUpdated = DateTime.UtcNow;
            catalog.IsOnUncPath = false; // it's in AppData from here on

            string baseName = SanitizeName(catalog.Name, catalog.Id);
            string newPath  = BuildFilePath(_localDir, baseName, catalog.Id, CATALOG_EXT);

            // Delete old AppData file if it was renamed or moved.
            // Never delete distribution files (UNC or addinDir).
            if (_filePaths.TryGetValue(catalog.Id, out string oldPath) &&
                !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) &&
                !IsUncPath(oldPath) &&
                oldPath.StartsWith(_localDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(oldPath); } catch { }
            }

            try
            {
                var json = JsonConvert.SerializeObject(catalog, Formatting.Indented);
                File.WriteAllText(newPath, json, System.Text.Encoding.UTF8);
                _filePaths[catalog.Id] = newPath;
            }
            catch { }
        }

        /// <summary>
        /// Copies a distribution (UNC) catalog to AppData and marks it unlocked.
        /// No-op if already in AppData.
        /// </summary>
        public void UnlockToLocal(CatalogData catalog)
        {
            if (catalog == null) return;
            catalog.IsLocked = false;
            Save(catalog); // Save always targets AppData and clears IsOnUncPath
        }

        /// <summary>Deletes the catalog's AppData file and removes it from the in-memory list.
        /// Distribution files are never deleted.</summary>
        public void Delete(CatalogData catalog)
        {
            if (_filePaths.TryGetValue(catalog.Id, out string path))
            {
                if (!IsUncPath(path))
                    try { File.Delete(path); } catch { }
                _filePaths.Remove(catalog.Id);
            }
            _catalogs.Remove(catalog);
        }

        public void AddGroup(CapabilitySet cs, CardGroup group)   => cs.Groups.Add(group);
        public void RemoveGroup(CapabilitySet cs, CardGroup group) => cs.Groups.Remove(group);

        /// <summary>Writes a catalog to an arbitrary path as standalone JSON (admin export).</summary>
        public void ExportCatalog(CatalogData catalog, string filePath)
        {
            var json = JsonConvert.SerializeObject(catalog, Formatting.Indented);
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Reads a catalog JSON file, assigns a new Id on collision,
        /// marks it local+unlocked, saves it to AppData, and returns it.
        /// </summary>
        public CatalogData ImportCatalog(string filePath)
        {
            var json    = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var catalog = JsonConvert.DeserializeObject<CatalogData>(json)
                          ?? throw new InvalidOperationException("Empty or invalid catalog file.");

            if (_catalogs.Any(c => c.Id == catalog.Id))
                catalog.Id = Guid.NewGuid().ToString("N").Substring(0, 8);

            catalog.IsLocked    = false;
            catalog.IsOnUncPath = false;
            _catalogs.Add(catalog);
            Save(catalog);
            return catalog;
        }

        /// <summary>
        /// Searches all loaded catalogs for a CardGroup by Id.
        /// (Catalogs don't contain CardGroups — reserved for future use.)
        /// </summary>
        public (CatalogData Catalog, CardGroup Group)? FindGroup(string groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return null;
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
            return cleaned.Length > 60 ? cleaned.Substring(0, 60).TrimEnd('_') : cleaned;
        }

        private static void MigrateCapabilitySet_Unused(CatalogData _) { }

        private string BuildFilePath(string dir, string baseName, string ownId, string ext)
        {
            string candidate = Path.Combine(dir, baseName + ext);
            foreach (var kvp in _filePaths)
                if (!string.Equals(kvp.Key, ownId, StringComparison.Ordinal) &&
                    string.Equals(kvp.Value, candidate, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir, $"{baseName}_{ownId.Substring(0, Math.Min(6, ownId.Length))}{ext}");
            return candidate;
        }
    }

    internal class LegacyCatalogStoreData
    {
        public List<CatalogData> Catalogs { get; set; } = new List<CatalogData>();
    }
}
