using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using CheckupAddIn.ViewModels;
using Microsoft.Win32;
using System.Windows.Threading;

namespace CheckupAddIn.Views
{
    /// <summary>
    /// Code-behind for the main Checkup window.
    /// Business logic lives in CheckupViewModel; this file handles only UI interactions
    /// that cannot be expressed in XAML bindings: drag-drop, key events, right-click copy,
    /// ComboBox action-item interception, and preset rename dialogs.
    /// </summary>
    public partial class CheckupWindow : Window
    {
        private CheckupViewModel _vm;

        // Source row captured at drag-start; cleared after drop or cancel.
        private RowModel _draggedRow;

        // Guards against a recursive SelectionChanged loop: when an action item ("Add Row" /
        // "Remove Row") is picked, the selection is programmatically reset to the previous item,
        // which would fire SelectionChanged again. This flag suppresses that second firing.
        private bool _suppressSelectionChanged;

        // Field Selector popup resize state.
        private Border _fieldSelectorPopupBorder;

        // Multi-token text box currently in use (for caret position after autocomplete insert).
        private TextBox _activeMultiTokenTextBox;

        // Tracks whether the Logic Dropdown popup was open when the toggle button was last pressed,
        // to avoid re-opening the popup immediately after StaysOpen=False closes it.
        private bool _logicPopupWasOpenBeforeButtonClick;

        // Same guard for the AllowedValues popup arrow button.
        private bool _allowedValuesPopupWasOpenBeforeButtonClick;

        public CheckupWindow()
        {
            InitializeComponent();
        }

        public void SetViewModel(CheckupViewModel vm)
        {
            _vm = vm;
            DataContext = vm;
            ThemeLoader.ApplyTo(this, vm.AppInstance);
            LanguageLoader.ApplyTo(this);
            if (UiStateStore.TryLoadWindowSize(out double w, out double h))
            {
                Width  = w;
                Height = h;
            }
            vm.RequestClose += () => { try { Close(); } catch { } };
            vm.RequestResetWindowSize += () => { Width = 650; Height = 900; };
            vm.RequestOpenCatalogPicker  += OnRequestOpenCatalogPicker;
        }

