using System.Collections.Generic;
using System.ComponentModel;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// View-row for one item in the multi-column Logic Dropdown popup.
    /// Wraps a <see cref="CatalogDropdownItem"/> and exposes per-cell values aligned to the shared column specs.
    /// </summary>
    public sealed class LogicDropdownItemRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool _isHighlighted;
        /// <summary>True while this item is the keyboard-focused entry in the popup (arrow-key navigation).</summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set { if (_isHighlighted == value) return; _isHighlighted = value; Notify(nameof(IsHighlighted)); }
        }

        public CatalogDropdownItem Item      { get; }
        public string              GroupName => Item.GroupName;
        /// <summary>The string used by Search-card live filter (cells + search values joined by pipe).</summary>
        public string              Filterable { get; }
        /// <summary>Cells, one per shared column, in column display order.</summary>
        public IReadOnlyList<LogicDropdownCell> Cells { get; }

        public LogicDropdownItemRow(CatalogDropdownItem item, IReadOnlyList<LogicDropdownColumn> columns)
        {
            Item = item;
            var cells = new List<LogicDropdownCell>(columns.Count);
            foreach (var col in columns)
            {
                string v = col.SourceIndex >= 0 && col.SourceIndex < item.AllDisplayValues.Count
                    ? item.AllDisplayValues[col.SourceIndex]
                    : "";
                cells.Add(new LogicDropdownCell(col, v));
            }
            Cells = cells;

            var sb = new System.Text.StringBuilder();
            foreach (var c in Cells)
                if (!string.IsNullOrEmpty(c.Value)) { sb.Append(c.Value); sb.Append('|'); }
            if (item.SearchValues != null)
                foreach (var sv in item.SearchValues)
                    if (!string.IsNullOrEmpty(sv)) { sb.Append(sv); sb.Append('|'); }
            Filterable = sb.ToString();
        }
    }
}
