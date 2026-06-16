using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using CheckupAddIn.ViewModels;
using Microsoft.Win32;

namespace CheckupAddIn.Views
{
    // Holds per-column header data for the ColHeaderTemplate binding.
    // Letter (A/B/C...) reflects display order; SortArrow reflects current sort state;
    // Role badge shows the column's semantic role with optional index (e.g. "SRT1" / "SRT2").
    public class ColumnHeaderData : INotifyPropertyChanged
    {
        private string            _letter;
        private ListSortDirection? _sortDir;
        private ColumnRole        _role;
        private int               _roleIndex     = 1;
        private int               _sameTypeCount = 1;

        public string Label { get; }

        public ColumnHeaderData(string letter, string label,
            ColumnRole role = ColumnRole.None, int roleIndex = 1, int sameTypeCount = 1)
        {
            _letter        = letter;
            Label          = label;
            _role          = role;
            _roleIndex     = roleIndex;
            _sameTypeCount = sameTypeCount;
        }

        public string Letter
        {
            get => _letter;
            set { if (_letter == value) return; _letter = value; OnPropertyChanged(); }
        }

        public ListSortDirection? SortDir
        {
            get => _sortDir;
            set { _sortDir = value; OnPropertyChanged(nameof(SortArrow)); }
        }

        // Always shows a caret so the clickable sort zone is discoverable: ⇅ when unsorted,
        // ▲/▼ when this column is the active sort. (Click handled by ColumnHeader_Click caret zone.)
        public string SortArrow =>
            _sortDir == ListSortDirection.Ascending  ? "▲" :
            _sortDir == ListSortDirection.Descending ? "▼" : "⇅";

