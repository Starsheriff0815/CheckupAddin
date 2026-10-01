using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CheckupAddIn.Models
{
    /// <summary>One selectable iLogic Rule in the Rule Selector popup (TDD §10.4).</summary>
    public sealed class RuleSelectorItem
    {
        /// <summary>Field Key assigned to the Row when this entry is picked (<c>SPECIAL:RULE:…</c>).</summary>
        public string Key          { get; init; } = "";
        /// <summary>Text shown in the list — file name (External) or rule name (Document).</summary>
        public string DisplayName  { get; init; } = "";
        /// <summary>Full path (file rules) or rule name (Document Rules) — shown as tooltip.</summary>
        public string ToolTip      { get; init; } = "";
        /// <summary>False for deactivated Document Rules: listed greyed, not selectable (D14).</summary>
        public bool   IsSelectable { get; init; } = true;
    }

    /// <summary>One group (folder / Document Rules / Checkup) in the Rule Selector popup.</summary>
    public sealed class RuleSelectorGroupVm : INotifyPropertyChanged
    {
        public string GroupDisplayName { get; init; } = "";

        /// <summary>All entries (natural order). Unfiltered.</summary>
        public IReadOnlyList<RuleSelectorItem> AllItems { get; init; } = System.Array.Empty<RuleSelectorItem>();

        private IReadOnlyList<RuleSelectorItem> _filteredItems = System.Array.Empty<RuleSelectorItem>();
        /// <summary>Entries matching the current search filter.</summary>
        public IReadOnlyList<RuleSelectorItem> FilteredItems
        {
            get => _filteredItems;
            set { _filteredItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFilteredItems)); }
        }

        public bool HasFilteredItems => _filteredItems.Count > 0;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
