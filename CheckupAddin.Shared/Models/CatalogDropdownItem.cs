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
        /// <summary>TAB column value — used by the catalog picker window to filter by tab.</summary>
        public string TabId     { get; }
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
            ExtraDisplayValues = extraDisplayValues ?? System.Array.Empty<string>();
            SearchValues       = searchValues       ?? System.Array.Empty<string>();

            var all = new List<string>(3 + ExtraDisplayValues.Count) { PriValue, SecValue, AuxValue };
            all.AddRange(ExtraDisplayValues);
            AllDisplayValues = all;
        }

        // WPF editable ComboBox uses ToString() to populate PART_EditableTextBox after selection.
        public override string ToString() => PriValue;
    }
}
