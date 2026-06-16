using System.ComponentModel;
using System.Windows;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// Shared column spec for the multi-column Logic Dropdown / Search popup.
    /// One instance per column is created when the popup opens; all cells (header + per-item)
    /// reference the same instance so width changes propagate via INPC.
    /// </summary>
    public sealed class LogicDropdownColumn : INotifyPropertyChanged
    {
        private double _width;
        private bool   _isLastColumn;

        public LogicDropdownColumn(string label, double initialWidth, bool isLastColumn, int sourceIndex)
        {
            Label         = label ?? "";
            _width        = initialWidth;
            _isLastColumn = isLastColumn;
            SourceIndex   = sourceIndex;
        }

        public string Label       { get; }
        /// <summary>Index into <see cref="CatalogDropdownItem.AllDisplayValues"/> for the cell value.</summary>
        public int    SourceIndex { get; }

        /// <summary>Current rendered width in DIPs. Changes propagate to all bound cells.</summary>
        public double Width
        {
            get => _width;
            set
            {
                double clamped = value < 30 ? 30 : value;
                if (System.Math.Abs(_width - clamped) < 0.5) return;
                _width = clamped;
                OnPropertyChanged("Width");
                OnPropertyChanged("GridLength");
            }
        }

        /// <summary>True for the right-most column — its separator Thumb is hidden.</summary>
        public bool IsLastColumn
        {
            get => _isLastColumn;
            set { if (_isLastColumn == value) return; _isLastColumn = value; OnPropertyChanged("IsLastColumn"); }
        }

        public GridLength GridLength => new GridLength(_width);

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// One cell in a Logic-dropdown row: references a shared <see cref="LogicDropdownColumn"/>
    /// (for the live Width) and carries the per-item text value.
    /// </summary>
    public sealed class LogicDropdownCell
    {
        public LogicDropdownColumn Column { get; }
        public string              Value  { get; }

        public LogicDropdownCell(LogicDropdownColumn column, string value)
        {
            Column = column;
            Value  = value ?? "";
        }
    }
}
