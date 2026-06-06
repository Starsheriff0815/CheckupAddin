using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// View-model for one collapsible group in the Field Selector scrollable zone.
    /// Owned by CheckupViewModel; rebuilt when the catalog changes or the filter text changes.
    /// </summary>
    public sealed class FieldSelectorGroupVm : INotifyPropertyChanged
    {
        public string GroupName        { get; init; } = "";
        public string GroupDisplayName { get; init; } = "";

        /// <summary>All items in this group (unfiltered). Rebuilt when catalog changes.</summary>
        public IReadOnlyList<FieldItem> AllItems { get; init; } = System.Array.Empty<FieldItem>();

        private IReadOnlyList<FieldItem> _filteredItems = System.Array.Empty<FieldItem>();
        /// <summary>Items visible under current search filter. Updated by CheckupViewModel.ApplyFieldSelectorFilter().</summary>
        public IReadOnlyList<FieldItem> FilteredItems
        {
            get => _filteredItems;
            set { _filteredItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFilteredItems)); }
        }

        public bool HasFilteredItems => _filteredItems.Count > 0;

        private bool _isCollapsed;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set { _isCollapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsExpanded)); }
        }

        public bool IsExpanded => !_isCollapsed;

        /// <summary>
        /// False when the group should auto-collapse and not allow manual expand.
        /// True for Sonderfunktionen when only hardcoded entries remain (no active LC groups).
        /// </summary>
        private bool _isChevronEnabled = true;
        public bool IsChevronEnabled
        {
            get => _isChevronEnabled;
            set { _isChevronEnabled = value; OnPropertyChanged(); }
        }

        public ICommand ToggleCollapseCommand { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
