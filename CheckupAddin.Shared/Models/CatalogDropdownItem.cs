using System.Collections.Generic;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// A single item in a Dropdown-card ComboBox: the PRI value that is written on selection,
    /// the SEC value shown as secondary description, the AUX value shown as a tooltip,
    /// and optional extra display-only columns shown in the picker window.
    /// </summary>
    public sealed class CatalogDropdownItem
    {
        public string PriValue  { get; }
        public string SecValue  { get; }
        public string AuxValue  { get; }
        /// <summary>GRP column value — used by ListCollectionView to group items under a header.</summary>
        public string GroupName { get; }
        /// <summary>TAB column value — raw cell, used by the catalog picker window to filter by tab.</summary>
        public string TabId     { get; }
        /// <summary>The TAB cell split into distinct tab ids (comma-separated cell → multi-tab membership, Task #40).
        /// A cell with no comma yields exactly one id, so single-tab catalogs are unaffected. Case-insensitive.</summary>
        public IReadOnlyCollection<string> TabIds => _tabIds;
        private readonly HashSet<string> _tabIds;
        /// <summary>Additional visual-only column values shown in the picker window (Display_N_Role columns).</summary>
        public IReadOnlyList<string> ExtraDisplayValues { get; }
        /// <summary>Values checked by the Search card's live filter. Empty = filter checks PriValue + SecValue by default.</summary>
        public IReadOnlyList<string> SearchValues { get; }
        /// <summary>All column values in display order: [PRI, SEC, AUX, ...extras]. Built once in the ctor.</summary>
        public IReadOnlyList<string> AllDisplayValues { get; }

        public CatalogDropdownItem(string pri, string sec, string aux, string groupName = "", string tabId = "",
                                   IReadOnlyList<string> extraDisplayValues = null,
                                   IReadOnlyList<string> searchValues = null)
        {
            PriValue           = pri ?? "";
            SecValue           = sec ?? "";
            AuxValue           = aux ?? "";
            GroupName          = groupName ?? "";
            TabId              = tabId ?? "";
            _tabIds            = SplitTabIds(TabId);
            ExtraDisplayValues = extraDisplayValues ?? System.Array.Empty<string>();
            SearchValues       = searchValues       ?? System.Array.Empty<string>();

            var all = new List<string>(3 + ExtraDisplayValues.Count) { PriValue, SecValue, AuxValue };
            all.AddRange(ExtraDisplayValues);
            AllDisplayValues = all;
        }

        /// <summary>True if this item belongs to the given tab id (case-insensitive). The "All" tab ("") is handled by the caller, never here. (Task #40)</summary>
        public bool IsInTab(string tabId) => !string.IsNullOrEmpty(tabId) && _tabIds.Contains(tabId);

        /// <summary>Splits a TAB-cell value into distinct, trimmed tab ids (comma-delimited). Empty/whitespace tokens dropped; case-insensitive distinct. A no-comma cell yields a single id. (Task #40)</summary>
        public static HashSet<string> SplitTabIds(string cell)
        {
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cell)) return set;
            foreach (var raw in cell.Split(','))
            {
                var t = raw.Trim();
                if (t.Length != 0) set.Add(t);
            }
            return set;
        }

        // WPF editable ComboBox uses ToString() to populate PART_EditableTextBox after selection.
        public override string ToString() => PriValue;
    }
}
