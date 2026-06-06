using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CheckupAddIn.Models
{
    /// <summary>A single logic block within a Card Group.</summary>
    public class CapabilityCard
    {
        /// <summary>Stable identifier for per-user UI state (e.g. F1 IsCollapsed). Auto-generated; persisted in JSON.</summary>
        public string                     Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string                     Type      { get; set; } = "";
        public bool                       Enabled   { get; set; } = true;
        /// <summary>Id of the CatalogData this card reads from (Dropdown / Button / Search cards).</summary>
        public string                     CatalogId { get; set; } = "";
        public Dictionary<string, string> Params    { get; set; } = new();
    }

    /// <summary>
    /// A group of capability cards that together drive one row in the Checkup main window.
    /// Each CardGroup maps to one TargetFieldKey entry and appears as "S: &lt;field&gt;" in the
    /// field-selector dropdown. Multiple groups within a CapabilitySet produce multiple rows.
    /// </summary>
    public class CardGroup
    {
        public string               Id             { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string               Name           { get; set; } = "";
        public string               TargetFieldKey { get; set; } = "";
        public List<CapabilityCard> Cards          { get; set; } = new();

        /// <summary>
        /// V1 Expert Mode flag. When true, this group is in the Expert section of the
        /// Logics-Constructor and its Basic Logics can reference live field values via
        /// $[FIELD_KEY] syntax. Default false (Normal group).
        /// </summary>
        public bool IsExpert { get; set; } = false;
    }

    /// <summary>
    /// A named, reusable logic set containing one or more Card Groups.
    /// One CapabilitySet = one entry in the Logic Builder list.
    /// Each of its Groups produces one row in the Checkup main window.
    /// </summary>
    public class CapabilitySet : INotifyPropertyChanged
    {
        private string   _name        = "New Capability Set";
        private bool     _isLocked    = false;
        private DateTime _lastUpdated = default;

        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; Notify(nameof(Name)); }
        }

        /// <summary>
        /// When true the file is edit-protected in the Logic Builder.
        /// Always true at runtime for sets loaded from a UNC distribution path —
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
        /// <summary>True when the file lives on a UNC path (\\server\...). Set by CapabilityStore.LoadFile.</summary>
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsShared { get; set; } = false;

        // ── Data ──────────────────────────────────────────────────────────────
        public List<CardGroup> Groups { get; set; } = new();

        // ── v1 legacy fields — present in old JSON only; migration converts them. ──
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CatalogId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string TargetFieldKey { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CapabilityCard> Cards { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