        public ColumnRole Role
        {
            get => _role;
            set
            {
                if (_role == value) return;
                _role = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RoleBadge));
                OnPropertyChanged(nameof(RoleBrush));
                OnPropertyChanged(nameof(HasRole));
            }
        }

        public int RoleIndex
        {
            get => _roleIndex;
            set { if (_roleIndex == value) return; _roleIndex = value; OnPropertyChanged(nameof(RoleBadge)); }
        }

        public int SameTypeCount
        {
            get => _sameTypeCount;
            set { if (_sameTypeCount == value) return; _sameTypeCount = value; OnPropertyChanged(nameof(RoleBadge)); }
        }

        public bool HasRole => _role != ColumnRole.None;

        // Returns 3-letter abbreviation for the role type, empty for None.
        private static string RoleAbbr(ColumnRole role) => role switch
        {
            ColumnRole.PrimaryDisplay   => "PRI",
            ColumnRole.SecondaryDisplay => "SEC",
            ColumnRole.TabId            => "TAB",
            ColumnRole.GroupId          => "GRP",
            ColumnRole.SortKey          => "SRT",
            ColumnRole.GroupSortKey     => "GST",
            ColumnRole.TabSortKey       => "TST",
            ColumnRole.Auxiliary        => "AUX",
            _                           => "",
        };

        // Shows plain abbreviation when only one column has this role type;
        // appends the 1-based index when multiple columns share the same type.
        public string RoleBadge
        {
            get
            {
                string abbr = RoleAbbr(_role);
                if (string.IsNullOrEmpty(abbr)) return "";
                return _sameTypeCount > 1 ? $"{abbr}{_roleIndex}" : abbr;
            }
        }

        public Brush RoleBrush => _roleBrushes.TryGetValue(_role, out var b) ? b : Brushes.Transparent;

        private static readonly Dictionary<ColumnRole, Brush> _roleBrushes = new Dictionary<ColumnRole, Brush>()
        {
            [ColumnRole.PrimaryDisplay]   = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)), // Blue
            [ColumnRole.SecondaryDisplay] = new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88)), // Teal
            [ColumnRole.TabId]            = new SolidColorBrush(Color.FromRgb(0x9C, 0x27, 0xB0)), // Purple
            [ColumnRole.GroupId]          = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63)), // Pink
            [ColumnRole.SortKey]          = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), // Orange   — item sort
            [ColumnRole.GroupSortKey]     = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), // Amber    — group sort
            [ColumnRole.TabSortKey]       = new SolidColorBrush(Color.FromRgb(0x8B, 0xC3, 0x4A)), // Lime     — tab sort
            [ColumnRole.Auxiliary]        = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)), // Blue-gray
        };

        public override string ToString() => Label ?? "";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class CatalogBuilderWindow : Window
    {
        private CatalogBuilderViewModel _vm;

        // BL panel right-extension: tracks the window width before the panel was opened so we can restore it.
        private double _widthBeforeBLOpen = double.NaN;

        // Group drag & drop state
        private CardGroupVm _draggedGroup;

        // Card drag & drop state
        private CardRowVm   _draggedCard;
        private CardGroupVm _draggedCardGroup;

        // Tracks which column/row header is highlighted for the selected-cell indicator
        private DataGridColumnHeader _highlightedColumnHeader;
        private DataGridRowHeader    _highlightedRowHeader;

        // Prevents ColumnDisplayIndexChanged from firing during RebuildDataGridColumns
        private bool _rebuildingColumns;

        // Tracks the catalog whose columns are currently displayed so widths can be saved
        // before tearing down for a different catalog or on window close
        private string _currentColumnCatalogId;

        // Context menu shown on cell right-click — kept in a field rather than assigned to
        // EntryGrid.ContextMenu because assigning it to the DataGrid registers WPF mouse
        // listeners that interfere with the column-header resize Thumb in Inventor's WPF host.
        private ContextMenu _contextMenu;
        private MenuItem    _ctxMoveRowUp;
        private MenuItem    _ctxMoveRowDown;
        private int         _rightClickedRowIndex = -1;

        // Column that was right-clicked last; used by the column-header context menu handlers.
        private CatalogColumn _rightClickedColumn;

        // Find bar state
        private readonly List<(int Row, int Col)> _findMatches = new List<(int, int)>();
        private int _findIndex;

        // Keyed by CatalogColumn.Key so we can update SortDir without rebuilding all columns
        private readonly Dictionary<string, ColumnHeaderData> _headerDataByKey = new Dictionary<string, ColumnHeaderData>();

        // Width of the resize gripper zone at the right edge of each column header (px)
        private const double GripperWidth = 7;

        public CatalogBuilderWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Wires the ViewModel, applies theme and language, and registers dialog delegates.
        /// Call immediately after construction and before ShowDialog.
        /// </summary>
        public void Initialize(CatalogBuilderViewModel vm, Inventor.Application app)
        {
            _vm = vm;
            DataContext = vm;
            ThemeLoader.ApplyTo(this, app);
            LanguageLoader.ApplyTo(this);

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CatalogBuilderViewModel.IsBasicLogicsPanelOpen)) return;
                if (WindowState != WindowState.Normal) return;
                if (vm.IsBasicLogicsPanelOpen)
                {
                    _widthBeforeBLOpen = Width;
                    Dispatcher.InvokeAsync(() =>
                    {
                        // Must run at Loaded (after Render layout pass) so ActualWidth reflects expanded state.
                        double contentWidth = BLExpander.ActualWidth - 32;
                        if (contentWidth > 0)
                            Width = _widthBeforeBLOpen + contentWidth;
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    if (!double.IsNaN(_widthBeforeBLOpen))
                    {
                        Width = Math.Max(MinWidth, _widthBeforeBLOpen);
                        _widthBeforeBLOpen = double.NaN;
                    }
                }
            };

            if (UiStateStore.TryLoadCatalogBuilderSize(out double w, out double h))
            {
                Width  = w;
                Height = h;
            }

            vm.AskForText = (title, initial) =>
            {
                // Clear DataGrid selection before AND after the rename dialog.
                // The ObservableCollection Replace that follows the rename fires ColumnsChanged →
                // Columns.Clear(), which crashes via dotnet/wpf #4279 if cells are selected.
                // After the dialog closes WPF may re-focus the DataGrid and re-select cells,
                // so we clear again and steal focus back to the window.
                EntryGrid.UnselectAllCells();
                var dlg    = new InputDialog(initial) { Owner = this, Title = title };
                var result = dlg.ShowDialog() == true ? dlg.InputText : null;
                EntryGrid.UnselectAllCells();
                Focus();
                return result;
            };

            vm.ConfirmDelete = () =>
                MessageBox.Show(this,
                    LanguageLoader.Get("CatBuilder_Dlg_ConfirmDelete"),
                    LanguageLoader.Get("CatBuilder_Dlg_ConfirmDeleteTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;

            vm.PromptUnsavedChanges = () =>
            {
                var result = MessageBox.Show(this,
                    LanguageLoader.Get("CatBuilder_Dlg_UnsavedChanges"),
                    LanguageLoader.Get("CatBuilder_Dlg_UnsavedChangesTitle"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                return result == MessageBoxResult.Yes  ? true  :
                       result == MessageBoxResult.No   ? false :
                       (bool?)null;
            };

            vm.PickSaveFile = () =>
            {
                var dlg = new SaveFileDialog
                {
                    Title      = LanguageLoader.Get("CatBuilder_Dlg_ExportTitle"),
                    Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            };

            vm.PickOpenFile = () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title      = LanguageLoader.Get("CatBuilder_Dlg_ImportTitle"),
                    Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            };

            vm.PickCapSetSaveFile = () =>
            {
                var dlg = new SaveFileDialog
                {
                    Title      = LanguageLoader.Get("CatBuilder_Dlg_ExportCapSet"),
                    Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            };

            vm.PickCapSetOpenFile = () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title      = LanguageLoader.Get("CatBuilder_Dlg_ImportCapSet"),
                    Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            };

            vm.ConfirmUpdateCatalog = () =>
            {
                var dlg = new InfoDialog(LanguageLoader.Get("Msg_UpdateCatalog"), "UpdateCatalog",
                                         "Win_Title_LogicConstructorInfo", 400, 200,
                                         showCancel: true) { Owner = this };
                return dlg.ShowDialog() == true;
            };

            vm.ConfirmUpdateCapSet = () =>
            {
                var dlg = new InfoDialog(LanguageLoader.Get("Msg_UpdateCapSet"), "UpdateCapSet",
                                         "Win_Title_LogicConstructorInfo", 400, 200,
                                         showCancel: true) { Owner = this };
                return dlg.ShowDialog() == true;
            };

            vm.ColumnsChanged   += RebuildDataGridColumns;
            vm.PropertyChanged  += Vm_PropertyChanged;
            vm.RoleIndicesChanged += RefreshAllHeaderBadges;
            BuildContextMenu();

            // If a catalog was restored from registry, rebuild columns now that the handler is hooked up.
            if (vm.HasSelectedCatalog)
                RebuildDataGridColumns();
        }

        // Syncs all column header role badges when the user picks a new role from the ComboBox.
        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CatalogBuilderViewModel.SelectedColumnRole))
                RefreshAllHeaderBadges();
            // Clear DataGrid selection before the Catalog panel becomes visible — prevents the
            // WPF DataGrid crash (dotnet/wpf #4279) where Columns.Clear() with stale selected
            // cells crashes via _selectedCells.OnColumnsChanged().
            if (e.PropertyName == nameof(CatalogBuilderViewModel.IsCatalogsTab) && _vm?.IsCatalogsTab == true)
                EntryGrid.UnselectAllCells();
        }

        // Recomputes same-type counts and pushes updated Role/RoleIndex/SameTypeCount to all headers.
        // Called when a role is assigned/cleared (SelectedColumnRole changes) or when indices swap
        // (MoveRoleIndex fires RoleIndicesChanged).
        private void RefreshAllHeaderBadges()
        {
            if (_vm == null) return;
            var roleTypeCounts = _vm.CurrentColumns
                .Where(c => c.Role != ColumnRole.None)
                .GroupBy(c => c.Role)
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var col in _vm.CurrentColumns)
            {
                if (!_headerDataByKey.TryGetValue(col.Key, out var hd)) continue;
                int cnt = col.Role != ColumnRole.None && roleTypeCounts.TryGetValue(col.Role, out int c) ? c : 1;
                hd.Role          = col.Role;
                hd.RoleIndex     = col.RoleIndex;
                hd.SameTypeCount = cnt;
            }
        }

        // ══════════════════════════════════════════════
        //  DYNAMIC DATAGRID COLUMNS
        // ══════════════════════════════════════════════

        private void RebuildDataGridColumns()
        {
            _rebuildingColumns = true;
            try
            {
                SaveCurrentColumnWidths();                          // persist before teardown
                _currentColumnCatalogId = _vm?.SelectedCatalog?.Id; // update to incoming catalog

                // Clear selection before touching columns — Columns.Clear() with selected cells
                // crashes via internal _selectedCells.OnColumnsChanged() validation (dotnet/wpf #4279)
                EntryGrid.UnselectAllCells();
                ClearHeaderHighlights();
                _headerDataByKey.Clear();
                EntryGrid.Columns.Clear();
                if (_vm == null || _vm.CurrentColumns.Count == 0) return;

                var savedWidths = UiStateStore.LoadCatalogColumnWidths(_currentColumnCatalogId);
                var template    = (DataTemplate)TryFindResource("ColHeaderTemplate");

                // Pre-compute how many columns share each role type so badges show numbers when needed.
                var roleTypeCounts = _vm.CurrentColumns
                    .Where(c => c.Role != ColumnRole.None)
                    .GroupBy(c => c.Role)
                    .ToDictionary(g => g.Key, g => g.Count());

                for (int i = 0; i < _vm.CurrentColumns.Count; i++)
                {
                    var col = _vm.CurrentColumns[i];
                    int sameTypeCount = col.Role != ColumnRole.None && roleTypeCounts.TryGetValue(col.Role, out int cnt) ? cnt : 1;
                    var hd  = new ColumnHeaderData(ToColumnLetter(i), col.Label, col.Role, col.RoleIndex, sameTypeCount);
                    _headerDataByKey[col.Key] = hd;

                    // Use saved pixel width if the user previously resized this column,
                    // otherwise fall back to Star (proportional auto-fill).
                    DataGridLength colWidth = savedWidths.TryGetValue(col.Key, out double px)
                        ? new DataGridLength(px, DataGridLengthUnitType.Pixel)
                        : new DataGridLength(1, DataGridLengthUnitType.Star);

                    var dgCol = new DataGridTextColumn
                    {
                        Header         = hd,
                        HeaderTemplate = template,
                        Binding = new Binding($"[{col.Key}]")
                        {
                            Mode                = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                        },
                        Width               = colWidth,
                        MinWidth            = 80,
                        ElementStyle        = (Style)TryFindResource(typeof(TextBlock)),
                        EditingElementStyle = (Style)TryFindResource(typeof(TextBox)),
                    };
                    EntryGrid.Columns.Add(dgCol);
                }
            }
            finally { _rebuildingColumns = false; }

        }

        // Snapshots the current pixel widths of any user-resized columns for the active catalog.
        // Star-width columns (not manually resized) are intentionally not saved — they will
        // continue to auto-fill on next load.
        private void SaveCurrentColumnWidths()
        {
            if (string.IsNullOrEmpty(_currentColumnCatalogId) || _vm == null) return;
            var widths = new Dictionary<string, double>();
            foreach (var dgCol in EntryGrid.Columns)
            {
                if (!dgCol.Width.IsAbsolute) continue; // Star columns stay auto
                if (dgCol.Header is not ColumnHeaderData hd) continue;
                var catCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
                if (catCol != null) widths[catCol.Key] = dgCol.Width.Value;
            }
            UiStateStore.SaveCatalogColumnWidths(_currentColumnCatalogId, widths);
        }

        // ══════════════════════════════════════════════
        //  ROW NUMBERS
        // ══════════════════════════════════════════════

        private void EntryGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            try
            {
                int idx = e.Row.GetIndex();
                e.Row.Header = idx >= 0 ? (idx + 1).ToString() : null;
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  CELL SELECTION → highlight row + column header
        //  Deferred via Dispatcher to avoid crashing during DataGrid keyboard navigation
        // ══════════════════════════════════════════════

        private void EntryGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            SyncSelectionToVm();   // keep SelectedColumn current so role ComboBox stays in sync
            ClearHeaderHighlights();
            if (EntryGrid.SelectedCells.Count == 0) return;

            // Capture column reference now; SelectedCells may shift before the callback fires
            var capturedColumn = EntryGrid.SelectedCells[0].Column;

            // Visual-tree access deferred so it never re-enters DataGrid's own selection processing
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    if (EntryGrid.SelectedCells.Count == 0) return;
                    var cell = EntryGrid.SelectedCells[0];
                    if (cell.Item == null || cell.Item == CollectionView.NewItemPlaceholder) return;

                    _highlightedColumnHeader = FindColumnHeader(capturedColumn);
                    if (_highlightedColumnHeader != null) _highlightedColumnHeader.Tag = "selected";

                    int idx = EntryGrid.Items.IndexOf(cell.Item);
                    if (idx >= 0 &&
                        EntryGrid.ItemContainerGenerator.ContainerFromIndex(idx) is DataGridRow row)
                    {
                        _highlightedRowHeader = FindVisualChild<DataGridRowHeader>(row);
                        if (_highlightedRowHeader != null) _highlightedRowHeader.Tag = "selected";
                    }
                }
                catch { }
            }));
        }

        // ══════════════════════════════════════════════
        //  ROW / COLUMN ACTION BUTTONS
        //  Context is read synchronously at click time — DataGrid.SelectedCells is always settled
        //  during a button click, unlike inside SelectedCellsChanged which fires mid-processing.
        // ══════════════════════════════════════════════

        private void AddRow_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();        // 1. capture context from SelectedCells
            EntryGrid.UnselectAllCells(); // 2. clear selection before collection mutation (dotnet/wpf #4279)
            _vm?.AddEntryCommand.Execute(null);
        }

        private void RemoveRow_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();
            EntryGrid.UnselectAllCells();
            _vm?.RemoveEntryCommand.Execute(null);
        }

        private void AddColumn_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();
            EntryGrid.UnselectAllCells();
            _vm?.AddColumnCommand.Execute(null);
        }

        private void RemoveColumn_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();
            EntryGrid.UnselectAllCells();
            _vm?.DeleteColumnCommand.Execute(null);
        }

        private void CatalogRoleHelp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InfoDialog(Services.InfoPanelBuilder.BuildRoleHelp(),
                "RoleHelp", "Win_Title_LogicConstructorInfo", 600, 700) { Owner = this };
            dlg.ShowDialog();
        }

        private void CapCardHelp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InfoDialog(Services.InfoPanelBuilder.BuildCardHelp(),
                "CardHelp", "Win_Title_LogicConstructorInfo", 650, 750) { Owner = this };
            dlg.ShowDialog();
        }

        // Reads the current DataGrid selection and pushes it into the VM synchronously.
        // Must be called BEFORE UnselectAllCells() — SelectedCells is empty afterwards.
        private void SyncSelectionToVm()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;
            var cell = EntryGrid.SelectedCells[0];
            if (cell.Item is EntryRow row)
                _vm.SelectedEntry = row;
            if (cell.Column?.Header is ColumnHeaderData hd)
                _vm.SelectedColumn = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
        }

        private void ClearHeaderHighlights()
        {
            if (_highlightedColumnHeader != null) { _highlightedColumnHeader.Tag = null; _highlightedColumnHeader = null; }
            if (_highlightedRowHeader    != null) { _highlightedRowHeader.Tag    = null; _highlightedRowHeader    = null; }
        }

        // ══════════════════════════════════════════════
        //  COPY / PASTE  (Ctrl+C / Ctrl+V)
        //  Handled at DataGrid level so the Window-level ESC handler stays clean.
        //  Format: tab-separated columns, \r\n-separated rows (Excel-compatible TSV).
        // ══════════════════════════════════════════════

        private void EntryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !(Keyboard.FocusedElement is TextBox))
            {
                HandleClearContents();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if      (e.Key == Key.C) { HandleCopy();      e.Handled = true; }
            else if (e.Key == Key.V) { HandlePaste();     e.Handled = true; }
            else if (e.Key == Key.X) { HandleCut();       e.Handled = true; }
            else if (e.Key == Key.D) { HandleFillDown();  e.Handled = true; }
            else if (e.Key == Key.R) { HandleFillRight(); e.Handled = true; }
            else if (e.Key == Key.Home)
            {
                if (EntryGrid.Items.Count > 0 && EntryGrid.Columns.Count > 0)
                    JumpToCell(0, ColModelIndexForDisplayIndex(0));
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                if (EntryGrid.Items.Count > 0 && EntryGrid.Columns.Count > 0)
                    JumpToCell(EntryGrid.Items.Count - 1, ColModelIndexForDisplayIndex(EntryGrid.Columns.Count - 1));
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                if (FindBar.Visibility == Visibility.Visible)
                    CloseFindBar();
                else
                    OpenFindBar();
                e.Handled = true;
            }
        }

        private int ColModelIndexForDisplayIndex(int displayIdx)
        {
            for (int i = 0; i < EntryGrid.Columns.Count; i++)
                if (EntryGrid.Columns[i].DisplayIndex == displayIdx) return i;
            return 0;
        }

        private void HandleCopy()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;

            var cells = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null)
                .ToList();
            if (cells.Count == 0) return;

            // Determine bounding box of selection
            int minRow = cells.Select(c => EntryGrid.Items.IndexOf(c.Item)).Min();
            int maxRow = cells.Select(c => EntryGrid.Items.IndexOf(c.Item)).Max();
            int minCol = cells.Select(c => c.Column.DisplayIndex).Min();
            int maxCol = cells.Select(c => c.Column.DisplayIndex).Max();

            // O(1) lookup: is (rowIdx, displayColIdx) selected?
            var selected = new HashSet<(int, int)>(
                cells.Select(c => (EntryGrid.Items.IndexOf(c.Item), c.Column.DisplayIndex)));

            // Columns indexed by DisplayIndex for O(1) lookup
            var colsByDisplay = new DataGridColumn[EntryGrid.Columns.Count];
            foreach (var col in EntryGrid.Columns) colsByDisplay[col.DisplayIndex] = col;

            var sb = new StringBuilder();
            for (int r = minRow; r <= maxRow; r++)
            {
                if (r > minRow) sb.Append("\r\n");
                for (int c = minCol; c <= maxCol; c++)
                {
                    if (c > minCol) sb.Append('\t');
                    if (selected.Contains((r, c)) && EntryGrid.Items[r] is EntryRow row &&
                        colsByDisplay[c]?.Header is ColumnHeaderData hd)
                    {
                        var catCol = _vm.CurrentColumns.FirstOrDefault(cc => cc.Label == hd.Label);
                        if (catCol != null) sb.Append(row[catCol.Key]);
                    }
                }
            }

            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void HandlePaste()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;

            string text;
            try { text = Clipboard.GetText(); } catch { return; }
            if (string.IsNullOrEmpty(text)) return;

            // Anchor = top-left cell of current selection
            var anchorCell = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null)
                .OrderBy(c => EntryGrid.Items.IndexOf(c.Item))
                .ThenBy(c => c.Column.DisplayIndex)
                .FirstOrDefault();
            if (anchorCell.Item is not EntryRow) return;

            int anchorRow = EntryGrid.Items.IndexOf(anchorCell.Item);
            int anchorCol = anchorCell.Column.DisplayIndex;

            var colsByDisplay = new DataGridColumn[EntryGrid.Columns.Count];
            foreach (var col in EntryGrid.Columns) colsByDisplay[col.DisplayIndex] = col;

            var lines = text.TrimEnd('\r', '\n').Split('\n');
            for (int r = 0; r < lines.Length; r++)
            {
                int rowIdx = anchorRow + r;
                if (rowIdx >= _vm.EntryRows.Count) break;

                var values = lines[r].TrimEnd('\r').Split('\t');
                var entryRow = _vm.EntryRows[rowIdx];

                for (int c = 0; c < values.Length; c++)
                {
                    int colIdx = anchorCol + c;
                    if (colIdx >= colsByDisplay.Length) break;
                    if (colsByDisplay[colIdx]?.Header is not ColumnHeaderData hd) continue;
                    var catCol = _vm.CurrentColumns.FirstOrDefault(cc => cc.Label == hd.Label);
                    if (catCol != null) entryRow[catCol.Key] = values[c];
                }
            }
        }

        // ══════════════════════════════════════════════
        //  CONTEXT MENU — built programmatically so DynamicResource language lookup works
        //  inside Inventor's WPF host (popup visual tree is isolated from Window resources)
        // ══════════════════════════════════════════════

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("CtxMenu_Copy",           "Ctrl+C", CtxCopy_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_Cut",            "Ctrl+X", CtxCut_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_Paste",          "Ctrl+V", CtxPaste_Click));

            // Fill Down submenu: Same Value (Ctrl+D) or Series auto-detect
            var fillDown = new MenuItem { Header = LanguageLoader.Get("CtxMenu_FillDown") };
            fillDown.Items.Add(MakeMenuItem("CtxMenu_FillSameValue", "Ctrl+D", CtxFillDownSame_Click));
            fillDown.Items.Add(MakeMenuItem("CtxMenu_FillSeries",    null,     CtxFillDownSeries_Click));
            menu.Items.Add(fillDown);

            // Fill Right submenu: Same Value (Ctrl+R) or Series auto-detect
            var fillRight = new MenuItem { Header = LanguageLoader.Get("CtxMenu_FillRight") };
            fillRight.Items.Add(MakeMenuItem("CtxMenu_FillSameValue", "Ctrl+R", CtxFillRightSame_Click));
            fillRight.Items.Add(MakeMenuItem("CtxMenu_FillSeries",    null,     CtxFillRightSeries_Click));
            menu.Items.Add(fillRight);

            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("CtxMenu_ClearContents",  "Del",    CtxClear_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("CtxMenu_InsertRowAbove", null,     CtxInsertAbove_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_InsertRowBelow", null,     CtxInsertBelow_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_DeleteRow",      null,     CtxDeleteRow_Click));
            _ctxMoveRowUp   = MakeMenuItem("CtxMenu_MoveRowUp",   null,     CtxMoveRowUp_Click);
            _ctxMoveRowDown = MakeMenuItem("CtxMenu_MoveRowDown", null,     CtxMoveRowDown_Click);
            menu.Items.Add(_ctxMoveRowUp);
            menu.Items.Add(_ctxMoveRowDown);
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("CtxMenu_InsertColLeft",  null,     CtxInsertColLeft_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_InsertColRight", null,     CtxInsertColRight_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_DeleteCol",      null,     CtxDeleteCol_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("CtxMenu_SortAZ",         null,     CtxSortAZ_Click));
            menu.Items.Add(MakeMenuItem("CtxMenu_SortZA",         null,     CtxSortZA_Click));

            // Store in field — do NOT assign to EntryGrid.ContextMenu.
            // Assigning a ContextMenu to the DataGrid itself registers WPF mouse-event
            // listeners that break the column-header resize Thumb in Inventor's WPF host.
            // The menu is shown manually from EntryGrid_CellMouseRightButtonUp instead.
            _contextMenu = menu;
            EntryGrid.MouseRightButtonUp += EntryGrid_CellMouseRightButtonUp;
        }

        private static MenuItem MakeMenuItem(string langKey, string gesture, RoutedEventHandler handler)
        {
            var item = new MenuItem { Header = LanguageLoader.Get(langKey) };
            if (gesture != null) item.InputGestureText = gesture;
            item.Click += handler;
            return item;
        }

        // Shows the cell context menu on right-click over a data cell.
        // Column headers are already handled by ColumnHeader_MouseRightButtonUp (which sets
        // e.Handled = true, so this handler is never reached for header right-clicks).
        private void EntryGrid_CellMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject d)) return;
            var cell = FindVisualParent<DataGridCell>(d);
            if (cell == null) return;

            if (_vm?.IsSelectedCatalogLocked == true)
            {
                // Locked: no context menu — silently copy right-clicked cell value to clipboard.
                if (cell.DataContext is EntryRow row &&
                    cell.Column?.Header is ColumnHeaderData hd)
                {
                    var catCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
                    if (catCol != null)
                        try { Clipboard.SetText(row[catCol.Key]); } catch { }
                }
                e.Handled = true;
                return;
            }

            // Track which row was right-clicked so Move Up/Down can be enabled correctly.
            var dataGridRow = FindVisualParent<DataGridRow>(d);
            _rightClickedRowIndex = dataGridRow != null
                ? EntryGrid.ItemContainerGenerator.IndexFromContainer(dataGridRow)
                : -1;
            int rowCount = _vm?.EntryRows.Count ?? 0;
            if (_ctxMoveRowUp   != null) _ctxMoveRowUp.IsEnabled   = _rightClickedRowIndex > 0;
            if (_ctxMoveRowDown != null) _ctxMoveRowDown.IsEnabled = _rightClickedRowIndex >= 0
                                                                   && _rightClickedRowIndex < rowCount - 1;

            _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _contextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void CtxMoveRowUp_Click(object sender, RoutedEventArgs e)
        {
            if (_rightClickedRowIndex <= 0) return;
            EntryGrid.UnselectAllCells();
            _vm?.MoveEntryUp(_rightClickedRowIndex);
            ClearAllSortArrows();
        }

        private void CtxMoveRowDown_Click(object sender, RoutedEventArgs e)
        {
            if (_rightClickedRowIndex < 0) return;
            EntryGrid.UnselectAllCells();
            _vm?.MoveEntryDown(_rightClickedRowIndex);
            ClearAllSortArrows();
        }

        private void ClearAllSortArrows()
        {
            foreach (var hd in _headerDataByKey.Values)
                hd.SortDir = null;
        }

        // ── Context menu click handlers ──

        private void CtxCopy_Click(object s, RoutedEventArgs e)  => HandleCopy();
        private void CtxCut_Click(object s, RoutedEventArgs e)   => HandleCut();
        private void CtxPaste_Click(object s, RoutedEventArgs e) => HandlePaste();
        private void CtxFillDownSame_Click(object s, RoutedEventArgs e)     => HandleFillDown();
        private void CtxFillDownSeries_Click(object s, RoutedEventArgs e)   => HandleFillDownSeries();
        private void CtxFillRightSame_Click(object s, RoutedEventArgs e)    => HandleFillRight();
        private void CtxFillRightSeries_Click(object s, RoutedEventArgs e)  => HandleFillRightSeries();
        private void CtxClear_Click(object s, RoutedEventArgs e) => HandleClearContents();

        private void CtxInsertAbove_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;
            var row = EntryGrid.SelectedCells[0].Item as EntryRow;
            EntryGrid.UnselectAllCells();
            _vm.InsertEntryBefore(row);
        }

        private void CtxInsertBelow_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();
            EntryGrid.UnselectAllCells();
            _vm?.AddEntryCommand.Execute(null);
        }

        private void CtxDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            // Collect every distinct selected row (single or multi-select) BEFORE clearing selection.
            var rows = EntryGrid.SelectedCells
                .Select(c => c.Item as EntryRow)
                .Where(r => r != null)
                .Distinct()
                .ToList();
            EntryGrid.UnselectAllCells();
            if (rows.Count > 0) _vm.RemoveEntries(rows);
        }

        private void CtxInsertColLeft_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;
            var dgCol = EntryGrid.SelectedCells[0].Column;
            CatalogColumn catCol = dgCol?.Header is ColumnHeaderData hd
                ? _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label) : null;
            EntryGrid.UnselectAllCells();
            _vm.InsertColumnBefore(catCol);
        }

        private void CtxInsertColRight_Click(object sender, RoutedEventArgs e)
        {
            SyncSelectionToVm();
            EntryGrid.UnselectAllCells();
            _vm?.AddColumnCommand.Execute(null);
        }

        private void CtxDeleteCol_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            // Collect every distinct selected column (single or multi-select) BEFORE clearing selection.
            var cols = EntryGrid.SelectedCells
                .Select(c => c.Column?.Header is ColumnHeaderData hd
                    ? _vm.CurrentColumns.FirstOrDefault(cc => cc.Label == hd.Label) : null)
                .Where(c => c != null)
                .Distinct()
                .ToList();
            EntryGrid.UnselectAllCells();
            if (cols.Count > 0) _vm.DeleteColumns(cols);
        }

        private void CtxSortAZ_Click(object sender, RoutedEventArgs e) => TriggerContextMenuSort(ascending: true);
        private void CtxSortZA_Click(object sender, RoutedEventArgs e) => TriggerContextMenuSort(ascending: false);

        // Sort via context menu — shows a dialog when the selection doesn't cover all rows,
        // offering to sort selected rows only or expand to the full table.
        // Header-click sort always sorts all rows without a dialog (quick, unambiguous).
        private void TriggerContextMenuSort(bool ascending)
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;
            if (EntryGrid.SelectedCells[0].Column?.Header is not ColumnHeaderData hd) return;
            var col = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
            if (col == null) return;

            // Collect the distinct row indices that have at least one selected cell
            var selectedRowIndices = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow)
                .Select(c => EntryGrid.Items.IndexOf(c.Item))
                .Where(i => i >= 0)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            bool sortAll = true;
            // Only offer partial-sort when ≥ 2 distinct rows are selected but not all rows.
            // A single-row selection is meaningless to sort, so treat it as "sort all".
            bool partialSelection = selectedRowIndices.Count >= 2
                && selectedRowIndices.Count < _vm.EntryRows.Count;

            if (partialSelection)
            {
                string msg = string.Format(
                    LanguageLoader.Get("Sort_Dlg_Partial"),
                    selectedRowIndices.Count, _vm.EntryRows.Count);
                var answer = MessageBox.Show(this, msg,
                    LanguageLoader.Get("Sort_Dlg_Title"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                sortAll = answer == MessageBoxResult.No;  // No = full table, Yes = selection only
            }

            _vm.SelectedColumn = col;
            // Clear selection before RefreshEntries() inside the sort to avoid the WPF DataGrid
            // crash pattern where CollectionChanged fires with stale selected-cell references.
            EntryGrid.UnselectAllCells();
            bool resultAsc = sortAll
                ? _vm.SortByColumnKey(col.Key, ascending)
                : _vm.SortRangeByColumnKey(col.Key, ascending, selectedRowIndices);
            ApplySortAndUpdateArrows(col.Key, resultAsc);
        }

        // ══════════════════════════════════════════════
        //  CELL OPERATIONS — Cut / Clear / Fill
        // ══════════════════════════════════════════════

        private void HandleCut() { HandleCopy(); HandleClearContents(); }

        private void HandleClearContents()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count == 0) return;
            foreach (var cellInfo in EntryGrid.SelectedCells)
            {
                if (cellInfo.Item is not EntryRow row) continue;
                if (cellInfo.Column?.Header is not ColumnHeaderData hd) continue;
                var catCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
                if (catCol != null) row[catCol.Key] = "";
            }
        }

        private void HandleFillDown()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count <= 1) return;
            var cells = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null).ToList();
            foreach (var colGroup in cells.GroupBy(c => c.Column.DisplayIndex))
            {
                var ordered = colGroup.OrderBy(c => EntryGrid.Items.IndexOf(c.Item)).ToList();
                if (ordered.Count < 2) continue;
                if (ordered[0].Item is not EntryRow sourceRow) continue;
                if (ordered[0].Column?.Header is not ColumnHeaderData hd) continue;
                var catCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
                if (catCol == null) continue;
                string fillValue = sourceRow[catCol.Key];
                foreach (var tc in ordered.Skip(1))
                    if (tc.Item is EntryRow tr) tr[catCol.Key] = fillValue;
            }
        }

        private void HandleFillRight()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count <= 1) return;
            var cells = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null).ToList();
            foreach (var rowGroup in cells.GroupBy(c => EntryGrid.Items.IndexOf(c.Item)))
            {
                var ordered = rowGroup.OrderBy(c => c.Column.DisplayIndex).ToList();
                if (ordered.Count < 2) continue;
                if (ordered[0].Item is not EntryRow row) continue;
                if (ordered[0].Column?.Header is not ColumnHeaderData srcHd) continue;
                var srcCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == srcHd.Label);
                if (srcCol == null) continue;
                string fillValue = row[srcCol.Key];
                foreach (var tc in ordered.Skip(1))
                {
                    if (tc.Column?.Header is not ColumnHeaderData tgtHd) continue;
                    var tgtCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == tgtHd.Label);
                    if (tgtCol != null) row[tgtCol.Key] = fillValue;
                }
            }
        }

        // Fills selected cells downward per column, auto-detecting and continuing a series.
        // Step is inferred from the first two cells when the second already has a value;
        // defaults to +1 when detection is impossible.
        private void HandleFillDownSeries()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count <= 1) return;
            var cells = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null).ToList();

            foreach (var colGroup in cells.GroupBy(c => c.Column.DisplayIndex))
            {
                var ordered = colGroup.OrderBy(c => EntryGrid.Items.IndexOf(c.Item)).ToList();
                if (ordered.Count < 2) continue;
                if (ordered[0].Column?.Header is not ColumnHeaderData hd) continue;
                var catCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
                if (catCol == null) continue;

                string src = (ordered[0].Item as EntryRow)?[catCol.Key] ?? "";
                double step = 1.0;
                if (ordered.Count >= 2 && ordered[1].Item is EntryRow row2)
                {
                    string val2 = row2[catCol.Key];
                    if (!string.IsNullOrEmpty(val2)) step = DetectSeriesStep(src, val2);
                }

                for (int i = 1; i < ordered.Count; i++)
                    if (ordered[i].Item is EntryRow tr)
                        tr[catCol.Key] = IncrementSeriesValue(src, step, i);
            }
        }

        // Fills selected cells rightward per row, auto-detecting and continuing a series.
        private void HandleFillRightSeries()
        {
            if (_vm == null || EntryGrid.SelectedCells.Count <= 1) return;
            var cells = EntryGrid.SelectedCells
                .Where(c => c.Item is EntryRow && c.Column != null).ToList();

            foreach (var rowGroup in cells.GroupBy(c => EntryGrid.Items.IndexOf(c.Item)))
            {
                var ordered = rowGroup.OrderBy(c => c.Column.DisplayIndex).ToList();
                if (ordered.Count < 2) continue;
                if (ordered[0].Item is not EntryRow row) continue;
                if (ordered[0].Column?.Header is not ColumnHeaderData srcHd) continue;
                var srcCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == srcHd.Label);
                if (srcCol == null) continue;

                string src = row[srcCol.Key];
                double step = 1.0;
                if (ordered.Count >= 2 && ordered[1].Column?.Header is ColumnHeaderData hd2)
                {
                    var col2 = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd2.Label);
                    if (col2 != null)
                    {
                        string val2 = row[col2.Key];
                        if (!string.IsNullOrEmpty(val2)) step = DetectSeriesStep(src, val2);
                    }
                }

                for (int i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].Column?.Header is not ColumnHeaderData tgtHd) continue;
                    var tgtCol = _vm.CurrentColumns.FirstOrDefault(c => c.Label == tgtHd.Label);
                    if (tgtCol != null) row[tgtCol.Key] = IncrementSeriesValue(src, step, i);
                }
            }
        }

        // Computes the step between two consecutive series values.
        // Tries integers → floats → dates → text-with-trailing-number; falls back to 1.
        private static double DetectSeriesStep(string val1, string val2)
        {
            if (long.TryParse(val1, out long l1) && long.TryParse(val2, out long l2))
                return l2 - l1;

            if (double.TryParse(val1, NumberStyles.Any, CultureInfo.CurrentCulture, out double d1) &&
                double.TryParse(val2, NumberStyles.Any, CultureInfo.CurrentCulture, out double d2))
                return d2 - d1;

            if (DateTime.TryParse(val1, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime dt1) &&
                DateTime.TryParse(val2, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime dt2))
                return (dt2 - dt1).TotalDays;

            var m1 = Regex.Match(val1, @"(\d+)$");
            var m2 = Regex.Match(val2, @"(\d+)$");
            if (m1.Success && m2.Success &&
                long.TryParse(m1.Value, out long n1) && long.TryParse(m2.Value, out long n2))
                return n2 - n1;

            return 1.0;
        }

        // Returns source value incremented by step * count, preserving the source format.
        // Falls back to returning source unchanged when the type can't be detected.
        private static string IncrementSeriesValue(string source, double step, int count)
        {
            double delta = step * count;

            // Pure integer — preserve leading-zero padding
            if (long.TryParse(source, out long lval))
            {
                string result = (lval + (long)Math.Round(delta)).ToString();
                if (source.Length > 1 && source[0] == '0')
                    result = result.PadLeft(source.Length, '0');
                return result;
            }

            // Floating-point — preserve decimal-place count
            if (double.TryParse(source, NumberStyles.Any, CultureInfo.CurrentCulture, out double dval))
            {
                string sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                int dp = 0;
                int dotIdx = source.IndexOf(sep, StringComparison.Ordinal);
                if (dotIdx >= 0) dp = source.Length - dotIdx - sep.Length;
                return (dval + delta).ToString("F" + dp, CultureInfo.CurrentCulture);
            }

            // Date — preserve short/long date style based on source
            if (DateTime.TryParse(source, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime date))
            {
                var result = date.AddDays(delta);
                bool hasTime = source.IndexOf(':') >= 0;
                return hasTime
                    ? result.ToString(CultureInfo.CurrentCulture)
                    : result.ToString("d", CultureInfo.CurrentCulture);
            }

            // Text with trailing number (e.g. "Part01", "Item_3") — preserve prefix + pad length
            var match = Regex.Match(source, @"^(.*?)(\d+)$");
            if (match.Success && long.TryParse(match.Groups[2].Value, out long n))
            {
                string prefix = match.Groups[1].Value;
                string numStr = match.Groups[2].Value;
                string numResult = (n + (long)Math.Round(delta)).ToString();
                if (numStr.Length > numResult.Length && numStr[0] == '0')
                    numResult = numResult.PadLeft(numStr.Length, '0');
                return prefix + numResult;
            }

            return source; // can't detect series pattern — fill same
        }

        // ══════════════════════════════════════════════
        //  COLUMN DISPLAY ORDER → sync to working copy + update letters
        // ══════════════════════════════════════════════

        private void EntryGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            if (_rebuildingColumns || _vm == null) return;
            var labelOrder = EntryGrid.Columns
                .OrderBy(c => c.DisplayIndex)
                .Select(c => (c.Header as ColumnHeaderData)?.Label ?? "")
                .ToList();
            _vm.SyncColumnOrder(labelOrder);
            UpdateColumnLetters();
        }

        private void UpdateColumnLetters()
        {
            var sorted = EntryGrid.Columns.OrderBy(c => c.DisplayIndex).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Header is ColumnHeaderData hd)
                    hd.Letter = ToColumnLetter(i);
            }
        }

        // ══════════════════════════════════════════════
        //  COLUMN HEADER SINGLE CLICK → SORT
        //  Position check excludes the resize-gripper zone (right edge)
        // ══════════════════════════════════════════════

        // Width of the clickable sort-caret zone at the header's right edge (just left of the
        // resize gripper). Spreadsheet model: caret = sort, rest of the header = select column.
        private const double SortCaretZoneWidth = 22;

        // Anchors for Shift+click range selection (spreadsheet-style).
        private DataGridColumn _colSelectAnchor;
        private int            _rowSelectAnchor = -1;

        private void ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null || sender is not DataGridColumnHeader header) return;
            if (IsGripperClick(header)) return;                 // right-edge resize zone — DataGrid handles
            if (header.Content is not ColumnHeaderData hd) return;
            var col = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
            if (col == null) return;

            if (IsSortCaretClick(header))
            {
                // Sort caret → sort by this column; repeat clicks toggle direction.
                // CRASH GUARD: clear the selection BEFORE the sort mutates the row collection —
                // a stale multi-cell selection across reordered rows crashes the DataGrid
                // (dotnet/wpf #4279). The context-menu sort path does the same.
                _vm.SelectedColumn = col;
                EntryGrid.UnselectAllCells();
                ApplySortAndUpdateArrows(col.Key, _vm.SortByColumnKey(col.Key));
                return;
            }

            // Header body → select the whole column (spreadsheet-style).
            if (IsShiftDown() && _colSelectAnchor != null)
            {
                SelectColumnRange(_colSelectAnchor, header.Column);          // Shift = range from anchor
            }
            else
            {
                SelectWholeColumn(header.Column, additive: IsCtrlDown());    // plain = replace, Ctrl = add
                _colSelectAnchor = header.Column;
            }
            _vm.SelectedColumn = col;
        }

        // The clickable sort-caret zone sits just left of the resize gripper at the right edge.
        private static bool IsSortCaretClick(DataGridColumnHeader header)
        {
            double x = Mouse.GetPosition(header).X;
            return x >= header.ActualWidth - GripperWidth - SortCaretZoneWidth
                && x <  header.ActualWidth - GripperWidth;
        }

        private static bool IsCtrlDown() =>
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        private static bool IsShiftDown() =>
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // Selects every data cell in the given column (spreadsheet whole-column select).
        private void SelectWholeColumn(DataGridColumn dgColumn, bool additive)
        {
            if (dgColumn == null) return;
            if (!additive) EntryGrid.UnselectAllCells();
            foreach (var item in EntryGrid.Items)
                if (item is EntryRow)
                    EntryGrid.SelectedCells.Add(new DataGridCellInfo(item, dgColumn));
            FocusGridForKeyboard();
        }

        // Selects every cell across the columns between two headers (Shift+click range).
        private void SelectColumnRange(DataGridColumn from, DataGridColumn to)
        {
            if (from == null || to == null) { SelectWholeColumn(to ?? from, additive: false); return; }
            int a = from.DisplayIndex, b = to.DisplayIndex;
            if (a > b) { (a, b) = (b, a); }
            EntryGrid.UnselectAllCells();
            foreach (var dgCol in EntryGrid.Columns)
                if (dgCol.DisplayIndex >= a && dgCol.DisplayIndex <= b)
                    foreach (var item in EntryGrid.Items)
                        if (item is EntryRow)
                            EntryGrid.SelectedCells.Add(new DataGridCellInfo(item, dgCol));
            FocusGridForKeyboard();
        }

        // Selects every cell in the given row (spreadsheet whole-row select).
        private void SelectWholeRow(object rowItem, bool additive)
        {
            if (rowItem is not EntryRow) return;
            if (!additive) EntryGrid.UnselectAllCells();
            foreach (var dgCol in EntryGrid.Columns)
                EntryGrid.SelectedCells.Add(new DataGridCellInfo(rowItem, dgCol));
            FocusGridForKeyboard();
        }

        // Selects every cell across the rows between two indices (Shift+click range).
        private void SelectRowRange(int fromIndex, int toIndex)
        {
            if (fromIndex > toIndex) { (fromIndex, toIndex) = (toIndex, fromIndex); }
            EntryGrid.UnselectAllCells();
            for (int i = fromIndex; i <= toIndex; i++)
            {
                if (i < 0 || i >= EntryGrid.Items.Count) continue;
                if (EntryGrid.Items[i] is not EntryRow item) continue;
                foreach (var dgCol in EntryGrid.Columns)
                    EntryGrid.SelectedCells.Add(new DataGridCellInfo(item, dgCol));
            }
            FocusGridForKeyboard();
        }

        // After a header-driven selection, move keyboard focus into the grid (deferred until the
        // header click finishes) so Del → EntryGrid_PreviewKeyDown → Clear Contents works.
        private void FocusGridForKeyboard() =>
            EntryGrid.Dispatcher.BeginInvoke(new Action(() => EntryGrid.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);

        // Left-click on a row-number header → select the whole row. Ctrl adds, Shift = range.
        // Uses the Click event (DataGridRowHeader is a ButtonBase): MouseLeftButtonUp is swallowed
        // by the header's internal selection handling, so an EventSetter on it never fires.
        private void RowHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not DataGridRowHeader rh) return;
            var row = rh.DataContext as EntryRow
                   ?? FindVisualParent<DataGridRow>(rh)?.Item as EntryRow;
            if (row == null) return;
            int idx = EntryGrid.Items.IndexOf(row);

            if (IsShiftDown() && _rowSelectAnchor >= 0)
            {
                SelectRowRange(_rowSelectAnchor, idx);
            }
            else
            {
                SelectWholeRow(row, additive: IsCtrlDown());
                _rowSelectAnchor = idx;
            }
        }

        private void ApplySortAndUpdateArrows(string colKey, bool ascending)
        {
            foreach (var kvp in _headerDataByKey)
                kvp.Value.SortDir = kvp.Key == colKey
                    ? (ascending ? ListSortDirection.Ascending : ListSortDirection.Descending)
                    : (ListSortDirection?)null;
        }

        // ══════════════════════════════════════════════
        //  COLUMN HEADER RIGHT-CLICK → context menu
        //  • Always shows "Edit Label…"
        //  • When column has a role with ≥2 same-type columns: also shows Move Role Up/Down
        // ══════════════════════════════════════════════

        private void ColumnHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null || sender is not DataGridColumnHeader header) return;
            if (header.Content is not ColumnHeaderData hd) return;
            var col = _vm.CurrentColumns.FirstOrDefault(c => c.Label == hd.Label);
            if (col == null) return;
            _vm.SelectedColumn  = col;
            _rightClickedColumn = col;

            bool hasRole    = col.Role != ColumnRole.None;
            bool canMoveUp  = hasRole && _vm.CurrentColumns.Any(c => c.Role == col.Role && c.RoleIndex < col.RoleIndex);
            bool canMoveDown = hasRole && _vm.CurrentColumns.Any(c => c.Role == col.Role && c.RoleIndex > col.RoleIndex);

            var menu = new ContextMenu();

            var editItem = new MenuItem { Header = LanguageLoader.Get("CatBuilder_Dlg_EditColumn") };
            editItem.Click += (s, ev) => _vm?.EditColumnCommand.Execute(null);
            menu.Items.Add(editItem);

            if (hasRole)
            {
                menu.Items.Add(new Separator());
                var upItem = new MenuItem
                {
                    Header    = "⬆  " + LanguageLoader.Get("CtxMenu_MoveRoleUp"),
                    IsEnabled = canMoveUp,
                };
                upItem.Click += (s, ev) =>
                {
                    if (_rightClickedColumn != null)
                        _vm?.MoveRoleIndex(_rightClickedColumn, -1);
                };
                var downItem = new MenuItem
                {
                    Header    = "⬇  " + LanguageLoader.Get("CtxMenu_MoveRoleDown"),
                    IsEnabled = canMoveDown,
                };
                downItem.Click += (s, ev) =>
                {
                    if (_rightClickedColumn != null)
                        _vm?.MoveRoleIndex(_rightClickedColumn, +1);
                };
                menu.Items.Add(upItem);
                menu.Items.Add(downItem);
            }

            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen    = true;
            e.Handled      = true;
        }

        // Returns true when the last mouse position is over the resize-gripper zone
        private static bool IsGripperClick(DataGridColumnHeader header)
        {
            Point pos = Mouse.GetPosition(header);
            return pos.X >= header.ActualWidth - GripperWidth;
        }

        // ══════════════════════════════════════════════
        //  GROUP DRAG & DROP (capabilities panel)
        //  Drag handle (2×3 dots) starts the operation; the group Border receives DragEnter/Drop.
        // ══════════════════════════════════════════════

        private void GroupDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not CardGroupVm groupVm) return;

            _draggedGroup = groupVm;
            groupVm.IsDragging = true;
            DragDrop.DoDragDrop(el, new DataObject("CatalogBuilderGroup", groupVm), DragDropEffects.Move);
            groupVm.IsDragging = false;
            _draggedGroup = null;
        }

        private void Group_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CatalogBuilderGroup")) return;
            if (sender is FrameworkElement el && el.DataContext is CardGroupVm groupVm && groupVm != _draggedGroup)
                groupVm.IsDragOver = true;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void Group_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is CardGroupVm groupVm)
                groupVm.IsDragOver = false;
        }

        private void Group_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CatalogBuilderGroup")) return;
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not CardGroupVm targetVm) return;
            if (_draggedGroup == null || _draggedGroup == targetVm) return;

            targetVm.IsDragOver = false;

            var groups = _vm?.CapSetGroups;
            if (groups == null) return;

            int fromIdx = groups.IndexOf(_draggedGroup);
            int toIdx   = groups.IndexOf(targetVm);
            if (fromIdx < 0 || toIdx < 0) return;

            _vm.OnGroupDragDropCompleted(fromIdx, toIdx);
            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  GROUP ACTIVATION (capabilities panel)
        //  PreviewMouseLeftButtonDown tunnels to the group Border before any child handles it.
        //  We walk up from the original source to detect whether a card was clicked.
        // ══════════════════════════════════════════════

        private void Group_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not CardGroupVm groupVm) return;

            CardRowVm cardVm = null;
            if (e.OriginalSource is DependencyObject src)
            {
                var item = FindVisualParent<ListBoxItem>(src);
                if (item?.DataContext is CardRowVm crvm) cardVm = crvm;
            }

            _vm.OnGroupCardActivated(groupVm, cardVm);
            // Don't set Handled — let the click continue so list selection etc. still work
        }

        // ══════════════════════════════════════════════
        //  CARD DRAG & DROP (within a group)
        //  Drag handle triggers the operation; each ListBoxItem is the drop target.
        // ══════════════════════════════════════════════

        private void CardDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not CardRowVm cardVm) return;

            var listBox = FindVisualParent<ListBox>(el);
            if (listBox?.DataContext is not CardGroupVm groupVm) return;

            _draggedCard      = cardVm;
            _draggedCardGroup = groupVm;
            DragDrop.DoDragDrop(el, new DataObject("CatalogBuilderCard", cardVm), DragDropEffects.Move);
            _draggedCard      = null;
            _draggedCardGroup = null;
        }

        private void Card_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CatalogBuilderCard")) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void Card_DragLeave(object sender, DragEventArgs e) { }

        private void Card_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CatalogBuilderCard")) return;
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not CardRowVm targetVm) return;
            if (_draggedCard == null || _draggedCard == targetVm) return;
            if (_draggedCardGroup == null) return;

            var listBox = FindVisualParent<ListBox>(el);
            if (listBox?.DataContext is not CardGroupVm targetGroup) return;

            if (targetGroup == _draggedCardGroup)
            {
                int fromIdx = _draggedCardGroup.Cards.IndexOf(_draggedCard);
                int toIdx   = _draggedCardGroup.Cards.IndexOf(targetVm);
                if (fromIdx < 0 || toIdx < 0) return;
                _draggedCardGroup.MoveCardTo(fromIdx, toIdx);
            }
            else
            {
                int toIdx = targetGroup.Cards.IndexOf(targetVm);
                if (toIdx < 0) return;
                var card = _draggedCard.Card;
                _draggedCardGroup.RemoveCard(_draggedCard);
                targetGroup.InsertCardAt(card, toIdx);
            }
            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  ESC — close window
        // ══════════════════════════════════════════════

        // ══════════════════════════════════════════════
        //  FIND BAR — Ctrl+F
        // ══════════════════════════════════════════════

        private void OpenFindBar()
        {
            FindBar.Visibility = Visibility.Visible;
            FindBox.Clear();
            _findMatches.Clear();
            _findIndex = 0;
            UpdateFindCounter();
            FindBox.Focus();
        }

        private void CloseFindBar()
        {
            FindBar.Visibility = Visibility.Collapsed;
            _findMatches.Clear();
            _findIndex = 0;
            EntryGrid.Focus();
        }

        private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunFind();

        private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                NavigateFind(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFindBar();
                e.Handled = true;
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)  => NavigateFind(+1);
        private void FindPrev_Click(object sender, RoutedEventArgs e)  => NavigateFind(-1);
        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void RunFind()
        {
            _findMatches.Clear();
            _findIndex = 0;
            var term = FindBox.Text;

            if (!string.IsNullOrEmpty(term) && _vm != null)
            {
                for (int r = 0; r < _vm.EntryRows.Count; r++)
                {
                    var row = _vm.EntryRows[r];
                    for (int c = 0; c < _vm.CurrentColumns.Count; c++)
                    {
                        if (row[_vm.CurrentColumns[c].Key]
                                .IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                            _findMatches.Add((r, c));
                    }
                }
            }

            UpdateFindCounter();
            if (_findMatches.Count > 0)
                JumpToCell(_findMatches[0].Item1, _findMatches[0].Item2);
        }

        private void NavigateFind(int delta)
        {
            if (_findMatches.Count == 0) return;
            _findIndex = (_findIndex + delta + _findMatches.Count) % _findMatches.Count;
            UpdateFindCounter();
            JumpToCell(_findMatches[_findIndex].Item1, _findMatches[_findIndex].Item2);
        }

        private void UpdateFindCounter()
        {
            if (_findMatches.Count == 0)
            {
                FindCounter.Text = FindBox.Text.Length > 0 ? LanguageLoader.Get("FindBar_NoMatch") : "";
                FindCounter.SetResourceReference(TextBlock.ForegroundProperty,
                    FindBox.Text.Length > 0 ? "CheckupErrorText" : "CheckupSecondaryText");
            }
            else
            {
                FindCounter.Text = string.Format(LanguageLoader.Get("FindBar_Counter"),
                                                 _findIndex + 1, _findMatches.Count);
                FindCounter.SetResourceReference(TextBlock.ForegroundProperty, "CheckupSecondaryText");
            }
        }

        private void JumpToCell(int rowIndex, int colIndex)
        {
            if (_vm == null) return;
            if (rowIndex < 0 || rowIndex >= EntryGrid.Items.Count) return;
            if (colIndex < 0 || colIndex >= EntryGrid.Columns.Count) return;
            var item = EntryGrid.Items[rowIndex];
            var col  = EntryGrid.Columns[colIndex];
            EntryGrid.ScrollIntoView(item, col);
            EntryGrid.CurrentCell = new DataGridCellInfo(item, col);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // Priority 1: close find bar
                if (FindBar.Visibility == Visibility.Visible)
                {
                    CloseFindBar();
                    e.Handled = true;
                    return;
                }
                // Priority 2: close any open TF or CF picker
                if (_vm != null)
                {
                    foreach (var g in _vm.CapSetGroups)
                    {
                        if (g.IsTargetFieldPickerOpen)
                        {
                            g.IsTargetFieldPickerOpen = false;
                            e.Handled = true;
                            return;
                        }
                        foreach (var c in g.Cards)
                        {
                            if (c.IsCardFieldPickerOpen)
                            {
                                c.IsCardFieldPickerOpen = false;
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                }
                // Priority 3: close window
                Close();
            }
            base.OnPreviewKeyDown(e);
        }

        // ══════════════════════════════════════════════
        //  CLOSING — dirty-check prompt
        // ══════════════════════════════════════════════

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm?.IsDirty == true)
            {
                var choice = _vm.PromptUnsavedChanges?.Invoke();
                if (choice == null)
                {
                    e.Cancel = true;
                    base.OnClosing(e);
                    return;
                }
                if (choice == true) _vm.SaveCommand.Execute(null);
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            UiStateStore.SaveCatalogBuilderSize(Width, Height);
            SaveCurrentColumnWidths();
            if (_vm != null)
            {
                _vm.ColumnsChanged    -= RebuildDataGridColumns;
                _vm.PropertyChanged   -= Vm_PropertyChanged;
                _vm.RoleIndicesChanged -= RefreshAllHeaderBadges;
            }
            base.OnClosed(e);
        }

        // ══════════════════════════════════════════════
        //  HELPERS — column letter, visual tree
        // ══════════════════════════════════════════════

        // Converts 0-based column index to spreadsheet letter: 0→A, 1→B, …, 25→Z, 26→AA …
        private static string ToColumnLetter(int index)
        {
            var sb = new System.Text.StringBuilder();
            index++;
            while (index > 0)
            {
                index--;
                sb.Insert(0, (char)('A' + index % 26));
                index /= 26;
            }
            return sb.ToString();
        }

        private DataGridColumnHeader FindColumnHeader(DataGridColumn column)
        {
            var presenter = FindVisualChild<DataGridColumnHeadersPresenter>(EntryGrid);
            if (presenter == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(presenter); i++)
            {
                if (VisualTreeHelper.GetChild(presenter, i) is DataGridColumnHeader h && h.Column == column)
                    return h;
            }
            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T t) return t;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        // ══════════════════════════════════════════════
        //  CAPABILITIES SCROLL — PreviewMouseWheel tunnels to outer ScrollViewer first,
        //  preventing inner ListBox from consuming wheel events and causing stutter.
        //  When the TargetField dropdown is open, skip so the popup's own ScrollViewer handles it.
        // ══════════════════════════════════════════════

        private bool _isTargetFieldDropDownOpen;

        private void CapabilitiesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isTargetFieldDropDownOpen) return;  // Let popup's ScrollViewer handle it
            var sv = (ScrollViewer)sender;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // Transparent lock overlay absorbs mouse events to block editing, but must still
        // pass wheel events through to the CapabilitiesScrollViewer so users can scroll.
        private void LockOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            CapabilitiesScrollViewer.ScrollToVerticalOffset(
                CapabilitiesScrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  TARGET FIELD PICKER (P3-B)
        // ══════════════════════════════════════════════

        private Border           _targetFieldPopupBorder;
        private double           _targetFieldDropdownHeight;
        private CardGroupVm      _activeTargetFieldPickerGroup;
        private PinnedFieldEntry _draggedTfFavoritenEntry;

        private void TargetFieldBtn_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.DataContext as CardGroupVm;
            g?.OpenTargetFieldPicker();
        }

        private void TargetFieldPopup_Opened(object sender, EventArgs e)
        {
            _isTargetFieldDropDownOpen = true;
            var popup = sender as System.Windows.Controls.Primitives.Popup;
            if (popup == null) return;
            _activeTargetFieldPickerGroup = (popup.PlacementTarget as FrameworkElement)?.DataContext as CardGroupVm;
            _targetFieldPopupBorder = popup.Child as Border;
            if (_targetFieldPopupBorder != null && _targetFieldDropdownHeight > 0)
            {
                _targetFieldPopupBorder.MaxHeight = double.PositiveInfinity;
                _targetFieldPopupBorder.Height    = _targetFieldDropdownHeight;
            }
        }

        private void TargetFieldPopup_Closed(object sender, EventArgs e)
        {
            _isTargetFieldDropDownOpen = false;
            _activeTargetFieldPickerGroup?.OnTargetFieldPickerClosed();
            if (_targetFieldPopupBorder != null && _targetFieldPopupBorder.ActualHeight > 0)
                _targetFieldDropdownHeight = _targetFieldPopupBorder.ActualHeight;
            _targetFieldPopupBorder = null;
            _activeTargetFieldPickerGroup = null;
        }

        private void TargetFieldPickerItem_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTargetFieldPickerGroup == null) return;
            var item = (sender as FrameworkElement)?.DataContext as FieldItem;
            if (item == null) return;
            _activeTargetFieldPickerGroup.TargetFieldKey = item.Key;
            _activeTargetFieldPickerGroup.IsTargetFieldPickerOpen = false;
        }

        private void TargetFieldPickerPinned_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTargetFieldPickerGroup == null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _activeTargetFieldPickerGroup.TargetFieldKey = entry.Key;
            _activeTargetFieldPickerGroup.IsTargetFieldPickerOpen = false;
        }

        private void TargetFieldPickerItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_activeTargetFieldPickerGroup == null) return;
            var dc = (sender as FrameworkElement)?.DataContext;
            string key = null;
            if (dc is PinnedFieldEntry pfe) key = pfe.Key;
            else if (dc is FieldItem fi)    key = fi.Key;
            if (!string.IsNullOrEmpty(key))
                _activeTargetFieldPickerGroup.ToggleTargetFieldPin(key);
            e.Handled = true;
        }

        private void TargetFieldPickerGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            var group = (sender as FrameworkElement)?.DataContext as FieldSelectorGroupVm;
            group?.ToggleCollapseCommand?.Execute(null);
        }

        private void TfFavoritenDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedTfFavoritenEntry != null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _draggedTfFavoritenEntry = entry;
            DragDrop.DoDragDrop(
                (DependencyObject)sender,
                new DataObject("TfFavoritenEntry", entry),
                DragDropEffects.Move);
            _draggedTfFavoritenEntry = null;
        }

        private void TfFavoritenItem_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("TfFavoritenEntry")
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void TfFavoritenItem_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("TfFavoritenEntry") || _activeTargetFieldPickerGroup == null) return;
            var src = e.Data.GetData("TfFavoritenEntry") as PinnedFieldEntry;
            var tgt = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (src == null || tgt == null || src.Key == tgt.Key) return;
            _activeTargetFieldPickerGroup.ReorderTargetFieldPin(src.Key, tgt.Key);
            e.Handled = true;
        }

        private void TargetFieldResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_targetFieldPopupBorder == null) return;
            double newH = _targetFieldPopupBorder.ActualHeight + e.VerticalChange;
            _targetFieldPopupBorder.MaxHeight = double.PositiveInfinity;
            _targetFieldPopupBorder.Height    = Math.Max(80, newH);
        }

        // ══════════════════════════════════════════════
        //  CARD FIELD PICKER (P3-C)
        // ══════════════════════════════════════════════

        private Border           _cardFieldPopupBorder;
        private double           _cardFieldDropdownHeight;
        private CardRowVm        _activeCardFieldPickerCard;
        private PinnedFieldEntry _draggedCfFavoritenEntry;

        private void CardFieldPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            var c = (sender as FrameworkElement)?.DataContext as CardRowVm;
            c?.OpenCardFieldPicker();
        }

        private void CardFieldPickerPopup_Opened(object sender, EventArgs e)
        {
            _isTargetFieldDropDownOpen = true;
            var popup = sender as System.Windows.Controls.Primitives.Popup;
            if (popup == null) return;
            _activeCardFieldPickerCard = (popup.PlacementTarget as FrameworkElement)?.DataContext as CardRowVm;
            _cardFieldPopupBorder = popup.Child as Border;
            if (_cardFieldPopupBorder != null && _cardFieldDropdownHeight > 0)
            {
                _cardFieldPopupBorder.MaxHeight = double.PositiveInfinity;
                _cardFieldPopupBorder.Height    = _cardFieldDropdownHeight;
            }
        }

        private void CardFieldPickerPopup_Closed(object sender, EventArgs e)
        {
            _isTargetFieldDropDownOpen = false;
            _activeCardFieldPickerCard?.OnCardFieldPickerClosed();
            if (_cardFieldPopupBorder != null && _cardFieldPopupBorder.ActualHeight > 0)
                _cardFieldDropdownHeight = _cardFieldPopupBorder.ActualHeight;
            _cardFieldPopupBorder = null;
            _activeCardFieldPickerCard = null;
        }

        private void CardFieldPickerItem_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCardFieldPickerCard == null) return;
            var item = (sender as FrameworkElement)?.DataContext as FieldItem;
            if (item == null) return;
            _activeCardFieldPickerCard.CardFieldPickerKey = item.Key;
            _activeCardFieldPickerCard.IsCardFieldPickerOpen = false;
        }

        private void CardFieldPickerPinnedItem_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCardFieldPickerCard == null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _activeCardFieldPickerCard.CardFieldPickerKey = entry.Key;
            _activeCardFieldPickerCard.IsCardFieldPickerOpen = false;
        }

        private void CardFieldPickerItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_activeCardFieldPickerCard == null) return;
            var dc = (sender as FrameworkElement)?.DataContext;
            string key = null;
            if (dc is PinnedFieldEntry pfe) key = pfe.Key;
            else if (dc is FieldItem fi)    key = fi.Key;
            if (!string.IsNullOrEmpty(key))
                _activeCardFieldPickerCard.ToggleCardFieldPin(key);
            e.Handled = true;
        }

        private void CardFieldPickerGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            var group = (sender as FrameworkElement)?.DataContext as FieldSelectorGroupVm;
            group?.ToggleCollapseCommand?.Execute(null);
        }

        private void CfFavoritenDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedCfFavoritenEntry != null) return;
            var entry = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (entry == null) return;
            _draggedCfFavoritenEntry = entry;
            DragDrop.DoDragDrop(
                (DependencyObject)sender,
                new DataObject("CfFavoritenEntry", entry),
                DragDropEffects.Move);
            _draggedCfFavoritenEntry = null;
        }

        private void CfFavoritenItem_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("CfFavoritenEntry")
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void CfFavoritenItem_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("CfFavoritenEntry") || _activeCardFieldPickerCard == null) return;
            var src = e.Data.GetData("CfFavoritenEntry") as PinnedFieldEntry;
            var tgt = (sender as FrameworkElement)?.DataContext as PinnedFieldEntry;
            if (src == null || tgt == null || src.Key == tgt.Key) return;
            _activeCardFieldPickerCard.ReorderCardFieldPin(src.Key, tgt.Key);
            e.Handled = true;
        }

        private void CardFieldResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_cardFieldPopupBorder == null) return;
            double newH = _cardFieldPopupBorder.ActualHeight + e.VerticalChange;
            _cardFieldPopupBorder.MaxHeight = double.PositiveInfinity;
            _cardFieldPopupBorder.Height    = Math.Max(80, newH);
        }

    }
}