        private void OnRequestOpenCatalogPicker(RowModel row)
        {
            if (_vm == null || row == null) return;
            if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string groupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
            var found = _vm.UserCapabilityStore?.FindGroup(groupId);
            var group = found?.Group;
            if (group == null) return;

            string catId   = CardEngine.GetPrimaryCatalogId(group);
            var catalog    = catId != null && _vm.UserCatalogStore != null
                ? _vm.UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                : null;
            var tabs       = CardEngine.GetPickerTabs(catalog);
            string catalogId = catalog?.Id ?? "";

            if (CardEngine.HasMultiPickCard(group))
            {
                var items = CardEngine.GetMultiPickItemsForCard(group, catalog);
                if (items.Count == 0) return;
                string priSep = CardEngine.GetMultiPickConfig(group).PrimarySep;
                var preSelected = (row.DisplayValue ?? "")
                    .Split(new[] { priSep }, System.StringSplitOptions.RemoveEmptyEntries);
                var preList = new System.Collections.Generic.List<string>();
                foreach (var t in preSelected)
                {
                    string trimmed = t.Trim();
                    if (trimmed.Length > 0) preList.Add(trimmed);
                }
                var picker = new CatalogPickerWindow(items, tabs, catalogId, _vm.AppInstance,
                    multiSelect: true, preSelectedPriValues: preList) { Owner = this };
                if (picker.ShowDialog() == true)
                    _vm.ApplyMultiPickResult(row, picker.SelectedPriValues);
            }
            else
            {
                var items = CardEngine.GetButtonItemsForCard(group, catalog);
                if (items.Count == 0) return;
                var picker = new CatalogPickerWindow(items, tabs, catalogId, _vm.AppInstance) { Owner = this };
                if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedPriValue))
                    _vm.ApplyLogicPickerResult(row, picker.SelectedPriValue);
            }
        }

        // ══════════════════════════════════════════════
        //  CATALOG BUILDER
        // ══════════════════════════════════════════════

        private void BtnCatalogBuilder_Click(object sender, RoutedEventArgs e)
            => OpenCatalogBuilder();

        private void OpenCatalogBuilder()
        {
            if (_vm?.UserCatalogStore == null) return;
            var vm     = new ViewModels.CatalogBuilderViewModel(_vm.UserCatalogStore, _vm.UserCapabilityStore, _vm.FieldCatalog);
            var window = new CatalogBuilderWindow();
            window.Initialize(vm, _vm.AppInstance);
            window.Owner = this;
            window.ShowDialog();
            _vm.InvalidateFieldCatalog();
        }

        public void SetInventorOwner(IntPtr inventorHwnd)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                helper.Owner = inventorHwnd;
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  PRESET RIGHT-CLICK SAVE
        // ══════════════════════════════════════════════

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            if (sender is not MenuItem mi) return;
            if (!int.TryParse(mi.Tag?.ToString(), out int idx)) return;

            var dlg = new InputDialog(_vm.GetPresetName(idx)) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
                _vm.SavePreset(idx, dlg.InputText);
        }

        // ══════════════════════════════════════════════
        //  WINDOW-LEVEL KEY HANDLING
        // ══════════════════════════════════════════════

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // If the Field Selector popup is open, close it first; don't propagate to window close.
                RowModel fieldSelectorRow = null;
                foreach (var r in _vm?.Rows ?? Enumerable.Empty<RowModel>())
                {
                    if (r.IsFieldSelectorOpen) { fieldSelectorRow = r; break; }
                }
                if (fieldSelectorRow != null)
                {
                    fieldSelectorRow.IsFieldSelectorOpen = false;
                    e.Handled = true;
                    return;
                }

                // If the AllowedValues popup is open, close it and restore keyboard focus to the TextBox.
                RowModel allowedValuesRow = null;
                foreach (var r in _vm?.Rows ?? Enumerable.Empty<RowModel>())
                {
                    if (r.IsAllowedValuesPopupOpen) { allowedValuesRow = r; break; }
                }
                if (allowedValuesRow != null)
                {
                    allowedValuesRow.IsAllowedValuesPopupOpen = false;
                    RestoreFocusToAllowedValuesTextBox(allowedValuesRow);
                    e.Handled = true;
                    return;
                }

                // If a Logic dropdown popup is open, close it and restore keyboard focus to the TextBox.
                RowModel logicPopupRow = null;
                foreach (var r in _vm?.Rows ?? Enumerable.Empty<RowModel>())
                {
                    if (r.IsLogicPopupOpen) { logicPopupRow = r; break; }
                }
                if (logicPopupRow != null)
                {
                    logicPopupRow.IsLogicPopupOpen = false;
                    RestoreFocusToLogicTextBox(logicPopupRow);
                    e.Handled = true;
                    return;
                }

                if (_vm == null || !_vm.Rows.Any(r => r.IsInlineEditing))
                {
                    Close();
                    e.Handled = true;
                    return;
                }
            }
            base.OnPreviewKeyDown(e);
        }

        private void FieldSelectorSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            // The Field Selector popup lives in a separate HwndSource (AllowsTransparency=True),
            // so the window-level OnPreviewKeyDown never fires while the search box has focus.
            // Handle ESC here directly.
            RowModel row = null;
            foreach (var r in _vm?.Rows ?? Enumerable.Empty<RowModel>())
                if (r.IsFieldSelectorOpen) { row = r; break; }
            if (row != null) { row.IsFieldSelectorOpen = false; e.Handled = true; }
        }

        // ══════════════════════════════════════════════
        //  EDIT TEXTBOX KEY HANDLING
        // ══════════════════════════════════════════════

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;

            // ESC closes the multi-token autocomplete popup first, then cancels edit on second press.
            if (e.Key == Key.Escape && row.IsMultiTokenAutoCompleteOpen)
            {
                row.IsMultiTokenAutoCompleteOpen = false;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (row.HasValueChanged && row.IsEditValueValid)
                    _vm?.ApplyFieldEditCommand.Execute(row);
                else if (!row.HasValueChanged)
                    _vm?.CancelFieldEditCommand.Execute(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && row.IsInlineEditing)
            {
                _vm?.CancelFieldEditCommand.Execute(row);
                e.Handled = true;
            }
        }

        private void EditTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_vm == null) return;
            var tb  = sender as TextBox;
            var row = tb?.DataContext as RowModel;
            if (row == null || !row.IsMultiTokenTextEditMode) return;
            _activeMultiTokenTextBox = tb;
            _vm.UpdateMultiTokenAutoComplete(row, tb.Text, tb.CaretIndex);
        }

        private void MultiTokenAutoCompleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var item = (sender as FrameworkElement)?.DataContext as Models.SpeziAutoCompleteItem;
            if (item == null) return;
            var row = _vm.Rows.FirstOrDefault(r => r.IsInlineEditing && r.IsMultiTokenTextEditMode);
            if (row == null) return;
            int caretPos = _activeMultiTokenTextBox != null ? _activeMultiTokenTextBox.CaretIndex : (row.EditText != null ? row.EditText.Length : 0);
            int newCaret = _vm.InsertMultiToken(row, item.Short, caretPos);
            var tb = _activeMultiTokenTextBox;
            if (tb != null)
                Dispatcher.BeginInvoke(
                    new Action(() => { if (tb.IsVisible) { tb.CaretIndex = Math.Min(newCaret, tb.Text.Length); tb.Focus(); } }),
                    DispatcherPriority.Render);
        }

        private void ValueCombo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var cb  = sender as ComboBox;
            var row = cb?.DataContext as RowModel;
            if (row == null) return;

            if (e.Key == Key.Enter)
            {
                if (cb.IsDropDownOpen)
                {
                    cb.IsDropDownOpen = false;
                    e.Handled = true;
                }
                else if (row.HasValueChanged && row.IsEditValueValid)
                {
                    _vm?.ApplyFieldEditCommand.Execute(row);
                    e.Handled = true;
                }
                else if (!row.HasValueChanged)
                {
                    _vm?.CancelFieldEditCommand.Execute(row);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (cb.IsDropDownOpen)
                {
                    cb.IsDropDownOpen = false;
                    e.Handled = true;
                }
                else if (row.IsInlineEditing)
                {
                    _vm?.CancelFieldEditCommand.Execute(row);
                    e.Handled = true;
                }
            }
        }

        // ══════════════════════════════════════════════
        //  ALLOWED VALUES — live-filter popup
        // ══════════════════════════════════════════════

        private void AllowedValuesArrowBtn_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            _allowedValuesPopupWasOpenBeforeButtonClick = row != null && row.IsAllowedValuesPopupOpen;
        }

        private void AllowedValuesArrowBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;
            if (_allowedValuesPopupWasOpenBeforeButtonClick)
            {
                RestoreFocusToAllowedValuesTextBox(row);
                return;
            }
            row.SetAllowedValuesFilter(null);
            row.IsAllowedValuesPopupOpen = true;
            RestoreFocusToAllowedValuesTextBox(row);
        }

        private void AllowedValuesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null || !row.IsComboEditMode) return;
            if (row.IsAllowedValuesPopupOpen)
            {
                // Popup already open — apply live filter.
                row.SetAllowedValuesFilter(row.EditText);
            }
            else if (row.EditText != row.OriginalValue)
            {
                // User actively typed something different from the initial value — open popup with filter (TDD §5.13).
                row.SetAllowedValuesFilter(row.EditText);
                row.IsAllowedValuesPopupOpen = true;
            }
            // else: text matches OriginalValue (edit-mode entry) → popup stays closed.
        }

        private void AllowedValuesItem_Click(object sender, MouseButtonEventArgs e)
        {
            var selected = (sender as FrameworkElement)?.DataContext as string;
            if (selected == null) return;
            RowModel row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null) return;
            row.EditText = selected;
            row.IsAllowedValuesPopupOpen = false;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => _vm?.ApplyFieldEditCommand.Execute(row)));
        }

        private void AllowedValuesTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;

            if (e.Key == Key.Down)
            {
                if (!row.IsAllowedValuesPopupOpen)
                {
                    row.SetAllowedValuesFilter(null);
                    row.IsAllowedValuesPopupOpen = true;
                }
                row.MoveAllowedValuesHighlight(+1);
                ScrollAllowedValuesHighlightedItemIntoView(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && row.IsAllowedValuesPopupOpen)
            {
                row.MoveAllowedValuesHighlight(-1);
                ScrollAllowedValuesHighlightedItemIntoView(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (row.IsAllowedValuesPopupOpen)
                {
                    if (row.HighlightedAllowedValue != null)
                    {
                        row.EditText = row.HighlightedAllowedValue;
                        row.IsAllowedValuesPopupOpen = false;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => _vm?.ApplyFieldEditCommand.Execute(row)));
                    }
                    else
                    {
                        row.IsAllowedValuesPopupOpen = false;
                    }
                    e.Handled = true;
                }
                else if (row.HasValueChanged && row.IsEditValueValid) { _vm?.ApplyFieldEditCommand.Execute(row); e.Handled = true; }
                else if (!row.HasValueChanged) { _vm?.CancelFieldEditCommand.Execute(row); e.Handled = true; }
            }
            else if (e.Key == Key.Escape)
            {
                if (row.IsAllowedValuesPopupOpen) { row.IsAllowedValuesPopupOpen = false; RestoreFocusToAllowedValuesTextBox(row); e.Handled = true; }
                else if (row.IsInlineEditing) { _vm?.CancelFieldEditCommand.Execute(row); e.Handled = true; }
            }
        }

        private void RestoreFocusToAllowedValuesTextBox(RowModel row)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!row.IsInlineEditing) return;
                    TextBox tb = FindDescendantTextBox(this, "AllowedValuesTextBox", row);
                    if (tb != null) tb.Focus();
                }));
        }

        private void ScrollAllowedValuesHighlightedItemIntoView(RowModel row)
        {
            if (row.HighlightedAllowedValue == null) return;
            string target = row.HighlightedAllowedValue;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() =>
                {
                    Popup popup = FindDescendantPopup(this, row);
                    if (popup == null || popup.Child == null) return;
                    ListBox lb = FindDescendantListBox(popup.Child);
                    if (lb != null) lb.ScrollIntoView(target);
                }));
        }

        private static ListBox FindDescendantListBox(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                ListBox lb = child as ListBox;
                if (lb != null) return lb;
                ListBox found = FindDescendantListBox(child);
                if (found != null) return found;
            }
            return null;
        }

        // ══════════════════════════════════════════════
        //  LOGIC DROPDOWN / SEARCH CARD POPUP
        // ══════════════════════════════════════════════

        private CustomPopupPlacement[] ForceLogicPopupBelow(Size popupSize, Size targetSize, Point offset)
            => new[] { new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.None) };

        private void LogicComboDropdownBtn_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            _logicPopupWasOpenBeforeButtonClick = row?.IsLogicPopupOpen == true;
        }

        private void LogicDropdownArrowBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;
            if (_logicPopupWasOpenBeforeButtonClick)
            {
                // StaysOpen=False already closed the popup before this Click fired.
                // Don't reopen — just return keyboard focus to the TextBox.
                RestoreFocusToLogicTextBox(row);
                return;
            }
            if (sender is FrameworkElement fe && fe.Tag is double w && w > 0)
                _vm?.RescaleLogicDropdownColumns(row, w);
            row.IsLogicPopupOpen = true;
            RestoreFocusToLogicTextBox(row);
        }

        private void LogicDropdownItem_Click(object sender, RoutedEventArgs e)
        {
            var itemRow = (sender as FrameworkElement)?.DataContext as LogicDropdownItemRow;
            if (itemRow == null) return;
            var row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null) return;
            row.EditText = itemRow.Item.PriValue;
            row.IsLogicPopupOpen = false;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => _vm?.ApplyFieldEditCommand.Execute(row)));
        }

        private static RowModel FindRowForPopupItem(DependencyObject d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is RowModel r) return r;
                if (d is Popup p)
                {
                    if (p.PlacementTarget is FrameworkElement pt && pt.DataContext is RowModel r2) return r2;
                    d = p.PlacementTarget;
                    continue;
                }
                d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void LogicDropdownTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;

            if (e.Key == Key.Down)
            {
                if (!row.IsLogicPopupOpen)
                {
                    FrameworkElement fe2 = sender as FrameworkElement;
                    object tagVal = fe2 != null ? fe2.Tag : null;
                    if (tagVal is double wd && wd > 0) _vm?.RescaleLogicDropdownColumns(row, wd);
                    row.IsLogicPopupOpen = true;
                }
                row.MoveLogicHighlight(+1);
                ScrollLogicHighlightedItemIntoView(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && row.IsLogicPopupOpen)
            {
                row.MoveLogicHighlight(-1);
                ScrollLogicHighlightedItemIntoView(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (row.IsLogicPopupOpen)
                {
                    if (row.HighlightedLogicItem != null)
                    {
                        row.EditText = row.HighlightedLogicItem.Item.PriValue;
                        row.IsLogicPopupOpen = false;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => _vm?.ApplyFieldEditCommand.Execute(row)));
                    }
                    else
                    {
                        row.IsLogicPopupOpen = false;
                    }
                    e.Handled = true;
                }
                else if (row.HasValueChanged && row.IsEditValueValid) { _vm?.ApplyFieldEditCommand.Execute(row); e.Handled = true; }
                else if (!row.HasValueChanged) { _vm?.CancelFieldEditCommand.Execute(row); e.Handled = true; }
            }
            else if (e.Key == Key.Escape)
            {
                if (row.IsLogicPopupOpen) { row.IsLogicPopupOpen = false; RestoreFocusToLogicTextBox(row); e.Handled = true; }
                else if (row.IsInlineEditing) { _vm?.CancelFieldEditCommand.Execute(row); e.Handled = true; }
            }
        }

        private void LogicSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            LogicDropdownTextBox_PreviewKeyDown(sender, e);
        }

        private void LogicSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null || !row.IsLogicSearchMode) return;
            bool hasItems = row.LogicDropdownRowsView != null && !row.LogicDropdownRowsView.IsEmpty;
            if (!row.IsLogicPopupOpen)
            {
                // Auto-open only when user actively types something different from the initial edit value.
                // This prevents the popup from auto-opening when entering edit mode (TDD §5.13).
                if (!hasItems || row.EditText == row.OriginalValue) return;
                if (sender is FrameworkElement fe && fe.Tag is double w && w > 0)
                    _vm?.RescaleLogicDropdownColumns(row, w);
                row.IsLogicPopupOpen = true;
            }
            else
            {
                // Popup already open: close only if no matches remain.
                if (!hasItems) row.IsLogicPopupOpen = false;
            }
        }

        private void RestoreFocusToLogicTextBox(RowModel row)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!row.IsInlineEditing) return;
                    TextBox tb = FindDescendantTextBox(this, "LogicDropdownTextBox", row);
                    if (tb != null) tb.Focus();
                }));
        }

        private void ScrollLogicHighlightedItemIntoView(RowModel row)
        {
            if (row.HighlightedLogicItem == null) return;
            LogicDropdownItemRow target = row.HighlightedLogicItem;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() =>
                {
                    Popup popup = FindDescendantPopup(this, row);
                    if (popup == null || popup.Child == null) return;
                    Button btn = FindDescendantWithDataContext(popup.Child, target);
                    if (btn != null) btn.BringIntoView();
                }));
        }

        private static Popup FindDescendantPopup(DependencyObject parent, object dataContext)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                Popup p = child as Popup;
                if (p != null && p.IsOpen && p.DataContext == dataContext) return p;
                Popup found = FindDescendantPopup(child, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private static Button FindDescendantWithDataContext(DependencyObject parent, object dataContext)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                Button b = child as Button;
                if (b != null && b.DataContext == dataContext) return b;
                Button found = FindDescendantWithDataContext(child, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private static TextBox FindDescendantTextBox(DependencyObject parent, string name, object dataContext)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                TextBox tb = child as TextBox;
                if (tb != null && tb.Name == name && tb.DataContext == dataContext)
                    return tb;
                TextBox found = FindDescendantTextBox(child, name, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private void LogicDropdownColumnThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var col = (sender as FrameworkElement)?.DataContext as LogicDropdownColumn;
            if (col == null) return;
            col.Width = col.Width + e.HorizontalChange;
            var row = FindRowForPopupItem(sender as DependencyObject);
            if (row != null) _vm?.SaveLogicDropdownColumnWidths(row);
        }

        private void LogicDropdownHeightThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null) return;
            row.LogicDropdownPopupHeight = row.LogicDropdownPopupHeight + e.VerticalChange;
            if (!string.IsNullOrEmpty(row.LogicDropdownContextKey))
                UiStateStore.SaveLogicDropdownSize(row.LogicDropdownContextKey, 0, row.LogicDropdownPopupHeight);
        }

        private void LogicDropdownColumnThumb_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var col = (sender as FrameworkElement)?.DataContext as LogicDropdownColumn;
            if (col == null) return;
            var row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null || row.LogicDropdownRows == null) return;
            double maxWidth = CheckupViewModel.MeasureLogicDropdownText(col.Label, bold: true);
            foreach (var ir in row.LogicDropdownRows)
                foreach (var cell in ir.Cells)
                    if (ReferenceEquals(cell.Column, col))
                    {
                        double w = CheckupViewModel.MeasureLogicDropdownText(cell.Value, bold: false);
                        if (w > maxWidth) maxWidth = w;
                    }
            col.Width = maxWidth + 20;
            _vm?.SaveLogicDropdownColumnWidths(row);
            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  EDIT CONTROL FOCUS MANAGEMENT
        // ══════════════════════════════════════════════

        private void EditControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue && sender is FrameworkElement fe)
                fe.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => fe.Focus()));
        }

        private void EditControl_LostFocus(object sender, RoutedEventArgs e)
        {
            // If a ComboBox dropdown is still open, focus is moving into the popup — not away.
            if (sender is ComboBox cb && cb.IsDropDownOpen) return;

            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null || !row.IsInlineEditing) return;

            // Popup items are non-focusable so they can't steal focus, but guard defensively.
            if (row.IsMultiTokenAutoCompleteOpen || row.IsLogicPopupOpen || row.IsAllowedValuesPopupOpen) return;

            // Defer so that a concurrent Apply/Cancel button click can process first.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!row.IsInlineEditing) return;
                    if (row.IsMultiTokenAutoCompleteOpen || row.IsLogicPopupOpen || row.IsAllowedValuesPopupOpen) return;

                    // Focus moved to another control in the same row — stay in edit mode.
                    var focused = Keyboard.FocusedElement as FrameworkElement;
                    if (focused?.DataContext == row) return;

                    _vm?.CancelFieldEditCommand.Execute(row);
                }));
        }

        // ══════════════════════════════════════════════
        //  COMBOBOX SELECTION CHANGED
        // ══════════════════════════════════════════════

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (e.AddedItems.Count == 0) return;

            var cb  = sender as ComboBox;
            var row = cb?.DataContext as RowModel;
            if (row == null) return;

            if (e.AddedItems[0] is FieldItem fi && fi.IsActionItem)
            {
                _suppressSelectionChanged = true;
                cb.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
                _suppressSelectionChanged = false;

                if (fi.Key == "__ADD_ROW__")
                    _vm?.AddRowCommand.Execute(row);
                else if (fi.Key == "__REMOVE_ROW__")
                    _vm?.RemoveRowCommand.Execute(row);
                return;
            }

            if (e.RemovedItems.Count > 0 && e.AddedItems[0] == e.RemovedItems[0]) return;

            _vm?.FieldSelectionChangedCommand.Execute(row);
        }

        // ══════════════════════════════════════════════
        //  RIGHT-CLICK COPY TO CLIPBOARD
        // ══════════════════════════════════════════════

        private void Value_RightClick(object sender, MouseButtonEventArgs e)
        {
            string text = sender is TextBlock tb ? tb.Text
                        : sender is TextBox txb   ? txb.Text
                        : (sender as FrameworkElement)?.DataContext is RowModel rm ? rm.DisplayValue
                        : "";

            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                    if (_vm != null) _vm.StatusMessage = $"Copied: {text}";
                }
                catch { }
            }

            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  DRAG AND DROP ROW REORDER
        // ══════════════════════════════════════════════

        private void DragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedRow != null) return;

            var element = sender as FrameworkElement;
            var row = element?.DataContext as RowModel;
            if (row == null) return;

            row.IsDragging = true;
            _draggedRow = row;
            var data = new DataObject("CheckupRow", row);
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
            row.IsDragging = false;
            _draggedRow = null;

            if (_vm != null)
                foreach (var r in _vm.Rows) r.IsDragOver = false;
        }

        private void Row_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CheckupRow")) return;
            var element = sender as FrameworkElement;
            var targetRow = element?.Tag as RowModel ?? element?.DataContext as RowModel;
            if (targetRow != null && targetRow != _draggedRow)
                targetRow.IsDragOver = true;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void Row_DragLeave(object sender, DragEventArgs e)
        {
            var element = sender as FrameworkElement;
            var targetRow = element?.Tag as RowModel ?? element?.DataContext as RowModel;
            if (targetRow != null) targetRow.IsDragOver = false;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CheckupRow") || _vm == null) return;

            var sourceRow = e.Data.GetData("CheckupRow") as RowModel;
            var element = sender as FrameworkElement;
            var targetRow = element?.Tag as RowModel ?? element?.DataContext as RowModel;

            if (sourceRow == null || targetRow == null || sourceRow == targetRow) return;

            targetRow.IsDragOver = false;

            int fromIdx = _vm.Rows.IndexOf(sourceRow);
            int toIdx   = _vm.Rows.IndexOf(targetRow);
            if (fromIdx >= 0 && toIdx >= 0)
                _vm.OnRowDragDropCompleted(fromIdx, toIdx);

            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  WINDOW MIN-WIDTH — lock to bottom-bar content so preset buttons never overlap.
        // ══════════════════════════════════════════════

        private void BottomBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement fe || !(fe.ActualWidth > 0)) return;
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double needed = fe.DesiredSize.Width + 24
                + System.Windows.SystemParameters.ResizeFrameVerticalBorderWidth * 2;
            if (needed > MinWidth)
                MinWidth = needed;
        }

        // ══════════════════════════════════════════════
        //  FIELD SELECTOR POPUP (F1)
        // ══════════════════════════════════════════════

        private double _fieldSelectorDropdownHeight = 280;

        private PinnedFieldEntry _draggedFavoritenEntry;

        private void FieldSelectorBtn_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (!(btn?.DataContext is RowModel row)) return;
            // Close any other open field selector first
            if (_vm != null)
                foreach (var r in _vm.Rows)
                    if (r != row && r.IsFieldSelectorOpen) r.IsFieldSelectorOpen = false;
            row.IsFieldSelectorOpen = !row.IsFieldSelectorOpen;
        }

        private void FieldSelectorPopup_Opened(object sender, EventArgs e)
        {
            var popup = sender as System.Windows.Controls.Primitives.Popup;
            if (popup == null) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _fieldSelectorPopupBorder = FindVisualChild<Border>(popup.Child);
                if (_fieldSelectorPopupBorder != null && _fieldSelectorDropdownHeight > 0)
                    _fieldSelectorPopupBorder.Height = _fieldSelectorDropdownHeight;
                FindVisualChild<System.Windows.Controls.TextBox>(popup.Child)?.Focus();
            }));
        }

        private void FieldSelectorPopup_Closed(object sender, EventArgs e)
        {
            if (_fieldSelectorPopupBorder != null && _fieldSelectorPopupBorder.ActualHeight > 0)
            {
                _fieldSelectorDropdownHeight = _fieldSelectorPopupBorder.ActualHeight;
                UiStateStore.SaveFieldSelectorDropdownSize(0, _fieldSelectorDropdownHeight);
            }
            _fieldSelectorPopupBorder = null;
            _vm?.OnFieldSelectorClosed();
        }

        private void FieldSelectorItem_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var fieldItem = (sender as System.Windows.FrameworkElement)?.DataContext as FieldItem;
            if (fieldItem == null) return;
            var row = _vm.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
            if (row == null) return;
            row.IsFieldSelectorOpen = false;
            if (fieldItem.IsActionItem)
            {
                if (fieldItem.Key == "__ADD_ROW__")         _vm.AddRowCommand.Execute(row);
                else if (fieldItem.Key == "__REMOVE_ROW__") _vm.RemoveRowCommand.Execute(row);
                return;
            }
            row.SelectedField = fieldItem;
            _vm.FieldSelectionChangedCommand.Execute(row);
        }

        private void FieldSelectorPinned_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var entry = (sender as System.Windows.FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            var row = _vm.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
            if (row == null) return;
            row.IsFieldSelectorOpen = false;
            row.SelectedField = entry.Item;
            _vm.FieldSelectionChangedCommand.Execute(row);
        }

        private void FieldSelectorItem_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            string key = null;
            var dc = (sender as System.Windows.FrameworkElement)?.DataContext;
            if (dc is PinnedFieldEntry pfe)
                key = pfe.Key;
            else if (dc is FieldItem fi)
                key = fi.Key;
            if (!string.IsNullOrEmpty(key))
                _vm.ToggleFieldPinCommand.Execute(key);
            e.Handled = true;
        }

        private void FavoritenDragHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedFavoritenEntry != null) return;
            var entry = (sender as System.Windows.FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _draggedFavoritenEntry = entry;
            System.Windows.DragDrop.DoDragDrop(
                sender as System.Windows.DependencyObject,
                new System.Windows.DataObject("FavoritenEntry", entry),
                System.Windows.DragDropEffects.Move);
            _draggedFavoritenEntry = null;
        }

        private void FavoritenItem_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("FavoritenEntry")
                ? System.Windows.DragDropEffects.Move
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void FavoritenItem_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("FavoritenEntry") || _vm == null) return;
            var sourceEntry = e.Data.GetData("FavoritenEntry") as PinnedFieldEntry;
            var targetEntry = (sender as System.Windows.FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (sourceEntry == null || targetEntry == null || sourceEntry.Key == targetEntry.Key) return;
            _vm.ReorderPinnedField(sourceEntry.Key, targetEntry.Key);
            e.Handled = true;
        }

        private void FieldSelectorGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            var gvm = (sender as System.Windows.FrameworkElement)?.DataContext as FieldSelectorGroupVm;
            gvm?.ToggleCollapseCommand?.Execute(null);
        }

        private void FieldSelectorResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_fieldSelectorPopupBorder == null) return;
            double newH = _fieldSelectorPopupBorder.ActualHeight + e.VerticalChange;
            _fieldSelectorDropdownHeight = Math.Max(80, newH);
            _fieldSelectorPopupBorder.MaxHeight = double.PositiveInfinity;
            _fieldSelectorPopupBorder.Height = _fieldSelectorDropdownHeight;
        }

        private static T FindVisualChild<T>(System.Windows.DependencyObject parent)
            where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        // ══════════════════════════════════════════════
        //  CLICK-OUTSIDE TO CANCEL INLINE EDIT
        // ══════════════════════════════════════════════

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            RowModel editingRow = null;
            if (_vm != null)
                foreach (var r in _vm.Rows) { if (r.IsInlineEditing) { editingRow = r; break; } }
            if (editingRow != null)
            {
                // If a popup is open, let StaysOpen=False handle it — don't cancel edit.
                if (editingRow.IsAllowedValuesPopupOpen || editingRow.IsLogicPopupOpen ||
                    editingRow.IsMultiTokenAutoCompleteOpen || editingRow.IsFieldSelectorOpen)
                {
                    base.OnPreviewMouseLeftButtonDown(e);
                    return;
                }
                if (!IsWithinRowContext(e.OriginalSource as DependencyObject, editingRow))
                    _vm?.CancelFieldEditCommand.Execute(editingRow);
            }
            base.OnPreviewMouseLeftButtonDown(e);
        }

        private static bool IsWithinRowContext(DependencyObject target, RowModel row)
        {
            DependencyObject d = target;
            while (d != null)
            {
                FrameworkElement fe = d as FrameworkElement;
                if (fe != null && fe.DataContext == row) return true;
                DependencyObject parent = LogicalTreeHelper.GetParent(d);
                if (parent == null) parent = VisualTreeHelper.GetParent(d);
                d = parent;
            }
            return false;
        }

        protected override void OnContentRendered(System.EventArgs e)
        {
            base.OnContentRendered(e);
            _vm?.CheckAndShowDemoWarning(this);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            UiStateStore.SaveWindowSize(Width, Height);
            _vm?.UnsubscribeFromInventorEvents();
            DataContext = null;
            base.OnClosing(e);
        }

        // ══════════════════════════════════════════════
        //  EXPORT / IMPORT PRESETS
        // ══════════════════════════════════════════════

        private void ExportPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var mi = sender as System.Windows.Controls.MenuItem;
            if (mi == null || !int.TryParse(mi.Tag?.ToString(), out int idx)) return;
            var dlg = new SaveFileDialog
            {
                Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() == true)
                _vm.ExportPreset(idx, dlg.FileName);
        }

        private void ExportAllPresets_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var dlg = new SaveFileDialog
            {
                Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() == true)
                _vm.ExportAllPresets(dlg.FileName);
        }

        private void ImportPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var mi = sender as System.Windows.Controls.MenuItem;
            if (mi == null || !int.TryParse(mi.Tag?.ToString(), out int idx)) return;
            var dlg = new OpenFileDialog
            {
                Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() != true) return;

            var library = _vm.ReadLibraryPresets(dlg.FileName);
            if (library == null) return;

            var picker = new PresetPickerDialog(library) { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedPreset != null)
                _vm.ImportPresetIntoSlot(idx, picker.SelectedPreset);
        }
    }
}
