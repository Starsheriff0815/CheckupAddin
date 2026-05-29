using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// Semantic role a column plays in the catalog's capability engine.
    /// Roles drive Phase 3 behavior (picker UI, field mapping, sort order).
    /// </summary>
    public enum ColumnRole
    {
        None             = 0, // unassigned — column carries data with no special behavior
        PrimaryDisplay   = 1, // the short / primary display value (e.g. SPEZI1 token)
        SecondaryDisplay = 2, // the long / secondary display value (e.g. SPEZI2 token)
        TabId            = 3, // groups entries into top-level picker tabs
        GroupId          = 4, // groups entries within a tab
        SortKey          = 5, // sort order for items within a group; int=5 keeps JSON backward-compat
        Auxiliary        = 6, // extra data (notes, tooltips, flags) not shown directly
        GroupSortKey     = 7, // sort order for groups within a tab
        TabSortKey       = 8, // sort order for tabs within the picker
    }

    /// <summary>One user-defined column in a catalog's schema.</summary>
    public class CatalogColumn
    {
        public string     Key       { get; set; } = "";
        public string     Label     { get; set; } = "";
        public ColumnRole Role      { get; set; } = ColumnRole.None;
        /// <summary>1-based priority index within this role type; auto-assigned by the VM.</summary>
        public int        RoleIndex { get; set; } = 1;
    }

    /// <summary>One data row in a catalog.</summary>
    public class CatalogEntry
    {
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>A named catalog: schema (columns) + data (entries).</summary>
    public class CatalogData : INotifyPropertyChanged
    {
        private string   _name        = "New Catalog";
        private bool     _isLocked    = false;
        private DateTime _lastUpdated = default;

        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; Notify(nameof(Name)); }
        }

        /// <summary>
        /// When true the file is edit-protected in the Logic Builder.
        /// Always true at runtime for catalogs loaded from a UNC distribution path —
        /// the store overrides this flag on load regardless of the JSON value.
        /// </summary>
        public bool IsLocked
        {
            get => _isLocked;
            set { if (_isLocked == value) return; _isLocked = value; Notify(nameof(IsLocked)); Notify(nameof(IsLockedDisplay)); }
        }

        /// <summary>UTC timestamp of the last Save(). Default (MinValue) means never saved via this version.</summary>
        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set { if (_lastUpdated == value) return; _lastUpdated = value; Notify(nameof(LastUpdated)); Notify(nameof(LastUpdatedDisplay)); }
        }

        // ── Runtime-only (not serialized) ─────────────────────────────────────
        /// <summary>True when the file lives on a UNC path (\\server\...). Set by CatalogStore.LoadFile.</summary>
        [JsonIgnore] public bool IsOnUncPath { get; set; } = false;

        [JsonIgnore] public string LastUpdatedDisplay =>
            _lastUpdated == default ? "–" : _lastUpdated.ToLocalTime().ToString("g");

        [JsonIgnore] public string IsLockedDisplay =>
            _isLocked ? "🔒" : "🔓";

        // ── Runtime-only sync gap flag ────────────────────────────────────────
        private bool _hasUpdateAvailable = false;
        /// <summary>True when a distribution version newer than this AppData copy was found at load time.</summary>
        [JsonIgnore] public bool HasUpdateAvailable
        {
            get => _hasUpdateAvailable;
            set { if (_hasUpdateAvailable == value) return; _hasUpdateAvailable = value; Notify(nameof(HasUpdateAvailable)); }
        }

        // ── Legacy field — read from old JSON but never written ────────────────
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsShared { get; set; } = false;

        // ── Data ──────────────────────────────────────────────────────────────
        public List<CatalogColumn> Columns { get; set; } = new List<CatalogColumn>();
        public List<CatalogEntry>  Entries { get; set; } = new List<CatalogEntry>();

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
