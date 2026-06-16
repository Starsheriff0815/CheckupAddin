using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace CheckupAddIn.Models
{
    /// <summary>A single logic block within a Card Group.</summary>
    public class CapabilityCard
    {
        public string                     Id        { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string                     Type      { get; set; } = "";
        public bool                       Enabled   { get; set; } = true;
        /// <summary>Id of the CatalogData this card reads from (Dropdown / Button / Search cards).</summary>
        public string                     CatalogId { get; set; } = "";
        public Dictionary<string, string> Params    { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// A group of capability cards that together drive one row in the Checkup main window.
    /// Each CardGroup maps to one TargetFieldKey entry and appears as "S: &lt;field&gt;" in the
    /// field-selector dropdown. Multiple groups within a CapabilitySet produce multiple rows.
    /// </summary>
    public class CardGroup
    {
        public string               Id             { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string               Name           { get; set; } = "";
        public string               TargetFieldKey { get; set; } = "";
        public List<CapabilityCard> Cards          { get; set; } = new List<CapabilityCard>();
        public bool                 IsExpert       { get; set; } = false;
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

        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

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
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsShared { get; set; } = false;

        // ── Data ──────────────────────────────────────────────────────────────
        public List<CardGroup> Groups { get; set; } = new List<CardGroup>();

        // ── v1 legacy fields — present in old JSON only; migration converts them. ──
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string CatalogId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string TargetFieldKey { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<CapabilityCard> Cards { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
