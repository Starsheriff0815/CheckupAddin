using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        private readonly ObservableCollection<PickerTabVm>  _tabVms       = new ObservableCollection<PickerTabVm>();
        private readonly ObservableCollection<PickerItemVm> _visibleItems = new ObservableCollection<PickerItemVm>();
        private ListCollectionView _view;

        private string       _activeTabId = "";
        private PickerItemVm _selected;

        private readonly bool            _isMultiSelect;
        private readonly HashSet<string> _selectedPriValuesSet;

        public string                SelectedPriValue  { get; private set; }
        public IReadOnlyList<string> SelectedPriValues { get; private set; } = System.Array.Empty<string>();

        public CatalogPickerWindow(
            IReadOnlyList<CatalogDropdownItem> items,
            IReadOnlyList<CatalogTabEntry>     tabs,
            string                             catalogId,
            Inventor.Application               app                  = null,
            bool                               multiSelect          = false,
            IReadOnlyList<string>              preSelectedPriValues = null)
        {
            _isMultiSelect        = multiSelect;
            _selectedPriValuesSet = multiSelect && preSelectedPriValues != null
                ? new HashSet<string>(preSelectedPriValues, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            InitializeComponent();

            _allItems  = items     ?? new List<CatalogDropdownItem>();
            _tabs      = tabs      ?? new List<CatalogTabEntry>();
            _catalogId = catalogId ?? "";

            ThemeLoader.ApplyTo(this, app);
            LanguageLoader.ApplyTo(this);

            if (_tabs.Count > 0)
            {
                _tabVms.Add(new PickerTabVm("", LanguageLoader.Get("CatalogPicker_All")));
                foreach (var t in _tabs)
                    _tabVms.Add(new PickerTabVm(t.TabId, t.Label));

                TabButtonsHost.ItemsSource = _tabVms;
                TabRowBorder.Visibility    = Visibility.Visible;

                string lastTab = UiStateStore.LoadCatalogPickerLastTab(_catalogId);
                bool   found   = false;
                foreach (var tv in _tabVms)
                    if (tv.TabId == lastTab) { found = true; break; }
                ActivateTab(found ? lastTab : "", save: false);
            }
            else
            {
                TabRowBorder.Visibility = Visibility.Collapsed;
                ActivateTab("", save: false);
            }

            _view = new ListCollectionView(_visibleItems);
            bool hasGroups = false;
            foreach (var item in _allItems)
                if (!string.IsNullOrEmpty(item.GroupName)) { hasGroups = true; break; }
            if (hasGroups)
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerItemVm.GroupName)));
            _view.Filter = FilterItem;
            ItemList.ItemsSource = _view;

            if (!UiStateStore.TryLoadCatalogPickerSize(out double w, out double h))
            { w = 480; h = 520; }
            Width = w; Height = h;

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
                if (_isMultiSelect && _selectedPriValuesSet.Contains(item.PriValue))
                    vm.SetSelectedSilent(true);
                _visibleItems.Add(vm);
            }

            if (!_isMultiSelect && _selected != null)
            {
                PickerItemVm match = null;
                foreach (var vm in _visibleItems)
                    if (vm.PriValue == _selected.PriValue) { match = vm; break; }
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
            if (vm.PriValue.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (vm.SecValue.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var extra in vm.ExtraDisplayValues)
                if (!string.IsNullOrEmpty(extra) && extra.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
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
            if (e.Key == Key.Enter) e.Handled = true;
        }

        // ═══════════════════════════════════════════
        // Selection
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
                    PreviewText.Text = string.IsNullOrEmpty(vm.SecValue)
                        ? vm.PriValue
                        : $"{vm.PriValue}  —  {vm.SecValue}";
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
                var result = new List<string>();
                foreach (var i in _allItems)
                    if (_selectedPriValuesSet.Contains(i.PriValue)) result.Add(i.PriValue);
                SelectedPriValues = result;
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

        public CatalogDropdownItem       Item              { get; }
        public string                    PriValue          => Item.PriValue;
        public string                    SecValue          => Item.SecValue;
        public string                    AuxValue          => Item.AuxValue;
        public string                    GroupName         => Item.GroupName;
        public IReadOnlyList<string>     ExtraDisplayValues => Item.ExtraDisplayValues;

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
