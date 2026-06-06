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

        // Captures whether the Logic popup was already open at PreviewMouseDown time on the arrow button,
        // so LogicDropdownArrowBtn_Click can skip re-opening when StaysOpen=False already closed it.
        private bool _logicPopupWasOpenBeforeButtonClick;

        // Same guard for the AllowedValues popup arrow button.
        private bool _allowedValuesPopupWasOpenBeforeButtonClick;

        // Field Selector popup resize state — cached border set in Opened, cleared in Closed.
        private Border _fieldSelectorPopupBorder;
        private double _fieldSelectorDropdownHeight;

        // Favoriten zone drag state.
        private PinnedFieldEntry _draggedFavoritenEntry;

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
            if (UiStateStore.TryLoadFieldSelectorDropdownSize(out _, out double dh))
                _fieldSelectorDropdownHeight = dh;
            vm.RequestClose              += () => { try { Close(); } catch { } };
            vm.RequestResetWindowSize    += () => { Width = 650; Height = 900; };
        }

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

        private void CatalogPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null || !row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
            var found = _vm.UserCapabilityStore?.FindGroup(groupId);
            var group = found?.Group;
            if (group == null) return;

            string catId  = CardEngine.GetPrimaryCatalogId(group);
            var catalog   = catId != null ? _vm.UserCatalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
            var tabs      = CardEngine.GetPickerTabs(catalog);
            string catalogId = catalog?.Id ?? "";

            if (CardEngine.HasMultiPickCard(group))
            {
                // Multi-select mode: get current field value to pre-check existing tokens
                var items = CardEngine.GetMultiPickItemsForCard(group, catalog);
                if (items.Count == 0) return;
                var (priSep, _, _, _) = CardEngine.GetMultiPickConfig(group);
                var preSelected = (row.DisplayValue ?? "")
                    .Split(new[] { priSep }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .ToList();
                var picker = new CatalogPickerWindow(items, tabs, catalogId, _vm.AppInstance,
                    multiSelect: true, preSelectedPriValues: preSelected) { Owner = this };
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
                var fieldSelectorRow = _vm?.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
                if (fieldSelectorRow != null)
                {
                    fieldSelectorRow.IsFieldSelectorOpen = false;
                    e.Handled = true;
                    return;
                }

                // If the AllowedValues popup is open, close it and restore keyboard focus to the TextBox.
                var allowedValuesRow = _vm?.Rows.FirstOrDefault(r => r.IsAllowedValuesPopupOpen);
                if (allowedValuesRow != null)
                {
                    allowedValuesRow.IsAllowedValuesPopupOpen = false;
                    RestoreFocusToAllowedValuesTextBox(allowedValuesRow);
                    e.Handled = true;
                    return;
                }

                // If a Logic dropdown popup is open, close it and restore keyboard focus to the TextBox.
                var logicPopupRow = _vm?.Rows.FirstOrDefault(r => r.IsLogicPopupOpen);
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
            var row = _vm?.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
            if (row != null) { row.IsFieldSelectorOpen = false; e.Handled = true; }
        }

        // ══════════════════════════════════════════════
        //  EDIT TEXTBOX KEY HANDLING
        // ══════════════════════════════════════════════

        private TextBox _activeMultiTokenTextBox;

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
            int caretPos = _activeMultiTokenTextBox?.CaretIndex ?? (row.EditText?.Length ?? 0);
            int newCaret = _vm.InsertMultiToken(row, item.Short, caretPos);
            var tb = _activeMultiTokenTextBox;
            if (tb != null)
                Dispatcher.BeginInvoke(
                    new Action(() => { if (tb.IsVisible) { tb.CaretIndex = Math.Min(newCaret, tb.Text.Length); tb.Focus(); } }),
                    System.Windows.Threading.DispatcherPriority.Render);
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
            _allowedValuesPopupWasOpenBeforeButtonClick = row?.IsAllowedValuesPopupOpen == true;
        }

        private void AllowedValuesArrowBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;
            if (_allowedValuesPopupWasOpenBeforeButtonClick)
            {
                // StaysOpen=False already closed the popup before this Click fired.
                // Don't reopen — just return keyboard focus to the TextBox.
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
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _vm?.ApplyFieldEditCommand.Execute(row);
            }));
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
                        Dispatcher.BeginInvoke(DispatcherPriority.Input,
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
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!row.IsInlineEditing) return;
                var tb = FindDescendantTextBox(this, "AllowedValuesTextBox", row);
                tb?.Focus();
            }));
        }

        private void ScrollAllowedValuesHighlightedItemIntoView(RowModel row)
        {
            if (row.HighlightedAllowedValue == null) return;
            string target = row.HighlightedAllowedValue;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var popup = FindDescendant<Popup>(this, p => p.IsOpen && p.DataContext == row);
                if (popup?.Child == null) return;
                var listBox = FindDescendant<ListBox>(popup.Child, _ => true);
                listBox?.ScrollIntoView(target);
            }));
        }

        // ══════════════════════════════════════════════
        //  LOGIC DROPDOWN — multi-column popup (U1)
        // ══════════════════════════════════════════════

        /// <summary>Custom Popup placement: always opens immediately below the anchor row.
        /// Overrides WPF's auto-flip behavior — the row that owns the popup must stay visible.</summary>
        private CustomPopupPlacement[] ForceLogicPopupBelow(Size popupSize, Size targetSize, Point offset)
            => new[] { new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.None) };

        private void LogicDropdownArrowBtn_PreviewMouseDown(object sender, MouseButtonEventArgs e)
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

        private void LogicDropdownTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null) return;

            if (e.Key == Key.Down)
            {
                if (!row.IsLogicPopupOpen)
                {
                    if (sender is FrameworkElement fe && fe.Tag is double w && w > 0)
                        _vm?.RescaleLogicDropdownColumns(row, w);
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
                        Dispatcher.BeginInvoke(DispatcherPriority.Input,
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
            // Same key handling as the Dropdown variant.
            LogicDropdownTextBox_PreviewKeyDown(sender, e);
        }

        private void LogicSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as RowModel;
            if (row == null || !row.IsLogicSearchMode) return;
            // EditText binding already invoked ApplySearchFilter via RowModel; just toggle popup.
            bool hasItems = row.LogicDropdownRowsView?.IsEmpty == false;
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

        private void LogicDropdownItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LogicDropdownItemRow itemRow) return;
            // Find the RowModel via PlacementTarget walk: the Button's logical parents lead through
            // the Popup back to the LogicDropdownPanel whose DataContext is the row.
            RowModel row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null) return;
            row.EditText = itemRow.Item.PriValue;
            row.IsLogicPopupOpen = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _vm?.ApplyFieldEditCommand.Execute(row);
            }));
        }

        private static RowModel FindRowForPopupItem(DependencyObject d)
        {
            // The popup is in a separate visual tree; walk up via Popup.PlacementTarget.
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

        private void RestoreFocusToLogicTextBox(RowModel row)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!row.IsInlineEditing) return;
                var tb = FindDescendantTextBox(this, "LogicDropdownTextBox", row);
                tb?.Focus();
            }));
        }

        private void ScrollLogicHighlightedItemIntoView(RowModel row)
        {
            if (row.HighlightedLogicItem == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var popup = FindDescendant<Popup>(this, p => p.IsOpen && p.DataContext == row);
                if (popup?.Child == null) return;
                var btn = FindDescendantWithContext<Button>(popup.Child, row.HighlightedLogicItem);
                btn?.BringIntoView();
            }));
        }

        private static T FindDescendant<T>(DependencyObject parent, System.Func<T, bool> predicate)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && predicate(t)) return t;
                var found = FindDescendant(child, predicate);
                if (found != null) return found;
            }
            return null;
        }

        private static T FindDescendantWithContext<T>(DependencyObject parent, object dataContext)
            where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.DataContext == dataContext) return t;
                var found = FindDescendantWithContext<T>(child, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private static TextBox FindDescendantTextBox(DependencyObject parent, string name, object dataContext)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb && tb.Name == name && tb.DataContext == dataContext)
                    return tb;
                var found = FindDescendantTextBox(child, name, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private void LogicDropdownColumnThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LogicDropdownColumn col) return;
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
            if ((sender as FrameworkElement)?.DataContext is not LogicDropdownColumn col) return;
            var row = FindRowForPopupItem(sender as DependencyObject);
            if (row == null || row.LogicDropdownRows == null) return;

            // Measure widest value in this column among visible rows (TDD §5.13 double-click auto-fit).
            double maxWidth = CheckupViewModel.MeasureLogicDropdownText(col.Label, bold: true);
            foreach (var ir in row.LogicDropdownRows)
            {
                foreach (var cell in ir.Cells)
                {
                    if (!ReferenceEquals(cell.Column, col)) continue;
                    double w = CheckupViewModel.MeasureLogicDropdownText(cell.Value, bold: false);
                    if (w > maxWidth) maxWidth = w;
                }
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
                    if (row.IsMultiTokenAutoCompleteOpen) return;
                    if (row.IsAllowedValuesPopupOpen) return;
                    if (row.IsLogicPopupOpen) return;

                    // Focus moved to another control in the same row — stay in edit mode.
                    var focused = Keyboard.FocusedElement as FrameworkElement;
                    if (focused?.DataContext == row) return;

                    _vm?.CancelFieldEditCommand.Execute(row);
                }));
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
        //  Called each time the bottom bar re-measures (language changes, first render, etc.).
        // ══════════════════════════════════════════════

        private void BottomBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement fe || !(fe.ActualWidth > 0)) return;
            // Measure with unconstrained width so DesiredSize reflects the bar's natural content
            // width, not the window-constrained ActualWidth.  Using ActualWidth here would create
            // a feedback loop: window shrinks → bar shrinks → MinWidth shrinks → window shrinks…
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double needed = fe.DesiredSize.Width + 24
                + System.Windows.SystemParameters.ResizeFrameVerticalBorderWidth * 2;
            if (needed > MinWidth)
                MinWidth = needed;
        }

        // ══════════════════════════════════════════════
        //  FIELD SELECTOR — custom Button+Popup with sticky top zone
        // ══════════════════════════════════════════════

        private void FieldSelectorBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RowModel row)
                row.IsFieldSelectorOpen = !row.IsFieldSelectorOpen;
        }

        private void FieldSelectorPopup_Opened(object sender, EventArgs e)
        {
            if (sender is not Popup popup) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _fieldSelectorPopupBorder = FindVisualChild<Border>(popup.Child);
                if (_fieldSelectorPopupBorder == null) return;
                // Width is auto-sized to content (longest entry) — never set a fixed width.
                if (_fieldSelectorDropdownHeight > 0) _fieldSelectorPopupBorder.Height = _fieldSelectorDropdownHeight;
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
            var fieldItem = (sender as FrameworkElement)?.DataContext as FieldItem;
            if (fieldItem == null) return;

            // Only one row can have its popup open at a time
            var row = _vm.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
            if (row == null) return;

            row.IsFieldSelectorOpen = false;

            if (fieldItem.IsActionItem)
            {
                if (fieldItem.Key == "__ADD_ROW__")    _vm.AddRowCommand.Execute(row);
                else if (fieldItem.Key == "__REMOVE_ROW__") _vm.RemoveRowCommand.Execute(row);
                return;
            }

            row.SelectedField = fieldItem;
            _vm.FieldSelectionChangedCommand.Execute(row);
        }

        private void FieldSelectorPinned_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;

            var row = _vm.Rows.FirstOrDefault(r => r.IsFieldSelectorOpen);
            if (row == null) return;

            row.IsFieldSelectorOpen = false;
            row.SelectedField = entry.Item;
            _vm.FieldSelectionChangedCommand.Execute(row);
        }

        private void FieldSelectorItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            string key = null;
            var dc = (sender as FrameworkElement)?.DataContext;
            if (dc is PinnedFieldEntry pfe)
                key = pfe.Key;
            else if (dc is FieldItem fi)
                key = fi.Key;
            if (!string.IsNullOrEmpty(key))
                _vm.ToggleFieldPinCommand.Execute(key);
            e.Handled = true;
        }

        private void FieldSelectorGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            var gvm = (sender as FrameworkElement)?.DataContext as FieldSelectorGroupVm;
            gvm?.ToggleCollapseCommand.Execute(null);
        }

        private void FavoritenDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedFavoritenEntry != null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _draggedFavoritenEntry = entry;
            DragDrop.DoDragDrop(sender as FrameworkElement, new DataObject("FavoritenEntry", entry), DragDropEffects.Move);
            _draggedFavoritenEntry = null;
        }

        private void FavoritenItem_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("FavoritenEntry") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void FavoritenItem_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("FavoritenEntry") || _vm == null) return;
            var sourceEntry = e.Data.GetData("FavoritenEntry") as PinnedFieldEntry;
            var targetEntry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (sourceEntry == null || targetEntry == null || sourceEntry.Key == targetEntry.Key) return;
            _vm.ReorderPinnedField(sourceEntry.Key, targetEntry.Key);
            e.Handled = true;
        }

        private void FieldSelectorResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_fieldSelectorPopupBorder == null) return;
            double newH = _fieldSelectorPopupBorder.ActualHeight + e.VerticalChange;
            _fieldSelectorPopupBorder.Height = Math.Max(80, newH);
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
            var editingRow = _vm?.Rows.FirstOrDefault(r => r.IsInlineEditing);
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
            var d = target;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext == row) return true;
                d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        protected override void OnContentRendered(EventArgs e)
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
            if (sender is not MenuItem mi || !int.TryParse(mi.Tag?.ToString(), out int idx)) return;
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
            if (sender is not MenuItem mi || !int.TryParse(mi.Tag?.ToString(), out int idx)) return;
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
