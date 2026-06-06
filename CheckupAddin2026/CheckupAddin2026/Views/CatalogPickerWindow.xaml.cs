using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CheckupAddIn.Models;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    public partial class CatalogPickerWindow : Window
    {
        private readonly IReadOnlyList<CatalogDropdownItem> _allItems;
        private readonly IReadOnlyList<CatalogTabEntry>     _tabs;
        private readonly string                              _catalogId;

        private readonly ObservableCollection<PickerTabVm>  _tabVms = new();
        private readonly ObservableCollection<PickerItemVm> _visibleItems = new();
        private ListCollectionView _view;

        private string _activeTabId = "";   // "" = All
        private PickerItemVm _selected;     // single-select only

        // Multi-select mode
        private readonly bool            _isMultiSelect;
        private readonly HashSet<string> _selectedPriValuesSet;  // canonical selection across tab switches

        public string SelectedPriValue { get; private set; }

        /// <summary>
        /// Selected PRI values in catalog order (multi-select mode only).
        /// Empty list when no items are selected.
        /// </summary>
        public IReadOnlyList<string> SelectedPriValues { get; private set; } = Array.Empty<string>();

        /// <param name="multiSelect">When true, opens in multi-select mode (checkboxes stay on; OK always enabled).</param>
        /// <param name="preSelectedPriValues">PRI values to pre-check when opening in multi-select mode.</param>
        public CatalogPickerWindow(
            IReadOnlyList<CatalogDropdownItem> items,
            IReadOnlyList<CatalogTabEntry>     tabs,
            string                             catalogId,
            Inventor.Application               app = null,
            bool                               multiSelect          = false,
            IReadOnlyList<string>              preSelectedPriValues = null)
        {
            _isMultiSelect        = multiSelect;
            _selectedPriValuesSet = multiSelect && preSelectedPriValues != null
                ? new HashSet<string>(preSelectedPriValues, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            InitializeComponent();

            _allItems  = items  ?? new List<CatalogDropdownItem>();
            _tabs      = tabs   ?? new List<CatalogTabEntry>();
            _catalogId = catalogId ?? "";

            ThemeLoader.ApplyTo(this, app);
            LanguageLoader.ApplyTo(this);

            // ── Build tab buttons (All + per-tab) ──
            if (_tabs.Count > 0)
            {
                _tabVms.Add(new PickerTabVm("", LanguageLoader.Get("CatalogPicker_All")));
                foreach (var t in _tabs)
                    _tabVms.Add(new PickerTabVm(t.TabId, t.Label));

                TabButtonsHost.ItemsSource = _tabVms;
                TabRowBorder.Visibility    = Visibility.Visible;

                // Restore last tab
                string lastTab = UiStateStore.LoadCatalogPickerLastTab(_catalogId);
                string match   = _tabVms.Any(tv => tv.TabId == lastTab) ? lastTab : "";
                ActivateTab(match, save: false);
            }
            else
            {
                TabRowBorder.Visibility = Visibility.Collapsed;
                ActivateTab("", save: false);
            }

            // ── Build list view ──
            _view = new ListCollectionView(_visibleItems);
            if (_allItems.Any(i => !string.IsNullOrEmpty(i.GroupName)))
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerItemVm.GroupName)));
            _view.Filter = FilterItem;
            ItemList.ItemsSource = _view;

            // ── Restore window size ──
            if (!UiStateStore.TryLoadCatalogPickerSize(out double w, out double h))
            { w = 480; h = 520; }
            Width = w; Height = h;

            // In multi-select mode OK is always enabled (allows selecting zero = clear the field)
            if (_isMultiSelect)
            {
                BtnOk.IsEnabled          = true;
                PreviewBorder.Visibility = Visibility.Visible;
                UpdateSelectionUi(null);
            }

            SearchBox.Focus();
        }

        // ═══════════════════════════════════════════
        // Tab management
        // ═══════════════════════════════════════════

        private void ActivateTab(string tabId, bool save = true)
        {
            _activeTabId = tabId;
            foreach (var tv in _tabVms)
                tv.IsActive = tv.TabId == tabId;

            if (save)
                UiStateStore.SaveCatalogPickerLastTab(_catalogId, tabId);

            RebuildVisibleItems();
        }

        private void RebuildVisibleItems()
        {
            _visibleItems.Clear();
            foreach (var item in _allItems)
            {
                if (_activeTabId != "" && item.TabId != _activeTabId) continue;
                var vm = new PickerItemVm(item, OnItemToggled);
                // Multi-select: pre-check items that are in the canonical selection set
                if (_isMultiSelect && _selectedPriValuesSet.Contains(item.PriValue))
                    vm.SetSelectedSilent(true);
                _visibleItems.Add(vm);
            }

            if (!_isMultiSelect)
            {
                // Single-select: re-apply previous selection to the rebuilt items
                if (_selected != null)
                {
                    var match = _visibleItems.FirstOrDefault(vm => vm.PriValue == _selected.PriValue);
                    if (match != null)
                    {
                        match.IsSelected = true;
                        _selected = match;
                    }
                    else
                    {
                        _selected = null;
                        UpdateSelectionUi(null);
                    }
                }
            }

            _view?.Refresh();
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tabId)
                ActivateTab(tabId);
        }

        // ═══════════════════════════════════════════
        // Filter
        // ═══════════════════════════════════════════

        private bool FilterItem(object obj)
        {
            if (obj is not PickerItemVm vm) return false;
            string f = SearchBox.Text;
            if (string.IsNullOrEmpty(f)) return true;
            if (vm.PriValue.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (vm.SecValue.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var extra in vm.ExtraDisplayValues)
                if (!string.IsNullOrEmpty(extra) && extra.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => _view?.Refresh();

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                ItemList.Focus();
                if (_view?.Count > 0 && ItemList.SelectedIndex < 0)
                    ItemList.SelectedIndex = 0;
                (ItemList.ItemContainerGenerator.ContainerFromIndex(
                    Math.Max(0, ItemList.SelectedIndex)) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (_selected != null || _isMultiSelect))
            {
                Confirm();
                e.Handled = true;
            }
        }

        private void ItemList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) e.Handled = true;  // prevent accidental form submit
        }

        // ═══════════════════════════════════════════
        // Selection (single-pick with pick/unpick)
        // ═══════════════════════════════════════════

        private void OnItemToggled(PickerItemVm vm, bool newValue)
        {
            if (_isMultiSelect)
            {
                if (newValue) _selectedPriValuesSet.Add(vm.PriValue);
                else          _selectedPriValuesSet.Remove(vm.PriValue);
                UpdateSelectionUi(null);
            }
            else
            {
                if (newValue)
                {
                    if (_selected != null && _selected != vm)
                        _selected.SetSelectedSilent(false);
                    _selected = vm;
                }
                else
                {
                    if (_selected == vm) _selected = null;
                }
                UpdateSelectionUi(_selected);
            }
        }

        private void UpdateSelectionUi(PickerItemVm vm)
        {
            if (_isMultiSelect)
            {
                // Always enabled; preview shows count and selected codes
                int count = _selectedPriValuesSet.Count;
                PreviewText.Text = count == 0
                    ? "—"
                    : $"{count}×  {string.Join(", ", _selectedPriValuesSet)}";
            }
            else
            {
                bool has = vm != null;
                BtnOk.IsEnabled          = has;
                PreviewBorder.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                if (has)
                {
                    string preview = string.IsNullOrEmpty(vm.SecValue)
                        ? vm.PriValue
                        : $"{vm.PriValue}  —  {vm.SecValue}";
                    PreviewText.Text = preview;
                }
            }
        }

        // ═══════════════════════════════════════════
        // OK / Cancel / Close
        // ═══════════════════════════════════════════

        private void OK_Click(object sender, RoutedEventArgs e) => Confirm();

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; return; }
            base.OnPreviewKeyDown(e);
        }

        private void Confirm()
        {
            if (_isMultiSelect)
            {
                // Output in catalog order (preserves TST→GST→SRT sorting defined in the catalog)
                SelectedPriValues = _allItems
                    .Where(i => _selectedPriValuesSet.Contains(i.PriValue))
                    .Select(i => i.PriValue)
                    .ToList();
                DialogResult = true;
                Close();
            }
            else
            {
                if (_selected == null) return;
                SelectedPriValue = _selected.PriValue;
                DialogResult     = true;
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            UiStateStore.SaveCatalogPickerSize(Width, Height);
            base.OnClosing(e);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Inner view-model: one tab button
    // ═══════════════════════════════════════════════════════════════

    internal sealed class PickerTabVm : INotifyPropertyChanged
    {
        public string TabId { get; }
        public string Label { get; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
        }

        public PickerTabVm(string tabId, string label)
        { TabId = tabId ?? ""; Label = label ?? ""; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ═══════════════════════════════════════════════════════════════
    // Inner view-model: one catalog item with pick/unpick checkbox
    // ═══════════════════════════════════════════════════════════════

    internal sealed class PickerItemVm : INotifyPropertyChanged
    {
        private bool _isSelected;
        private readonly Action<PickerItemVm, bool> _onToggle;

        public PickerItemVm(CatalogDropdownItem item, Action<PickerItemVm, bool> onToggle)
        {
            Item      = item;
            _onToggle = onToggle;
        }

        public CatalogDropdownItem Item      { get; }
        public string PriValue   => Item.PriValue;
        public string SecValue   => Item.SecValue;
        public string AuxValue   => Item.AuxValue;
        public string GroupName  => Item.GroupName;
        public IReadOnlyList<string> ExtraDisplayValues => Item.ExtraDisplayValues;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                _onToggle?.Invoke(this, value);
            }
        }

        /// <summary>Sets IsSelected without firing the toggle callback (used to deselect without re-entering the callback).</summary>
        public void SetSelectedSilent(bool value)
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
