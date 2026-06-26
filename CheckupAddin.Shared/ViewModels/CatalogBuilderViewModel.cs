using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using static CheckupAddIn.Services.CardEngine;

// Aliases to reduce verbosity when both store types are in scope.
using CapStore = CheckupAddIn.Services.CapabilityStore;

namespace CheckupAddIn.ViewModels
{
    /// <summary>
    /// A single editable row in the catalog DataGrid.
    /// Wraps the underlying CatalogEntry.Values dictionary with a string indexer that
    /// fires PropertyChanged("Item[]") so WPF DataGrid bindings refresh correctly.
    /// OnChanged is called by the ViewModel to detect in-cell edits for dirty tracking.
    /// </summary>
    public class EntryRow : INotifyPropertyChanged
    {
        private readonly Dictionary<string, string> _data;
        internal Action OnChanged;

        public EntryRow(Dictionary<string, string> data) => _data = data;

        public string this[string key]
        {
            get => _data.TryGetValue(key, out var v) ? v : "";
            set
            {
                _data[key] = value ?? "";
                OnPropertyChanged(Binding.IndexerName);
                OnChanged?.Invoke();
            }
        }

        public Dictionary<string, string> Data => _data;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// ViewModel for the Catalog Builder window.
    ///
    /// All entry/column edits go to a deep-copy _workingCopy of the selected catalog.
    /// The original object in the store is only overwritten when the user explicitly saves.
    /// Switching catalogs or closing the window while dirty triggers a Save/Discard/Cancel prompt
    /// via the PromptUnsavedChanges delegate.
    ///
    /// Catalog-level operations (New, Rename, Delete, Import, Export) are immediate and save
    /// to disk right away, because they affect catalog identity / membership rather than entry data.
    ///
    /// Sorting does NOT set IsDirty — it is a view-order operation only.
    /// </summary>
    public class CatalogBuilderViewModel : INotifyPropertyChanged
    {
        private readonly CatalogStore    _store;
        private readonly CapabilityStore _capStore;
        private readonly List<FieldItem> _availableTargetFields = new();


        // ── Active tab ──
        private bool _isCapabilitiesTab;
        public bool IsCatalogsTab     => !_isCapabilitiesTab;
        public bool IsCapabilitiesTab =>  _isCapabilitiesTab;

        private void SetTab(bool capabilities)
        {
            if (_isCapabilitiesTab == capabilities) return;
            _isCapabilitiesTab = capabilities;
            UiStateStore.SaveCatalogBuilderActiveTab(capabilities);
            OnPropertyChanged(nameof(IsCatalogsTab));
            OnPropertyChanged(nameof(IsCapabilitiesTab));
        }

        // ── Catalogs list ──
        public ObservableCollection<CatalogData> Catalogs { get; } = new();

        // _selectedCatalog is the original object from the store — untouched until CommitAndSave.
        // _workingCopy is the mutable clone all edits go to.
        private CatalogData _selectedCatalog;
        private CatalogData _workingCopy;

        private bool _isDirty;
        public bool IsDirty
        {
            get => _isDirty;
            private set { _isDirty = value; OnPropertyChanged(); }
        }

        // Sort state — persisted for toggle behaviour within a session
        private string _lastSortKey;
        private bool _lastSortAscending = true;

        public CatalogData SelectedCatalog
        {
            get => _selectedCatalog;
            set
            {
                if (value == _selectedCatalog) return;
                if (value != null) UiStateStore.SaveLastCatalogId(value.Id);

                // If switching away from a dirty catalog, prompt the user
                if (_isDirty && _selectedCatalog != null)
                {
                    var choice = PromptUnsavedChanges?.Invoke();   // true=save, false=discard, null=cancel
                    if (choice == null)
                    {
                        // User chose Cancel → keep the current catalog selected
                        OnPropertyChanged(nameof(SelectedCatalog));
                        return;
                    }
                    if (choice == true) CommitAndSave();
                    // false → just abandon the working copy
                }

                _selectedCatalog = value;
                _workingCopy     = value != null ? DeepClone(value) : null;
                _isDirty         = false;
                _lastSortKey     = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedCatalog));
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(IsSelectedCatalogOnUncPath));
                OnPropertyChanged(nameof(IsSelectedCatalogLocked));
                OnPropertyChanged(nameof(IsSelectedCatalogEditable));
                OnPropertyChanged(nameof(IsSelectedCatalogUpdateAvailable));
                OnPropertyChanged(nameof(IsSelectedCatalogLockedNoUpdate));
                OnPropertyChanged(nameof(HasEditableSelectedColumn));
                RefreshCurrentColumns();
                RefreshEntries();
                ColumnsChanged?.Invoke();
            }
        }

        public bool HasSelectedCatalog              => _workingCopy != null;
        /// <summary>True when the selected catalog's file is on a UNC path (🌐 icon).</summary>
        public bool IsSelectedCatalogOnUncPath      => _selectedCatalog?.IsOnUncPath == true;
        /// <summary>True when the catalog is locked (UNC-sourced or IsLocked flag set).</summary>
        public bool IsSelectedCatalogLocked         => _selectedCatalog?.IsOnUncPath == true
                                                       || _selectedCatalog?.IsLocked == true;
        /// <summary>True when a catalog is selected AND it is not locked — gates all editing commands.</summary>
        public bool IsSelectedCatalogEditable       => HasSelectedCatalog && !IsSelectedCatalogLocked;
        /// <summary>True when the distribution version of the selected catalog is newer than the local AppData copy.</summary>
        public bool IsSelectedCatalogUpdateAvailable => _selectedCatalog?.HasUpdateAvailable == true;
        /// <summary>True when locked but no update is pending — shows the normal Unlock button.</summary>
        public bool IsSelectedCatalogLockedNoUpdate  => IsSelectedCatalogLocked && !IsSelectedCatalogUpdateAvailable;

        // ── Capability sets ──
        public ObservableCollection<CapabilitySet> CapabilitySets { get; } = new();

        private CapabilitySet _selectedCapabilitySet;
        public CapabilitySet SelectedCapabilitySet
        {
            get => _selectedCapabilitySet;
            set
            {
                _selectedCapabilitySet = value;
                if (value != null) UiStateStore.SaveLastCapabilitySetId(value.Id);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedCapabilitySet));
                OnPropertyChanged(nameof(HasNoSelectedCapabilitySet));
                OnPropertyChanged(nameof(IsSelectedCapSetOnUncPath));
                OnPropertyChanged(nameof(IsSelectedCapSetLocked));
                OnPropertyChanged(nameof(IsSelectedCapSetEditable));
                OnPropertyChanged(nameof(IsSelectedCapSetUpdateAvailable));
                OnPropertyChanged(nameof(IsSelectedCapSetLockedNoUpdate));
                OnPropertyChanged(nameof(HasEditableActiveGroup));
                RebuildCapSetGroups();
            }
        }

        public bool HasSelectedCapabilitySet         => _selectedCapabilitySet != null;
        public bool HasNoSelectedCapabilitySet      => _selectedCapabilitySet == null;
        /// <summary>True when the active capability set has at least one Expert group. Drives the info strip.</summary>
        public bool HasAnyExpertGroup               => CapSetGroups.Any(g => g.IsExpert);
        /// <summary>True when the selected set's file is on a UNC path (🌐 icon).</summary>
        public bool IsSelectedCapSetOnUncPath       => _selectedCapabilitySet?.IsOnUncPath == true;
        /// <summary>True when the set is locked (UNC-sourced or IsLocked flag set).</summary>
        public bool IsSelectedCapSetLocked          => _selectedCapabilitySet?.IsOnUncPath == true
                                                       || _selectedCapabilitySet?.IsLocked == true;
        /// <summary>True when a capability set is selected AND it is not locked — gates all editing commands.</summary>
        public bool IsSelectedCapSetEditable        => HasSelectedCapabilitySet && !IsSelectedCapSetLocked;
        /// <summary>True when the distribution version of the selected capability set is newer than the local AppData copy.</summary>
        public bool IsSelectedCapSetUpdateAvailable  => _selectedCapabilitySet?.HasUpdateAvailable == true;
        /// <summary>True when locked but no update is pending — shows the normal Unlock button.</summary>
        public bool IsSelectedCapSetLockedNoUpdate   => IsSelectedCapSetLocked && !IsSelectedCapSetUpdateAvailable;

        /// <summary>All selectable target fields (excludes SPECIAL:LOGIC:* to prevent self-reference).</summary>
        public IReadOnlyList<FieldItem> AvailableTargetFields => _availableTargetFields;

        /// <summary>Same as AvailableTargetFields but wrapped in a ListCollectionView with group headers,
        /// so the Zielfeld ComboBox matches the grouped field-selector in the main Checkup window.</summary>
        public ListCollectionView GroupedAvailableTargetFields { get; private set; }

        // ── Columns ──
        public ObservableCollection<CatalogColumn> CurrentColumns { get; } = new();

        private CatalogColumn _selectedColumn;
        public CatalogColumn SelectedColumn
        {
            get => _selectedColumn;
            set
            {
                _selectedColumn = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedColumn));
                OnPropertyChanged(nameof(HasEditableSelectedColumn));
                OnPropertyChanged(nameof(SelectedColumnRole));
            }
        }

        public bool HasSelectedColumn        => _selectedColumn != null;
        public bool HasEditableSelectedColumn => HasSelectedColumn && IsSelectedCatalogEditable;

        // Role items for the role ComboBox — base types only (no numbered variants).
        // Multiple columns can share the same role type; the VM auto-assigns RoleIndex.
        public static ColumnRoleItem[] ColumnRoleItems { get; } = new[]
        {
            new ColumnRoleItem(ColumnRole.None,             "—   None",              "Keine besondere Funktion. Hilfsspalte oder interne Kennung."),
            new ColumnRoleItem(ColumnRole.PrimaryDisplay,   "PRI  PrimaryDisplay",   "Kurzform (Feld 1): z. B. der Token in SPEZIFIK 1."),
            new ColumnRoleItem(ColumnRole.SecondaryDisplay, "SEC  SecondaryDisplay", "Langform (Feld 2): z. B. der Token in SPEZIFIK 2."),
            new ColumnRoleItem(ColumnRole.TabId,            "TAB  TabId",            "Reiter-Kennung: jeder eindeutige Wert wird ein Tab in der Auswahlmaske. Mehrere durch Komma getrennte Werte listen die Zeile unter mehreren Tabs."),
            new ColumnRoleItem(ColumnRole.GroupId,          "GRP  GroupId",          "Untergruppen-Überschrift innerhalb eines Tabs (nicht der Reiter-Titel): Einträge mit gleichem Wert werden unter einer Überschrift gruppiert."),
            new ColumnRoleItem(ColumnRole.SortKey,          "SRT  SortKey",          "Sortierung der Einträge innerhalb einer Gruppe; mehrfach → SRT1, SRT2 …"),
            new ColumnRoleItem(ColumnRole.GroupSortKey,     "GST  GroupSortKey",     "Sortierung der Gruppen innerhalb eines Tabs; mehrfach → GST1, GST2 …"),
            new ColumnRoleItem(ColumnRole.TabSortKey,       "TST  TabSortKey",       "Sortierung der Tabs in der Auswahlmaske; mehrfach → TST1, TST2 …"),
            new ColumnRoleItem(ColumnRole.Auxiliary,        "AUX  Auxiliary",        "Hilfsdaten: z. B. Tooltip-Text — wird nicht in Felder geschrieben."),
            new ColumnRoleItem(ColumnRole.Generation,       "GEN  Generation",       "Generations-Marker (T43): die Lesart eines Werts hängt von der aktiven Generation ab; leere Zelle = universell (gilt für alle Generationen)."),
        };

        public ColumnRole SelectedColumnRole
        {
            get => _selectedColumn?.Role ?? ColumnRole.None;
            set
            {
                if (_selectedColumn == null) return;
                var oldRole = _selectedColumn.Role;
                if (oldRole == value) return;

                // Clear this column's role and compact the old type's indices
                _selectedColumn.Role      = ColumnRole.None;
                _selectedColumn.RoleIndex = 1;
                if (oldRole != ColumnRole.None)
                    CompactRoleIndices(oldRole);

                // Assign new role with next available index
                if (value != ColumnRole.None)
                {
                    int maxIdx = _workingCopy.Columns
                        .Where(c => c.Role == value)
                        .Select(c => c.RoleIndex)
                        .DefaultIfEmpty(0)
                        .Max();
                    _selectedColumn.Role      = value;
                    _selectedColumn.RoleIndex = maxIdx + 1;
                }

                IsDirty = true;
                OnPropertyChanged();
            }
        }

        private void CompactRoleIndices(ColumnRole role)
        {
            var cols = _workingCopy?.Columns
                .Where(c => c.Role == role)
                .OrderBy(c => c.RoleIndex)
                .ToList();
            if (cols == null) return;
            for (int i = 0; i < cols.Count; i++)
                cols[i].RoleIndex = i + 1;
        }

        /// <summary>
        /// Swaps this column's RoleIndex with the adjacent same-type column in the given direction
        /// (−1 = move up / higher priority, +1 = move down / lower priority).
        /// Fires RoleIndicesChanged so code-behind can refresh all header badges.
        /// </summary>
        public void MoveRoleIndex(CatalogColumn col, int direction)
        {
            if (_workingCopy == null || col == null || col.Role == ColumnRole.None) return;
            var sameType = _workingCopy.Columns
                .Where(c => c.Role == col.Role)
                .OrderBy(c => c.RoleIndex)
                .ToList();
            int pos = sameType.IndexOf(col);
            if (pos < 0) return;
            int targetPos = pos + direction;
            if (targetPos < 0 || targetPos >= sameType.Count) return;
            (sameType[pos].RoleIndex, sameType[targetPos].RoleIndex) =
                (sameType[targetPos].RoleIndex, sameType[pos].RoleIndex);
            IsDirty = true;
            RoleIndicesChanged?.Invoke();
        }

        /// <summary>Fired when any column's RoleIndex changes (e.g. Move Up/Down) so badges refresh.</summary>
        public event Action RoleIndicesChanged;

        // ── Entries ──
        public ObservableCollection<EntryRow> EntryRows { get; } = new();

        private EntryRow _selectedEntry;
        public EntryRow SelectedEntry
        {
            get => _selectedEntry;
            set { _selectedEntry = value; OnPropertyChanged(); }
        }

        // ── Fired when the column schema changes — code-behind rebuilds DataGrid columns ──
        public event Action ColumnsChanged;

        // ── Dialog delegates (set by code-behind, keep ViewModel testable) ──
        public Func<string, string, string> AskForText { get; set; }  // (title, initial) → text or null
        public Func<bool>                   ConfirmDelete            { get; set; }
        public Func<bool?>                  PromptUnsavedChanges     { get; set; }  // true=save, false=discard, null=cancel
        public Func<string>                 PickSaveFile             { get; set; }
        public Func<string>                 PickOpenFile             { get; set; }
        public Func<string>                 PickCapSetSaveFile       { get; set; }
        public Func<string>                 PickCapSetOpenFile       { get; set; }

        // ── Commands — catalogs ──
        public RelayCommand NewCatalogCommand    { get; }
        public RelayCommand RenameCatalogCommand { get; }
        public RelayCommand DeleteCatalogCommand { get; }
        public RelayCommand ExportCatalogCommand { get; }
        public RelayCommand ImportCatalogCommand { get; }
        public RelayCommand AddEntryCommand      { get; }
        public RelayCommand RemoveEntryCommand   { get; }
        public RelayCommand AddColumnCommand     { get; }
        public RelayCommand EditColumnCommand    { get; }
        public RelayCommand DeleteColumnCommand  { get; }
        public RelayCommand SaveCommand          { get; }

        // ── Commands — tab switching ──
        public RelayCommand SwitchToCatalogsCommand      { get; }
        public RelayCommand SwitchToCapabilitiesCommand  { get; }

        // ── Groups for the selected capability set ──
        public ObservableCollection<CardGroupVm> CapSetGroups { get; } = new();

        public bool HasCapSetGroups   => CapSetGroups.Count > 0;
        public bool HasNoCapSetGroups => CapSetGroups.Count == 0;

        // ── Active selection state (drives global ▲▼⧉× toolbar) ────────────────
        private CardGroupVm _activeGroupVm;
        private CardRowVm   _activeCardVm;

        public CardGroupVm ActiveGroupVm
        {
            get => _activeGroupVm;
            private set
            {
                _activeGroupVm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveGroup));
                OnPropertyChanged(nameof(HasEditableActiveGroup));
            }
        }

        public CardRowVm ActiveCardVm
        {
            get => _activeCardVm;
            private set { _activeCardVm = value; OnPropertyChanged(); }
        }

        public bool HasActiveGroup        => _activeGroupVm != null;
        public bool HasEditableActiveGroup => HasActiveGroup && IsSelectedCapSetEditable;

        private bool _isBasicLogicsPanelOpen;
        private bool _isCardPanelOpen;
        public bool IsBasicLogicsPanelOpen
        {
            get => _isBasicLogicsPanelOpen;
            set { _isBasicLogicsPanelOpen = value; UiStateStore.SaveCatalogBuilderBasicLogicsPanel(value); OnPropertyChanged(); }
        }

        public bool IsCardPanelOpen
        {
            get => _isCardPanelOpen;
            set { _isCardPanelOpen = value; UiStateStore.SaveCatalogBuilderCardPanel(value); OnPropertyChanged(); }
        }

        // ── Commands — capability sets ──
        public RelayCommand NewCapSetCommand         { get; }
        public RelayCommand RenameCapSetCommand      { get; }
        public RelayCommand DeleteCapSetCommand      { get; }
        public RelayCommand ExportCapSetCommand      { get; }
        public RelayCommand ImportCapSetCommand      { get; }
        public RelayCommand AddGroupCommand          { get; }
        public RelayCommand ToggleCatalogLockCommand { get; }
        public RelayCommand ToggleCapSetLockCommand  { get; }
        public RelayCommand UpdateCatalogCommand     { get; }
        public RelayCommand UpdateCapSetCommand      { get; }

        /// <summary>Set by the code-behind to show an OK/Cancel confirmation dialog. Returns true on OK.</summary>
        public Func<bool> ConfirmUpdateCatalog { get; set; }
        /// <summary>Set by the code-behind to show an OK/Cancel confirmation dialog. Returns true on OK.</summary>
        public Func<bool> ConfirmUpdateCapSet  { get; set; }

        // ── Commands — global active-item actions ──
        public RelayCommand MoveActiveUpCommand          { get; }
        public RelayCommand MoveActiveDownCommand        { get; }
        public RelayCommand DuplicateActiveCommand       { get; }
        public RelayCommand RemoveActiveCommand          { get; }
        public RelayCommand AddDropdownToActiveCommand    { get; }
        public RelayCommand AddSyncToActiveCommand        { get; }
        public RelayCommand AddLinkToActiveCommand        { get; }
        public RelayCommand AddButtonToActiveCommand      { get; }
        public RelayCommand AddSearchToActiveCommand      { get; }
        public RelayCommand AddMultiPickToActiveCommand       { get; }
        public RelayCommand AddPairTransformToActiveCommand   { get; }
        public RelayCommand AddPrefixSuffixToActiveCommand    { get; }
        public RelayCommand AddSortToActiveCommand             { get; }
        public RelayCommand AddComposeToActiveCommand          { get; }
        // Basic Logic template commands — each adds a BL card pre-filled with a formula skeleton.
        public RelayCommand AddConcatenateToActiveCommand  { get; }
        public RelayCommand AddIfElseToActiveCommand       { get; }
        public RelayCommand AddLookupToActiveCommand       { get; }
        public RelayCommand AddFormatToActiveCommand       { get; }
        public RelayCommand AddRoundToActiveCommand        { get; }
        public RelayCommand AddValueToActiveCommand        { get; }
        public RelayCommand AddStrToActiveCommand          { get; }
        public RelayCommand AddEqToActiveCommand           { get; }
        public RelayCommand AddNeToActiveCommand           { get; }
        public RelayCommand AddLtToActiveCommand           { get; }
        public RelayCommand AddGtToActiveCommand           { get; }
        public RelayCommand AddLteToActiveCommand          { get; }
        public RelayCommand AddGteToActiveCommand          { get; }
        public RelayCommand AddAndToActiveCommand          { get; }
        public RelayCommand AddOrToActiveCommand           { get; }
        public RelayCommand AddNotToActiveCommand          { get; }
        public RelayCommand AddJoinToActiveCommand         { get; }
        public RelayCommand AddLeftToActiveCommand         { get; }
        public RelayCommand AddRightToActiveCommand        { get; }
        public RelayCommand AddMidToActiveCommand          { get; }
        public RelayCommand AddTrimToActiveCommand         { get; }
        public RelayCommand AddUpperToActiveCommand        { get; }
        public RelayCommand AddLowerToActiveCommand        { get; }
        public RelayCommand AddReplaceToActiveCommand      { get; }
        public RelayCommand AddAbsToActiveCommand          { get; }
        public RelayCommand AddLenToActiveCommand          { get; }
        public RelayCommand AddContainsToActiveCommand     { get; }
        public RelayCommand AddStartsWithToActiveCommand   { get; }
        public RelayCommand AddEndsWithToActiveCommand     { get; }
        public RelayCommand AddIsEmptyToActiveCommand      { get; }
        public RelayCommand AddDefaultToActiveCommand      { get; }
        public RelayCommand ToggleBasicLogicsPanelCommand  { get; }
        public RelayCommand ToggleCardPanelCommand         { get; }

        public CatalogBuilderViewModel(CatalogStore store, CapabilityStore capStore = null,
                                       IReadOnlyList<FieldItem> availableFields = null)
        {
            _store    = store;
            _capStore = capStore;
            foreach (var c in store.Catalogs) Catalogs.Add(c);
            if (capStore != null)
                foreach (var s in capStore.CapabilitySets) CapabilitySets.Add(s);

            // Build target-field list: all fields except the empty sentinel and action items.
            // SPECIAL:LOGIC:* entries are included so Link cards can reference other Logic groups.
            if (availableFields != null)
            {
                foreach (var f in availableFields)
                {
                    if (string.IsNullOrEmpty(f.Key)) continue;
                    if (f.IsActionItem) continue;
                    _availableTargetFields.Add(f);
                }
            }

            var grouped = new ListCollectionView(_availableTargetFields);
            grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldItem.GroupName)));
            GroupedAvailableTargetFields = grouped;

            // Restore last selections (by ID) — silent no-op when ID is not found.
            string lastCatId = UiStateStore.LoadLastCatalogId();
            if (!string.IsNullOrEmpty(lastCatId))
                _selectedCatalog = Catalogs.FirstOrDefault(c => c.Id == lastCatId);
            if (_selectedCatalog != null)
            {
                _workingCopy = DeepClone(_selectedCatalog);
                RefreshCurrentColumns();
                RefreshEntries();
            }

            string lastCapId = UiStateStore.LoadLastCapabilitySetId();
            if (!string.IsNullOrEmpty(lastCapId))
                _selectedCapabilitySet = CapabilitySets.FirstOrDefault(s => s.Id == lastCapId);

            _isCapabilitiesTab      = UiStateStore.LoadCatalogBuilderActiveTab();
            _isBasicLogicsPanelOpen = UiStateStore.LoadCatalogBuilderBasicLogicsPanel();
            _isCardPanelOpen        = UiStateStore.LoadCatalogBuilderCardPanel();

            NewCatalogCommand    = new RelayCommand(NewCatalog);
            RenameCatalogCommand = new RelayCommand(RenameCatalog,  () => IsSelectedCatalogEditable);
            DeleteCatalogCommand = new RelayCommand(DeleteCatalog,  () => IsSelectedCatalogEditable);
            ExportCatalogCommand = new RelayCommand(ExportCatalog,  () => HasSelectedCatalog);
            ImportCatalogCommand = new RelayCommand(ImportCatalog);
            AddEntryCommand      = new RelayCommand(AddEntry,       () => IsSelectedCatalogEditable);
            RemoveEntryCommand   = new RelayCommand(RemoveEntry,    () => IsSelectedCatalogEditable);
            AddColumnCommand     = new RelayCommand(AddColumn,      () => IsSelectedCatalogEditable);
            EditColumnCommand    = new RelayCommand(EditColumn,     () => IsSelectedCatalogEditable && _selectedColumn != null);
            DeleteColumnCommand  = new RelayCommand(DeleteColumn,   () => IsSelectedCatalogEditable);
            SaveCommand          = new RelayCommand(DoSave,         () => IsSelectedCatalogEditable);

            SwitchToCatalogsCommand     = new RelayCommand(() => SetTab(false));
            SwitchToCapabilitiesCommand = new RelayCommand(() => SetTab(true));

            NewCapSetCommand    = new RelayCommand(NewCapSet);
            RenameCapSetCommand = new RelayCommand(RenameCapSet, () => IsSelectedCapSetEditable);
            DeleteCapSetCommand = new RelayCommand(DeleteCapSet, () => IsSelectedCapSetEditable);
            ExportCapSetCommand = new RelayCommand(ExportCapSet, () => HasSelectedCapabilitySet);
            ImportCapSetCommand = new RelayCommand(ImportCapSet);
            AddGroupCommand     = new RelayCommand(AddGroup,     () => IsSelectedCapSetEditable);

            ToggleCatalogLockCommand = new RelayCommand(
                () =>
                {
                    if (_selectedCatalog == null) return;
                    if (_selectedCatalog.IsOnUncPath)
                    {
                        // Unlock = copy to AppData; file becomes local and unlocked.
                        _store.UnlockToLocal(_selectedCatalog);
                    }
                    else
                    {
                        _selectedCatalog.IsLocked = !_selectedCatalog.IsLocked;
                        _store.Save(_selectedCatalog);
                    }
                    OnPropertyChanged(nameof(IsSelectedCatalogOnUncPath));
                    OnPropertyChanged(nameof(IsSelectedCatalogLocked));
                    OnPropertyChanged(nameof(IsSelectedCatalogEditable));
                    OnPropertyChanged(nameof(IsSelectedCatalogUpdateAvailable));
                    OnPropertyChanged(nameof(IsSelectedCatalogLockedNoUpdate));
                    OnPropertyChanged(nameof(HasEditableSelectedColumn));
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => _selectedCatalog != null);

            ToggleCapSetLockCommand = new RelayCommand(
                () =>
                {
                    if (_selectedCapabilitySet == null) return;
                    if (_selectedCapabilitySet.IsOnUncPath)
                    {
                        _capStore?.UnlockToLocal(_selectedCapabilitySet);
                    }
                    else
                    {
                        _selectedCapabilitySet.IsLocked = !_selectedCapabilitySet.IsLocked;
                        _capStore?.Save(_selectedCapabilitySet);
                    }
                    OnPropertyChanged(nameof(IsSelectedCapSetOnUncPath));
                    OnPropertyChanged(nameof(IsSelectedCapSetLocked));
                    OnPropertyChanged(nameof(IsSelectedCapSetEditable));
                    OnPropertyChanged(nameof(IsSelectedCapSetUpdateAvailable));
                    OnPropertyChanged(nameof(IsSelectedCapSetLockedNoUpdate));
                    OnPropertyChanged(nameof(HasEditableActiveGroup));
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => _selectedCapabilitySet != null);

            UpdateCatalogCommand = new RelayCommand(
                () =>
                {
                    if (_selectedCatalog == null) return;
                    if (ConfirmUpdateCatalog?.Invoke() != true) return;
                    var newCatalog = _store.RevertToDistribution(_selectedCatalog);
                    if (newCatalog == null) return;
                    int idx = Catalogs.IndexOf(_selectedCatalog);
                    if (idx >= 0) Catalogs[idx] = newCatalog;
                    else Catalogs.Add(newCatalog);
                    _isDirty = false;
                    _selectedCatalog = null;
                    SelectedCatalog = newCatalog;
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => IsSelectedCatalogUpdateAvailable);

            UpdateCapSetCommand = new RelayCommand(
                () =>
                {
                    if (_selectedCapabilitySet == null) return;
                    if (ConfirmUpdateCapSet?.Invoke() != true) return;
                    var newSet = _capStore?.RevertToDistribution(_selectedCapabilitySet);
                    if (newSet == null) return;
                    int idx = CapabilitySets.IndexOf(_selectedCapabilitySet);
                    if (idx >= 0) CapabilitySets[idx] = newSet;
                    else CapabilitySets.Add(newSet);
                    _selectedCapabilitySet = null;
                    SelectedCapabilitySet = newSet;
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => IsSelectedCapSetUpdateAvailable);

            MoveActiveUpCommand = new RelayCommand(
                () =>
                {
                    if (_activeCardVm != null && _activeGroupVm != null)
                    {
                        int cardIdx = _activeGroupVm.Cards.IndexOf(_activeCardVm);
                        if (cardIdx > 0)
                        {
                            _activeGroupVm.MoveCardUp(_activeCardVm);
                        }
                        else
                        {
                            int groupIdx = CapSetGroups.IndexOf(_activeGroupVm);
                            if (groupIdx > 0)
                            {
                                var target = CapSetGroups[groupIdx - 1];
                                // V1: cross-group card move must stay within same Normal/Expert section
                                if (target.IsExpert != _activeGroupVm.IsExpert) return;
                                var card = _activeCardVm.Card;
                                _activeGroupVm.RemoveCard(_activeCardVm);
                                target.InsertCardAt(card, target.Cards.Count);
                                OnGroupCardActivated(target, target.Cards[target.Cards.Count - 1]);
                            }
                        }
                    }
                    else if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        // V1: block move across Normal/Expert section boundary
                        if (idx > 0 && CapSetGroups[idx - 1].IsExpert == _activeGroupVm.IsExpert)
                            OnGroupDragDropCompleted(idx, idx - 1);
                    }
                },
                () =>
                {
                    if (!IsSelectedCapSetEditable) return false;
                    if (_activeCardVm != null && _activeGroupVm != null)
                    {
                        int cardIdx = _activeGroupVm.Cards.IndexOf(_activeCardVm);
                        if (cardIdx > 0) return true;
                        return CapSetGroups.IndexOf(_activeGroupVm) > 0;
                    }
                    if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        // V1: must have a previous group in the SAME section (no cross-boundary moves)
                        return idx > 0 && CapSetGroups[idx - 1].IsExpert == _activeGroupVm.IsExpert;
                    }
                    return false;
                });

            MoveActiveDownCommand = new RelayCommand(
                () =>
                {
                    if (_activeCardVm != null && _activeGroupVm != null)
                    {
                        int cardIdx = _activeGroupVm.Cards.IndexOf(_activeCardVm);
                        if (cardIdx < _activeGroupVm.Cards.Count - 1)
                        {
                            _activeGroupVm.MoveCardDown(_activeCardVm);
                        }
                        else
                        {
                            int groupIdx = CapSetGroups.IndexOf(_activeGroupVm);
                            if (groupIdx < CapSetGroups.Count - 1)
                            {
                                var target = CapSetGroups[groupIdx + 1];
                                // V1: cross-group card move must stay within same Normal/Expert section
                                if (target.IsExpert != _activeGroupVm.IsExpert) return;
                                var card = _activeCardVm.Card;
                                _activeGroupVm.RemoveCard(_activeCardVm);
                                target.InsertCardAt(card, 0);
                                OnGroupCardActivated(target, target.Cards[0]);
                            }
                        }
                    }
                    else if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        // V1: block move across Normal/Expert section boundary
                        if (idx >= 0 && idx < CapSetGroups.Count - 1
                            && CapSetGroups[idx + 1].IsExpert == _activeGroupVm.IsExpert)
                            OnGroupDragDropCompleted(idx, idx + 1);
                    }
                },
                () =>
                {
                    if (!IsSelectedCapSetEditable) return false;
                    if (_activeCardVm != null && _activeGroupVm != null)
                    {
                        int cardIdx = _activeGroupVm.Cards.IndexOf(_activeCardVm);
                        if (cardIdx >= 0 && cardIdx < _activeGroupVm.Cards.Count - 1) return true;
                        return CapSetGroups.IndexOf(_activeGroupVm) < CapSetGroups.Count - 1;
                    }
                    if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        // V1: must have a next group in the SAME section (no cross-boundary moves)
                        return idx >= 0 && idx < CapSetGroups.Count - 1
                            && CapSetGroups[idx + 1].IsExpert == _activeGroupVm.IsExpert;
                    }
                    return false;
                });

            DuplicateActiveCommand = new RelayCommand(
                () =>
                {
                    if (_activeCardVm != null && _activeGroupVm != null)
                        _activeGroupVm.DuplicateCard(_activeCardVm);
                    else if (_activeGroupVm != null)
                        DuplicateGroup(_activeGroupVm);
                },
                () => _activeGroupVm != null && IsSelectedCapSetEditable);

            RemoveActiveCommand = new RelayCommand(
                () =>
                {
                    if (_activeCardVm != null && _activeGroupVm != null)
                    {
                        var toRemove = _activeCardVm;
                        ActiveCardVm = null;
                        _activeGroupVm.RemoveCard(toRemove);
                        RelayCommand.RaiseCanExecuteChanged();
                    }
                    else if (_activeGroupVm != null)
                    {
                        var toRemove = _activeGroupVm;
                        OnGroupCardActivated(null, null);
                        RemoveGroupVm(toRemove);
                    }
                },
                () => _activeGroupVm != null && IsSelectedCapSetEditable);

            AddDropdownToActiveCommand  = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeDropdown), () => HasEditableActiveGroup);
            AddSyncToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSync),     () => HasEditableActiveGroup);
            AddLinkToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeLink),     () => HasEditableActiveGroup);
            AddButtonToActiveCommand    = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeButton),   () => HasEditableActiveGroup);
            AddSearchToActiveCommand    = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSearch),   () => HasEditableActiveGroup);
            AddMultiPickToActiveCommand     = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeMultiPick),     () => HasEditableActiveGroup);
            AddPairTransformToActiveCommand = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypePairTransform), () => HasEditableActiveGroup);
            AddPrefixSuffixToActiveCommand  = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypePrefixSuffix),  () => HasEditableActiveGroup);
            AddSortToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSort),          () => HasEditableActiveGroup);
            AddComposeToActiveCommand       = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeCompose),       () => HasEditableActiveGroup);
            // Basic Logic templates — each adds a BL card pre-filled with the function skeleton.
            AddConcatenateToActiveCommand  = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("CONCATENATE(\"\", {INPUT})"),                    () => HasEditableActiveGroup);
            AddIfElseToActiveCommand       = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("IF(EQ({INPUT}, \"\"), \"\", \"\")"),             () => HasEditableActiveGroup);
            AddLookupToActiveCommand       = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LOOKUP({INPUT}, \"PRI\", \"SEC\")"),             () => HasEditableActiveGroup);
            AddFormatToActiveCommand       = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("FORMAT({INPUT}, \"0.00\")"),                     () => HasEditableActiveGroup);
            AddRoundToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("ROUND({INPUT}, 2)"),                             () => HasEditableActiveGroup);
            AddValueToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("VALUE({INPUT})"),                                () => HasEditableActiveGroup);
            AddStrToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("STR({INPUT})"),                                  () => HasEditableActiveGroup);
            AddEqToActiveCommand           = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("EQ({INPUT}, \"\")"),                             () => HasEditableActiveGroup);
            AddNeToActiveCommand           = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("NE({INPUT}, \"\")"),                             () => HasEditableActiveGroup);
            AddLtToActiveCommand           = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LT({INPUT}, 0)"),                                () => HasEditableActiveGroup);
            AddGtToActiveCommand           = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("GT({INPUT}, 0)"),                                () => HasEditableActiveGroup);
            AddLteToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LTE({INPUT}, \"0\")"),                           () => HasEditableActiveGroup);
            AddGteToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("GTE({INPUT}, \"0\")"),                           () => HasEditableActiveGroup);
            AddAndToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("AND(EQ({INPUT}, \"\"), EQ({INPUT}, \"\"))"),     () => HasEditableActiveGroup);
            AddOrToActiveCommand           = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("OR(EQ({INPUT}, \"\"), EQ({INPUT}, \"\"))"),      () => HasEditableActiveGroup);
            AddNotToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("NOT(EQ({INPUT}, \"\"))"),                        () => HasEditableActiveGroup);
            AddJoinToActiveCommand         = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("JOIN(\"-\", {INPUT}, \"\")"),                    () => HasEditableActiveGroup);
            AddLeftToActiveCommand         = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LEFT({INPUT}, 3)"),                              () => HasEditableActiveGroup);
            AddRightToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("RIGHT({INPUT}, 3)"),                             () => HasEditableActiveGroup);
            AddMidToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("MID({INPUT}, 0, 3)"),                            () => HasEditableActiveGroup);
            AddTrimToActiveCommand         = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("TRIM({INPUT})"),                                 () => HasEditableActiveGroup);
            AddUpperToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("UPPER({INPUT})"),                                () => HasEditableActiveGroup);
            AddLowerToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LOWER({INPUT})"),                                () => HasEditableActiveGroup);
            AddReplaceToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("REPLACE({INPUT}, \"old\", \"new\")"),            () => HasEditableActiveGroup);
            AddAbsToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("ABS({INPUT})"),                                  () => HasEditableActiveGroup);
            AddLenToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("LEN({INPUT})"),                                  () => HasEditableActiveGroup);
            AddContainsToActiveCommand     = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("CONTAINS({INPUT}, \"\")"),                       () => HasEditableActiveGroup);
            AddStartsWithToActiveCommand   = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("STARTSWITH({INPUT}, \"\")"),                     () => HasEditableActiveGroup);
            AddEndsWithToActiveCommand     = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("ENDSWITH({INPUT}, \"\")"),                       () => HasEditableActiveGroup);
            AddIsEmptyToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("ISEMPTY({INPUT})"),                              () => HasEditableActiveGroup);
            AddDefaultToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddBasicLogicCard("DEFAULT({INPUT}, \"\")"),                        () => HasEditableActiveGroup);
            ToggleBasicLogicsPanelCommand  = new RelayCommand(() => IsBasicLogicsPanelOpen = !IsBasicLogicsPanelOpen);
            ToggleCardPanelCommand         = new RelayCommand(() => IsCardPanelOpen        = !IsCardPanelOpen);

            CapSetGroups.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasCapSetGroups));
                OnPropertyChanged(nameof(HasNoCapSetGroups));
            };

            // Restore group list for last-selected capability set
            RebuildCapSetGroups();
        }

        // ── Helpers ──

        private void RefreshCurrentColumns()
        {
            CurrentColumns.Clear();
            SelectedColumn = null;
            if (_workingCopy == null) return;
            foreach (var col in _workingCopy.Columns) CurrentColumns.Add(col);
        }

        private void RefreshEntries()
        {
            EntryRows.Clear();
            if (_workingCopy == null) return;
            foreach (var e in _workingCopy.Entries)
            {
                var row = new EntryRow(e.Values);
                row.OnChanged = () => IsDirty = true;
                EntryRows.Add(row);
            }
        }

        private static CatalogData DeepClone(CatalogData src) => new CatalogData
        {
            Id      = src.Id,
            Name    = src.Name,
            Columns = src.Columns.Select(c => new CatalogColumn { Key = c.Key, Label = c.Label, Role = c.Role, RoleIndex = c.RoleIndex }).ToList(),
            Entries = src.Entries.Select(e => new CatalogEntry  { Values = new Dictionary<string, string>(e.Values) }).ToList(),
        };

        // ── Capability set operations (all immediate — no working-copy needed until card editing) ──

        private void NewCapSet()
        {
            string name = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_NewCapSet"),
                LanguageLoader.Get("CatBuilder_Dlg_NewCapSetPrompt"));
            if (string.IsNullOrWhiteSpace(name)) return;
            var s = new CapabilitySet { Name = name };
            _capStore?.Save(s);
            CapabilitySets.Add(s);
            SelectedCapabilitySet = s;
        }

        private void RenameCapSet()
        {
            if (_selectedCapabilitySet == null) return;
            string name = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_RenameCapSet"),
                _selectedCapabilitySet.Name);
            if (string.IsNullOrWhiteSpace(name) || name == _selectedCapabilitySet.Name) return;
            _selectedCapabilitySet.Name = name;
            _capStore?.Save(_selectedCapabilitySet);
        }

        private void DeleteCapSet()
        {
            if (_selectedCapabilitySet == null) return;
            if (ConfirmDelete?.Invoke() != true) return;
            var toRemove = _selectedCapabilitySet;
            SelectedCapabilitySet = null;
            CapabilitySets.Remove(toRemove);
            _capStore?.Delete(toRemove);
        }

        private void ExportCapSet()
        {
            if (_selectedCapabilitySet == null) return;
            string path = PickCapSetSaveFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;
            try { _capStore?.ExportSet(_selectedCapabilitySet, path); }
            catch (Exception ex) { DiagLogger.Log("catalog", $"ExportCapSet failed: {DiagLogger.S(ex.Message)}"); }
        }

        private void ImportCapSet()
        {
            if (_capStore == null) return;
            string path = PickCapSetOpenFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var s = _capStore.ImportSet(path);
                CapabilitySets.Add(s);
                SelectedCapabilitySet = s;
            }
            catch (Exception ex) { DiagLogger.Log("catalog", $"ImportCapSet failed: {DiagLogger.S(ex.Message)}"); }
        }

        // ── Group management ──

        /// <summary>Returns catalog roles for a given catalog id — passed to CardGroupVm so cards update dynamically.</summary>
        internal IReadOnlyList<string> GetCatalogRolesForId(string catalogId)
        {
            if (string.IsNullOrEmpty(catalogId)) return Array.Empty<string>();
            var catalog = _store?.Catalogs.FirstOrDefault(c => c.Id == catalogId);
            return CardEngine.GetCatalogRoles(catalog);
        }

        private void RebuildCapSetGroups()
        {
            CapSetGroups.Clear();
            _activeGroupVm = null;
            _activeCardVm  = null;
            OnPropertyChanged(nameof(ActiveGroupVm));
            OnPropertyChanged(nameof(ActiveCardVm));
            OnPropertyChanged(nameof(HasActiveGroup));
            if (_selectedCapabilitySet == null)
            {
                RelayCommand.RaiseCanExecuteChanged();
                return;
            }
            // V1: Normal groups first, then Expert groups (preserving relative order within each section).
            var ordered = _selectedCapabilitySet.Groups
                .Select((g, idx) => new { g, idx, isExpert = g.IsExpert })
                .OrderBy(x => x.isExpert)
                .ThenBy(x => x.idx)
                .Select(x => x.g)
                .ToList();
            _selectedCapabilitySet.Groups.Clear();
            _selectedCapabilitySet.Groups.AddRange(ordered);

            foreach (var group in _selectedCapabilitySet.Groups)
                CapSetGroups.Add(MakeGroupVm(group));
            RenumberGroups();
            RecomputeExpertTopoOrder();
            OnPropertyChanged(nameof(HasAnyExpertGroup));
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Assigns sequential OrderNumber 1..N to CapSetGroups and marks the first Expert group.</summary>
        internal void RenumberGroups()
        {
            bool firstExpertSeen = false;
            for (int i = 0; i < CapSetGroups.Count; i++)
            {
                var g = CapSetGroups[i];
                g.OrderNumber = i + 1;
                if (g.IsExpert && !firstExpertSeen)
                {
                    g.IsFirstExpert = true;
                    firstExpertSeen = true;
                }
                else
                {
                    g.IsFirstExpert = false;
                }
            }
        }

        /// <summary>Called when a group's IsExpert flag flips. Moves the group to the bottom of its new section + renumbers.</summary>
        internal void OnGroupExpertModeChanged(CardGroupVm groupVm)
        {
            int oldIdx = CapSetGroups.IndexOf(groupVm);
            if (oldIdx < 0) return;
            // Underlying Groups list: remove and re-insert at end of the appropriate section.
            var group = groupVm.Group;
            _selectedCapabilitySet.Groups.Remove(group);
            int insertAt = group.IsExpert
                ? _selectedCapabilitySet.Groups.Count                                  // append to end (Expert section is last)
                : _selectedCapabilitySet.Groups.FindIndex(g => g.IsExpert);            // before first Expert (or at end if none)
            if (insertAt < 0) insertAt = _selectedCapabilitySet.Groups.Count;
            _selectedCapabilitySet.Groups.Insert(insertAt, group);

            // Mirror into CapSetGroups
            CapSetGroups.Move(oldIdx, insertAt);
            RenumberGroups();
            RecomputeExpertTopoOrder();
            OnPropertyChanged(nameof(HasAnyExpertGroup));
        }

        /// <summary>Computes topological evaluation order for Expert groups and sets ExpertTopoOrder on each CardGroupVm.
        /// Normal groups get 0; Expert groups get 1..N in eval order; cycle members get -1.</summary>
        private void RecomputeExpertTopoOrder()
        {
            try
            {
                // Reset Normal groups
                foreach (var g in CapSetGroups)
                    if (!g.IsExpert) g.ExpertTopoOrder = 0;

                var expertVms = new System.Collections.Generic.List<CardGroupVm>();
                foreach (var g in CapSetGroups)
                    if (g.IsExpert) expertVms.Add(g);

                if (expertVms.Count == 0) return;

                var gidToVm    = new Dictionary<string, CardGroupVm>(expertVms.Count);
                foreach (var g in expertVms) gidToVm[g.Group.Id] = g;
                var expertGids = new HashSet<string>(gidToVm.Keys);
                var inDegree   = new Dictionary<string, int>(expertVms.Count);
                var outEdges   = new Dictionary<string, System.Collections.Generic.List<string>>(expertVms.Count);
                foreach (string id in expertGids) { inDegree[id] = 0; outEdges[id] = new System.Collections.Generic.List<string>(); }

                foreach (var g in expertVms)
                {
                    string gid = g.Group.Id;
                    foreach (var card in g.Cards)
                    {
                        if (card.Card.Type != CardEngine.CardTypeBasicLogic) continue;
                        if (!card.Card.Params.TryGetValue(CardEngine.ParamFormula, out string formula)) continue;
                        foreach (string refKey in FormulaEngine.GetExpertRefs(formula))
                        {
                            if (!refKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                            string refGid = refKey["SPECIAL:LOGIC:".Length..];
                            if (!expertGids.Contains(refGid) || refGid == gid) continue;
                            inDegree[gid]++;
                            outEdges[refGid].Add(gid);
                        }
                    }
                }

                var queue     = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
                var topoOrder = new System.Collections.Generic.List<string>();
                while (queue.Count > 0)
                {
                    string cur = queue.Dequeue();
                    topoOrder.Add(cur);
                    foreach (string next in outEdges[cur])
                        if (--inDegree[next] == 0) queue.Enqueue(next);
                }

                for (int i = 0; i < topoOrder.Count; i++)
                    gidToVm[topoOrder[i]].ExpertTopoOrder = i + 1;

                foreach (var kv in inDegree)
                    if (kv.Value > 0 && gidToVm.TryGetValue(kv.Key, out var vm))
                        vm.ExpertTopoOrder = -1;
            }
            catch { /* defensive — never crash CatalogBuilder from topo compute */ }
        }

        /// <summary>Called by code-behind when user clicks a group or card to activate it.</summary>
        internal void OnGroupCardActivated(CardGroupVm group, CardRowVm card)
        {
            var prev = _activeGroupVm;
            if (prev != null && prev != group)
            {
                prev.IsActive    = false;
                prev.SelectedCard = null;
            }
            if (group != null)
            {
                group.IsActive    = true;
                group.SelectedCard = card;
            }

            ActiveGroupVm = group;
            ActiveCardVm  = card;

            // All groups re-evaluate IsInactive (depends on whether any group is active)
            foreach (var g in CapSetGroups) g.NotifyActiveContextChanged();

            RelayCommand.RaiseCanExecuteChanged();
        }

        private void DuplicateGroup(CardGroupVm groupVm)
        {
            if (_selectedCapabilitySet == null || groupVm == null) return;
            var src = groupVm.Group;
            var newGroup = new CardGroup
            {
                Name           = src.Name,
                TargetFieldKey = src.TargetFieldKey,
                Cards = src.Cards.Select(c => new CapabilityCard
                {
                    Type      = c.Type,
                    Enabled   = c.Enabled,
                    CatalogId = c.CatalogId,
                    Params    = new Dictionary<string, string>(c.Params),
                }).ToList(),
            };
            int srcIdx = _selectedCapabilitySet.Groups.IndexOf(src);
            _selectedCapabilitySet.Groups.Insert(srcIdx + 1, newGroup);
            _capStore?.Save(_selectedCapabilitySet);
            var newVm = MakeGroupVm(newGroup);
            CapSetGroups.Insert(CapSetGroups.IndexOf(groupVm) + 1, newVm);
        }

        private CardGroupVm MakeGroupVm(CardGroup group)
        {
            // Each group needs its OWN ListCollectionView — sharing one causes shared currency,
            // which makes selecting a TargetField in one group write the same value to all groups.
            var ownView = new ListCollectionView(_availableTargetFields);
            ownView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldItem.GroupName)));
            return new CardGroupVm(group,
                () => _capStore?.Save(_selectedCapabilitySet),
                RemoveGroupVm,
                _availableTargetFields,
                ownView,
                _store?.Catalogs ?? Array.Empty<CatalogData>(),
                GetCatalogRolesForId,
                () => _activeGroupVm != null,
                CapSetGroups,
                OnGroupCardActivated,
                OnGroupExpertModeChanged,
                OnGroupDragDropCompleted,
                DuplicateGroup);
        }

        private void AddGroup()
        {
            if (_selectedCapabilitySet == null) return;
            var group = new CardGroup();
            _selectedCapabilitySet.Groups.Add(group);
            _capStore?.Save(_selectedCapabilitySet);
            var vm = MakeGroupVm(group);
            CapSetGroups.Add(vm);
        }

        internal void RemoveGroupVm(CardGroupVm vm)
        {
            if (_selectedCapabilitySet == null) return;
            _selectedCapabilitySet.Groups.Remove(vm.Group);
            CapSetGroups.Remove(vm);
            _capStore?.Save(_selectedCapabilitySet);
        }

        /// <summary>Called by code-behind after a drag-drop reorder of groups.</summary>
        public void OnGroupDragDropCompleted(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return;
            if (fromIndex >= CapSetGroups.Count || toIndex >= CapSetGroups.Count) return;
            if (_selectedCapabilitySet == null) return;

            var group = _selectedCapabilitySet.Groups[fromIndex];
            _selectedCapabilitySet.Groups.RemoveAt(fromIndex);
            _selectedCapabilitySet.Groups.Insert(toIndex, group);
            _capStore?.Save(_selectedCapabilitySet);
            CapSetGroups.Move(fromIndex, toIndex);
        }

        private void CommitAndSave()
        {
            if (_selectedCatalog == null || _workingCopy == null) return;
            _selectedCatalog.Name    = _workingCopy.Name;
            _selectedCatalog.Columns = _workingCopy.Columns;
            _selectedCatalog.Entries = _workingCopy.Entries;
            _store.Save(_selectedCatalog);
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty));
        }

        // ── Catalog-level operations (immediate — affect identity/membership) ──

        private void NewCatalog()
        {
            string name = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_NewCatalog"),
                LanguageLoader.Get("CatBuilder_Dlg_NewCatalogPrompt"));
            if (string.IsNullOrWhiteSpace(name)) return;
            var c = new CatalogData { Name = name };
            _store.Save(c);
            Catalogs.Add(c);
            SelectedCatalog = c;
        }

        private void RenameCatalog()
        {
            if (_selectedCatalog == null || _workingCopy == null) return;
            string name = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_RenameCatalog"),
                _selectedCatalog.Name);
            if (string.IsNullOrWhiteSpace(name) || name == _selectedCatalog.Name) return;
            // Capture before Catalogs[idx]=catalog fires CollectionChanged, which can null _selectedCatalog.
            var catalog  = _selectedCatalog;
            catalog.Name      = name;
            _workingCopy.Name = name;
            _store.Save(catalog);
        }

        private void DeleteCatalog()
        {
            if (_selectedCatalog == null) return;
            if (ConfirmDelete?.Invoke() != true) return;
            var toRemove = _selectedCatalog;
            SelectedCatalog = null;
            Catalogs.Remove(toRemove);
            _store.Delete(toRemove);
        }

        private void ExportCatalog()
        {
            // Export the working copy so in-progress changes are included
            if (_workingCopy == null) return;
            string path = PickSaveFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;
            try { _store.ExportCatalog(_workingCopy, path); }
            catch (Exception ex) { DiagLogger.Log("catalog", $"ExportCatalog failed: {DiagLogger.S(ex.Message)}"); }
        }

        private void ImportCatalog()
        {
            string path = PickOpenFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var c = _store.ImportCatalog(path);
                Catalogs.Add(c);
                SelectedCatalog = c;
            }
            catch (Exception ex) { DiagLogger.Log("catalog", $"ImportCatalog failed: {DiagLogger.S(ex.Message)}"); }
        }

        // ── Entry operations (deferred — go to _workingCopy) ──

        private void AddEntry()
        {
            if (_workingCopy == null) return;
            var entry = new CatalogEntry();

            // Insert after selected row, fall back to end
            int insertIdx = _workingCopy.Entries.Count;
            if (_selectedEntry != null)
            {
                int sel = _workingCopy.Entries.FindIndex(e => e.Values == _selectedEntry.Data);
                if (sel >= 0) insertIdx = sel + 1;
            }

            _workingCopy.Entries.Insert(insertIdx, entry);
            var row = new EntryRow(entry.Values);
            row.OnChanged = () => IsDirty = true;
            EntryRows.Insert(insertIdx, row);
            SelectedEntry = row;
            IsDirty = true;
        }

        private void RemoveEntry() =>
            RemoveEntries(_selectedEntry != null ? new List<EntryRow> { _selectedEntry } : null);

        /// <summary>Deletes one or more entry rows (context-menu single OR multi-select).
        /// Single-row delete delegates here so there is one code path and one menu label.</summary>
        public void RemoveEntries(IList<EntryRow> rows)
        {
            if (_workingCopy == null || rows == null || rows.Count == 0) return;
            if (ConfirmDelete?.Invoke() != true) return;   // same guard as DeleteColumns (Task #27)
            foreach (var entry in rows.Where(r => r != null).Distinct().ToList())
            {
                var match = _workingCopy.Entries.Find(e => e.Values == entry.Data);
                if (match != null) _workingCopy.Entries.Remove(match);
                EntryRows.Remove(entry);
            }
            SelectedEntry = null;
            IsDirty = true;
        }

        // ── Column operations (deferred — go to _workingCopy) ──

        private void AddColumn()
        {
            if (_workingCopy == null) return;
            string label = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_AddColumn"),
                LanguageLoader.Get("CatBuilder_Dlg_AddColumnPrompt"));
            if (string.IsNullOrWhiteSpace(label)) return;

            string key = LabelToKey(label);
            string baseKey = key;
            int n = 1;
            while (_workingCopy.Columns.Exists(c => c.Key == key))
                key = baseKey + "_" + n++;

            var col = new CatalogColumn { Key = key, Label = label };

            // Insert after selected column, fall back to end
            int insertIdx = _workingCopy.Columns.Count;
            if (_selectedColumn != null)
            {
                int sel = _workingCopy.Columns.IndexOf(_selectedColumn);
                if (sel >= 0) insertIdx = sel + 1;
            }

            _workingCopy.Columns.Insert(insertIdx, col);
            CurrentColumns.Insert(insertIdx, col);
            SelectedColumn = col;
            IsDirty = true;
            ColumnsChanged?.Invoke();
        }

        private void EditColumn()
        {
            if (_selectedColumn == null) return;
            string label = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_EditColumn"),
                _selectedColumn.Label);
            if (string.IsNullOrWhiteSpace(label) || label == _selectedColumn.Label) return;
            _selectedColumn.Label = label;
            int idx = CurrentColumns.IndexOf(_selectedColumn);
            if (idx >= 0) { CurrentColumns.RemoveAt(idx); CurrentColumns.Insert(idx, _selectedColumn); }
            SelectedColumn = _selectedColumn;
            IsDirty = true;
            ColumnsChanged?.Invoke();
        }

        private void DeleteColumn() =>
            DeleteColumns(_selectedColumn != null ? new List<CatalogColumn> { _selectedColumn } : null);

        /// <summary>Deletes one or more columns (context-menu single OR multi-select). One confirmation
        /// covers the whole batch. Single-column delete delegates here so there is one menu label.</summary>
        public void DeleteColumns(IList<CatalogColumn> cols)
        {
            if (_workingCopy == null || cols == null || cols.Count == 0) return;
            if (ConfirmDelete?.Invoke() != true) return;
            foreach (var col in cols.Where(c => c != null).Distinct().ToList())
            {
                _workingCopy.Columns.Remove(col);
                foreach (var e in _workingCopy.Entries) e.Values.Remove(col.Key);
                CurrentColumns.Remove(col);
            }
            SelectedColumn = null;
            RefreshEntries();
            IsDirty = true;
            ColumnsChanged?.Invoke();
        }

        // ── Sort (view-order only — does NOT set IsDirty) ──

        /// <summary>
        /// Sorts working-copy entries by the given column key.
        /// Without forceAscending: toggles between ascending/descending on repeated calls for the same key.
        /// With forceAscending: sets direction explicitly (used by context menu Sort A→Z / Z→A).
        /// Returns true when sorted ascending.
        /// </summary>
        public bool SortByColumnKey(string key, bool? forceAscending = null)
        {
            bool ascending = forceAscending ?? !(key == _lastSortKey && _lastSortAscending);
            _lastSortKey       = key;
            _lastSortAscending = ascending;

            if (_workingCopy == null) return ascending;

            var sorted = _workingCopy.Entries.ToList();
            sorted.Sort((a, b) =>
            {
                a.Values.TryGetValue(key, out var va); va ??= "";
                b.Values.TryGetValue(key, out var vb); vb ??= "";
                int cmp = NaturalCompare(va, vb);
                return ascending ? cmp : -cmp;
            });

            _workingCopy.Entries.Clear();
            foreach (var e in sorted) _workingCopy.Entries.Add(e);
            RefreshEntries();
            // IsDirty is intentionally NOT set here — column-header sort is a temporary view
            // operation. Only context-menu range sort (SortRangeByColumnKey) marks dirty.
            return ascending;
        }

        /// <summary>
        /// Sorts only the rows at <paramref name="rowIndices"/> in place; all other rows are untouched.
        /// Used by the context-menu sort when the user selects "sort selection only".
        /// Does not set IsDirty (consistent with full-table sort).
        /// </summary>
        public bool SortRangeByColumnKey(string key, bool ascending, IList<int> rowIndices)
        {
            if (_workingCopy == null || rowIndices.Count == 0) return ascending;
            _lastSortKey       = key;
            _lastSortAscending = ascending;

            var toSort = rowIndices.Select(i => _workingCopy.Entries[i]).ToList();
            toSort.Sort((a, b) =>
            {
                a.Values.TryGetValue(key, out var va); va ??= "";
                b.Values.TryGetValue(key, out var vb); vb ??= "";
                int cmp = NaturalCompare(va, vb);
                return ascending ? cmp : -cmp;
            });

            for (int i = 0; i < rowIndices.Count; i++)
                _workingCopy.Entries[rowIndices[i]] = toSort[i];

            RefreshEntries();
            return ascending;
        }

        // ── Context-menu insert operations ──

        public void InsertEntryBefore(EntryRow target)
        {
            if (_workingCopy == null) return;
            var entry = new CatalogEntry();
            int insertIdx = 0;
            if (target != null)
            {
                int sel = _workingCopy.Entries.FindIndex(e => e.Values == target.Data);
                if (sel >= 0) insertIdx = sel;
            }
            _workingCopy.Entries.Insert(insertIdx, entry);
            var row = new EntryRow(entry.Values);
            row.OnChanged = () => IsDirty = true;
            EntryRows.Insert(insertIdx, row);
            IsDirty = true;
        }

        public void MoveEntryUp(int rowIndex)
        {
            if (_workingCopy == null) return;
            if (rowIndex <= 0 || rowIndex >= EntryRows.Count) return;
            EntryRows.Move(rowIndex, rowIndex - 1);
            var entry = _workingCopy.Entries[rowIndex];
            _workingCopy.Entries.RemoveAt(rowIndex);
            _workingCopy.Entries.Insert(rowIndex - 1, entry);
            IsDirty = true;
        }

        public void MoveEntryDown(int rowIndex)
        {
            if (_workingCopy == null) return;
            if (rowIndex < 0 || rowIndex >= EntryRows.Count - 1) return;
            EntryRows.Move(rowIndex, rowIndex + 1);
            var entry = _workingCopy.Entries[rowIndex];
            _workingCopy.Entries.RemoveAt(rowIndex);
            _workingCopy.Entries.Insert(rowIndex + 1, entry);
            IsDirty = true;
        }

        public void InsertColumnBefore(CatalogColumn target)
        {
            if (_workingCopy == null) return;
            string label = AskForText?.Invoke(
                LanguageLoader.Get("CatBuilder_Dlg_AddColumn"),
                LanguageLoader.Get("CatBuilder_Dlg_AddColumnPrompt"));
            if (string.IsNullOrWhiteSpace(label)) return;

            string key = LabelToKey(label);
            string baseKey = key;
            int n = 1;
            while (_workingCopy.Columns.Exists(c => c.Key == key))
                key = baseKey + "_" + n++;

            var col = new CatalogColumn { Key = key, Label = label };
            int insertIdx = target != null
                ? Math.Max(0, _workingCopy.Columns.IndexOf(target))
                : 0;

            _workingCopy.Columns.Insert(insertIdx, col);
            CurrentColumns.Insert(insertIdx, col);
            SelectedColumn = col;
            IsDirty = true;
            ColumnsChanged?.Invoke();
        }

        // ── Column reorder (fired by code-behind on DataGrid ColumnDisplayIndexChanged) ──

        public void SyncColumnOrder(IEnumerable<string> labelOrder)
        {
            if (_workingCopy == null) return;
            var newOrder = labelOrder
                .Select(label => _workingCopy.Columns.FirstOrDefault(c => c.Label == label))
                .Where(c => c != null)
                .ToList();
            if (newOrder.Count != _workingCopy.Columns.Count) return;
            _workingCopy.Columns = newOrder;
            CurrentColumns.Clear();
            foreach (var c in newOrder) CurrentColumns.Add(c);
            IsDirty = true;
        }

        private void DoSave() => CommitAndSave();

        private static string LabelToKey(string label)
        {
            var sb = new StringBuilder();
            foreach (char c in label)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == ' ' || c == '_' || c == '-') sb.Append('_');
            }
            string k = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(k) ? "col" : k;
        }

        private static int NaturalCompare(string x, string y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int ix = 0, iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                bool xd = char.IsDigit(x[ix]);
                bool yd = char.IsDigit(y[iy]);
                if (xd && yd)
                {
                    int nx = ix, ny = iy;
                    while (nx < x.Length && char.IsDigit(x[nx])) nx++;
                    while (ny < y.Length && char.IsDigit(y[ny])) ny++;
                    if (ulong.TryParse(x.Substring(ix, nx - ix), out ulong vx) &&
                        ulong.TryParse(y.Substring(iy, ny - iy), out ulong vy))
                    {
                        int cmp = vx.CompareTo(vy);
                        if (cmp != 0) return cmp;
                    }
                    ix = nx; iy = ny;
                }
                else
                {
                    int cmp = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                    if (cmp != 0) return cmp;
                    ix++; iy++;
                }
            }
            return x.Length.CompareTo(y.Length);
        }

        // ── INotifyPropertyChanged ──
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// INPC wrapper over a single CapabilityCard for the card list in the Catalog Builder.
    /// </summary>
    public sealed class CardRowVm : INotifyPropertyChanged
    {
        internal CapabilityCard Card { get; }
        private readonly Action _onChanged;
        private readonly Func<string, IReadOnlyList<string>> _getRoles;

        /// <summary>All selectable target fields (for Sync companion field ComboBox).</summary>
        public IReadOnlyList<FieldItem> AvailableTargetFields { get; }

        /// <summary>All catalogs available for the per-card CatalogId picker.</summary>
        public IReadOnlyList<CatalogData> AvailableCatalogs { get; }

        /// <summary>
        /// Distinct role badges present in the card's selected catalog.
        /// Refreshed whenever CatalogId changes.
        /// </summary>
        private IReadOnlyList<string> _availableCatalogRoles;
        public IReadOnlyList<string> AvailableCatalogRoles
        {
            get => _availableCatalogRoles;
            private set { _availableCatalogRoles = value; OnPropertyChanged(); }
        }

        public CardRowVm(CapabilityCard card, Action onChanged,
                         IReadOnlyList<FieldItem> fields,
                         IReadOnlyList<CatalogData> catalogs,
                         Func<string, IReadOnlyList<string>> getRoles)
        {
            Card                  = card;
            _onChanged            = onChanged;
            _getRoles             = getRoles;
            AvailableTargetFields = fields   ?? Array.Empty<FieldItem>();
            AvailableCatalogs     = catalogs ?? Array.Empty<CatalogData>();

            _availableCatalogRoles = BuildRoleList(getRoles?.Invoke(card.CatalogId));

            _isCollapsed = Services.UiStateStore.LoadCardCollapsed(card.Id ?? "", defaultCollapsed: false);
            ToggleCollapseCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);

            InitDisplayColumns();
        }

        // ── F1 Collapsibility (per-card) ──────────────────────────────────────
        private bool _isCollapsed;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed == value) return;
                _isCollapsed = value;
                Services.UiStateStore.SaveCardCollapsed(Card.Id ?? "", value);
                OnPropertyChanged();
            }
        }
        public RelayCommand ToggleCollapseCommand { get; }

        private int _orderNumber;
        /// <summary>1-based number within the owning group; set by parent CardGroupVm.</summary>
        public int OrderNumber
        {
            get => _orderNumber;
            set { if (_orderNumber == value) return; _orderNumber = value; OnPropertyChanged(); }
        }

        private bool _isExpertModeGroup;
        /// <summary>True when this card's group has IsExpert=true. Set by parent VM. Drives ⚡ badge on Type Pill.</summary>
        public bool IsExpertModeGroup
        {
            get => _isExpertModeGroup;
            internal set { if (_isExpertModeGroup == value) return; _isExpertModeGroup = value; OnPropertyChanged(); }
        }

        private static IReadOnlyList<string> BuildRoleList(IReadOnlyList<string> catalogRoles)
        {
            var roles = new List<string> { "" };
            if (catalogRoles != null)
            {
                var sorted = new List<string>(catalogRoles);
                sorted.Sort(NaturalRoleCompare);
                roles.AddRange(sorted);
            }
            return roles;
        }

        // ── Per-card CatalogId (Dropdown / Button / Search cards) ──────────

        public string CatalogId
        {
            get => Card.CatalogId ?? "";
            set
            {
                string cur = CatalogId;
                if (cur == (value ?? "")) return;
                Card.CatalogId = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
                // Refresh role list for the new catalog
                AvailableCatalogRoles = BuildRoleList(_getRoles?.Invoke(Card.CatalogId));
            }
        }

        // ── Display column VMs (Dropdown / Button cards) ─────────────────────
        public ObservableCollection<DisplayColumnVm> DisplayColumnVms { get; } = new();

        private void InitDisplayColumns()
        {
            DisplayColumnVms.Clear();
            var savedRoles = new List<string>();
            for (int n = 0; n < MaxDisplayColumns; n++)
            {
                Card.Params.TryGetValue(DisplayRoleKey(n), out string role);
                if (string.IsNullOrEmpty(role)) break;
                savedRoles.Add(role);
            }
            // All saved roles + one trailing empty slot (unless already at max)
            int total = Math.Min(savedRoles.Count + 1, MaxDisplayColumns);
            for (int i = 0; i < total; i++)
                AddDisplayColumnVm(i, i < savedRoles.Count ? savedRoles[i] : "");
        }

        private void AddDisplayColumnVm(int index, string role)
        {
            var vm = new DisplayColumnVm(index, role, AvailableCatalogRoles);
            vm.OnRoleChanged = OnDisplayRoleChanged;
            DisplayColumnVms.Add(vm);
        }

        private void OnDisplayRoleChanged(DisplayColumnVm vm, string oldRole, string newRole)
        {
            int idx = vm.Index;
            if (string.IsNullOrEmpty(newRole))
            {
                // Clear this slot and all subsequent slots in params + VMs
                for (int n = idx; n < MaxDisplayColumns; n++)
                    Card.Params.Remove(DisplayRoleKey(n));
                while (DisplayColumnVms.Count > idx + 1)
                    DisplayColumnVms.RemoveAt(DisplayColumnVms.Count - 1);
            }
            else
            {
                Card.Params[DisplayRoleKey(idx)] = newRole;
                // Append trailing empty slot if this was the last and we're below the limit
                if (DisplayColumnVms.Count == idx + 1 && DisplayColumnVms.Count < MaxDisplayColumns)
                    AddDisplayColumnVm(idx + 1, "");
            }
            _onChanged?.Invoke();
        }

        public string CardTypeKey => Card.Type;

        public string TypeLabel => LanguageLoader.Get(Card.Type switch
        {
            CardTypeDropdown      => "CardType_Dropdown",
            CardTypeSync          => "CardType_Sync",
            CardTypeLink          => "CardType_Link",
            CardTypeButton        => "CardType_Button",
            CardTypeSearch        => "CardType_Search",
            CardTypeMultiPick     => "CardType_SmartComplete",
            CardTypePairTransform => "CardType_PairTransform",
            CardTypePrefixSuffix  => "CardType_PrefixSuffix",
            CardTypeSort          => "CardType_Sort",
            CardTypeCompose       => "CardType_Compose",
            CardTypeBasicLogic    => "CardType_BasicLogic",
            string t              => t,
        });
        public bool   IsSync                 => Card.Type == CardTypeSync;
        public bool   IsDropdown             => Card.Type == CardTypeDropdown;
        public bool   IsLink                 => Card.Type == CardTypeLink;
        public bool   IsButton               => Card.Type == CardTypeButton;
        public bool   IsSearch               => Card.Type == CardTypeSearch;
        public bool   IsMultiPick            => Card.Type == CardTypeMultiPick;
        public bool   IsPairTransform        => Card.Type == CardTypePairTransform;
        public bool   IsPrefixSuffix         => Card.Type == CardTypePrefixSuffix;
        public bool   IsSort                 => Card.Type == CardTypeSort;
        public bool   IsCompose              => Card.Type == CardTypeCompose;
        public bool   IsBasicLogic           => Card.Type == CardTypeBasicLogic;
        /// <summary>Cards that use a catalog (show catalog picker in config panel).</summary>
        public bool   IsCatalogCard          => IsDropdownButtonSearch || IsSort || IsCompose;
        /// <summary>Dropdown and Button cards expose SecRole/TooltipRole pickers (single-select context).</summary>
        public bool   IsDropdownOrButton     => Card.Type == CardTypeDropdown || Card.Type == CardTypeButton;
        /// <summary>Cards that have a catalog picker, sec/tooltip roles, and display columns.</summary>
        public bool   IsDropdownButtonSearch => Card.Type == CardTypeDropdown || Card.Type == CardTypeButton
                                            || Card.Type == CardTypeSearch    || Card.Type == CardTypeMultiPick;

        public bool Enabled
        {
            get => Card.Enabled;
            set
            {
                if (Card.Enabled == value) return;
                Card.Enabled = value;
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Dropdown card params ────────────────────────────────────────────

        /// <summary>Which catalog role appears as secondary text in dropdown items (default "SEC"; empty = hidden).</summary>
        public string SecRole
        {
            get => Card.Params.TryGetValue(ParamSecRole, out var v) ? v : "SEC";
            set
            {
                string cur = SecRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSecRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Which catalog role appears as the item tooltip in the dropdown (default "AUX"; empty = hidden).</summary>
        public string TooltipRole
        {
            get => Card.Params.TryGetValue(ParamTooltipRole, out var v) ? v : "AUX";
            set
            {
                string cur = TooltipRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamTooltipRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Search card params ──────────────────────────────────────────────

        /// <summary>Comma-separated role badges to match when filtering (e.g. "PRI,SEC"). Empty = PRI+SEC.</summary>
        public string SearchRoles
        {
            get => Card.Params.TryGetValue(ParamSearchRoles, out var v) ? v : "";
            set
            {
                string cur = SearchRoles;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSearchRoles] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Multi-Pick card params ──────────────────────────────────────────

        /// <summary>Separator inserted between selected tokens in the primary field (default "-").</summary>
        public string PrimaryTokenSeparator
        {
            get => Card.Params.TryGetValue(ParamPrimaryTokenSeparator, out var v) ? v : "-";
            set
            {
                string cur = PrimaryTokenSeparator;
                if (cur == (value ?? "")) return;
                Card.Params[ParamPrimaryTokenSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Separator inserted between companion tokens written to the companion field (default ", ").</summary>
        public string CompanionTokenSeparator
        {
            get => Card.Params.TryGetValue(ParamCompanionTokenSeparator, out var v) ? v : ", ";
            set
            {
                string cur = CompanionTokenSeparator;
                if (cur == (value ?? "")) return;
                Card.Params[ParamCompanionTokenSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Pair Transform card params ──────────────────────────────────────

        /// <summary>Separator used to split the source field value into tokens (default "-").</summary>
        public string SourceTokenSeparator
        {
            get => Card.Params.TryGetValue(ParamSourceTokenSeparator, out var v) ? v : "-";
            set
            {
                string cur = SourceTokenSeparator;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSourceTokenSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Catalog role badge each source token is looked up as (default "PRI").</summary>
        public string LookupRole
        {
            get => Card.Params.TryGetValue(ParamLookupRole, out var v) ? v : "PRI";
            set
            {
                string cur = LookupRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamLookupRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Catalog role badge whose value is output for each matched token (default "SEC").</summary>
        public string OutputRole
        {
            get => Card.Params.TryGetValue(ParamOutputRole, out var v) ? v : "SEC";
            set
            {
                string cur = OutputRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamOutputRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Separator used to join the output tokens (default ", ").</summary>
        public string OutputTokenSeparator
        {
            get => Card.Params.TryGetValue(ParamOutputTokenSeparator, out var v) ? v : ", ";
            set
            {
                string cur = OutputTokenSeparator;
                if (cur == (value ?? "")) return;
                Card.Params[ParamOutputTokenSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── PrefixSuffix card params ────────────────────────────────────────

        /// <summary>Text prepended to (or stripped from the start of) the field value.</summary>
        public string PsPrefix
        {
            get => Card.Params.TryGetValue(ParamPrefix, out var v) ? v : "";
            set
            {
                string cur = PsPrefix;
                if (cur == (value ?? "")) return;
                Card.Params[ParamPrefix] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Text appended to (or stripped from the end of) the field value.</summary>
        public string PsSuffix
        {
            get => Card.Params.TryGetValue(ParamSuffix, out var v) ? v : "";
            set
            {
                string cur = PsSuffix;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSuffix] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>When true the card strips prefix/suffix from stored values instead of adding them.</summary>
        public bool PsIsRemoveMode
        {
            get => string.Equals(
                Card.Params.TryGetValue(ParamIsRemoveMode, out var v) ? v : "false",
                "true", StringComparison.OrdinalIgnoreCase);
            set
            {
                bool cur = PsIsRemoveMode;
                if (cur == value) return;
                Card.Params[ParamIsRemoveMode] = value ? "true" : "false";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Sort card params ────────────────────────────────────────────────

        /// <summary>Separator used to split and rejoin the field value into tokens (default "-").</summary>
        public string SrtTokenSeparator
        {
            get => Card.Params.TryGetValue(ParamSortTokenSeparator, out var v) ? v : "-";
            set
            {
                string cur = SrtTokenSeparator;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSortTokenSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Catalog role badge each token is looked up as (default "PRI").</summary>
        public string SrtLookupRole
        {
            get => Card.Params.TryGetValue(ParamSortLookupRole, out var v) ? v : "PRI";
            set
            {
                string cur = SrtLookupRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamSortLookupRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>When true the sort order is reversed (descending).</summary>
        public bool SrtIsInvert
        {
            get => string.Equals(
                Card.Params.TryGetValue(ParamSortInvert, out var v) ? v : "false",
                "true", StringComparison.OrdinalIgnoreCase);
            set
            {
                bool cur = SrtIsInvert;
                if (cur == value) return;
                Card.Params[ParamSortInvert] = value ? "true" : "false";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Compose card params (Task #41) ──────────────────────────────────
        // LookupRole / OutputRole / CompanionFieldKey reuse the shared properties below.

        /// <summary>Separator joining differing composed items (default " / ").</summary>
        public string ComposeItemSeparator
        {
            get => Card.Params.TryGetValue(ParamComposeItemSeparator, out var v) ? v : " / ";
            set { if (ComposeItemSeparator == (value ?? "")) return; Card.Params[ParamComposeItemSeparator] = value ?? ""; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Comma-separated positional prefixes (spaces preserved), e.g. "Front ,Back ".</summary>
        public string ComposeItemPrefixes
        {
            get => Card.Params.TryGetValue(ParamComposeItemPrefixes, out var v) ? v : "";
            set { if (ComposeItemPrefixes == (value ?? "")) return; Card.Params[ParamComposeItemPrefixes] = value ?? ""; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Prefix used when identical codes collapse into one item (default "").</summary>
        public string ComposeCollapsedPrefix
        {
            get => Card.Params.TryGetValue(ParamComposeCollapsedPrefix, out var v) ? v : "";
            set { if (ComposeCollapsedPrefix == (value ?? "")) return; Card.Params[ParamComposeCollapsedPrefix] = value ?? ""; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>When true, identical codes collapse into one item using CollapsedPrefix.</summary>
        public bool ComposeCollapseWhenEqual
        {
            get => string.Equals(Card.Params.TryGetValue(ParamComposeCollapseWhenEqual, out var v) ? v : "false", "true", StringComparison.OrdinalIgnoreCase);
            set { if (ComposeCollapseWhenEqual == value) return; Card.Params[ParamComposeCollapseWhenEqual] = value ? "true" : "false"; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>When true (default), codes whose output value is empty are dropped from the result.</summary>
        public bool ComposeDropEmptyOutputs
        {
            get => !string.Equals(Card.Params.TryGetValue(ParamComposeDropEmptyOutputs, out var v) ? v : "true", "false", StringComparison.OrdinalIgnoreCase);
            set { if (ComposeDropEmptyOutputs == value) return; Card.Params[ParamComposeDropEmptyOutputs] = value ? "true" : "false"; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Maximum sub-codes to extract; empty = unlimited.</summary>
        public string ComposeMaxItems
        {
            get => Card.Params.TryGetValue(ParamComposeMaxItems, out var v) ? v : "";
            set
            {
                string val = (value ?? "").Trim();
                if (ComposeMaxItems == val) return;
                if (val.Length == 0) Card.Params.Remove(ParamComposeMaxItems);
                else if (int.TryParse(val, out int n) && n >= 0) Card.Params[ParamComposeMaxItems] = n.ToString();
                else return;   // ignore non-numeric input
                _onChanged?.Invoke(); OnPropertyChanged();
            }
        }

        /// <summary>Policy for input that doesn't fully tokenise: skip | keepRaw | passthrough.</summary>
        public string ComposeOnUnknownToken
        {
            get => Card.Params.TryGetValue(ParamComposeOnUnknownToken, out var v) && !string.IsNullOrEmpty(v) ? v : "skip";
            set { if (ComposeOnUnknownToken == (value ?? "skip")) return; Card.Params[ParamComposeOnUnknownToken] = string.IsNullOrEmpty(value) ? "skip" : value; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Options for the OnUnknownToken combo.</summary>
        public IReadOnlyList<string> ComposeUnknownModes { get; } = new[] { "skip", "keepRaw", "passthrough" };

        // ── Compose card — Split Mode params (Task #42) ─────────────────────

        /// <summary>Enables Split Mode: source field is split on this separator before sub-tokenization.</summary>
        public string ComposeSourceSeparator
        {
            get => Card.Params.TryGetValue(ParamComposeSourceSeparator, out var v) ? v : "";
            set
            {
                if (ComposeSourceSeparator == (value ?? "")) return;
                Card.Params[ParamComposeSourceSeparator] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsComposeSplitMode));  // drives Row 3 visibility
            }
        }

        /// <summary>True when the card is in Split Mode (SourceSeparator is set). Drives Row 3 visibility.</summary>
        public bool IsComposeSplitMode => !string.IsNullOrEmpty(ComposeSourceSeparator);

        /// <summary>Separator used to join per-token outputs (default ", ").</summary>
        public string ComposeTokenOutputSeparator
        {
            get => Card.Params.TryGetValue(ParamComposeTokenOutputSeparator, out var v) ? v : ", ";
            set { if (ComposeTokenOutputSeparator == (value ?? ", ")) return; Card.Params[ParamComposeTokenOutputSeparator] = value ?? ", "; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Catalog ID used for sub-tokenization (overrides the card's primary catalog). Empty = use primary catalog.</summary>
        public string ComposeFallbackCatalogId
        {
            get => Card.Params.TryGetValue(ParamComposeFallbackCatalogId, out var v) ? v ?? "" : "";
            set { if (ComposeFallbackCatalogId == (value ?? "")) return; Card.Params[ParamComposeFallbackCatalogId] = value ?? ""; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Whether direct-match tokens are included in or skipped from the output (default "include").</summary>
        public string ComposeOnDirectMatch
        {
            get => Card.Params.TryGetValue(ParamComposeOnDirectMatch, out var v) && !string.IsNullOrEmpty(v) ? v : "include";
            set { if (ComposeOnDirectMatch == (value ?? "include")) return; Card.Params[ParamComposeOnDirectMatch] = string.IsNullOrEmpty(value) ? "include" : value; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Options for the OnDirectMatch combo.</summary>
        public IReadOnlyList<string> ComposeDirectMatchModes { get; } = new[] { "include", "skip" };

        /// <summary>Whether this card replaces or appends to the companion field (default "replace").</summary>
        public string ComposeOutputMode
        {
            get => Card.Params.TryGetValue(ParamComposeOutputMode, out var v) && !string.IsNullOrEmpty(v) ? v : "replace";
            set { if (ComposeOutputMode == (value ?? "replace")) return; Card.Params[ParamComposeOutputMode] = string.IsNullOrEmpty(value) ? "replace" : value; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>Options for the OutputMode combo.</summary>
        public IReadOnlyList<string> ComposeOutputModes { get; } = new[] { "replace", "append" };

        /// <summary>Separator prepended between the existing companion value and the new result in append mode (default ", ").</summary>
        public string ComposeAppendSeparator
        {
            get => Card.Params.TryGetValue(ParamComposeAppendSeparator, out var v) ? v : ", ";
            set { if (ComposeAppendSeparator == (value ?? ", ")) return; Card.Params[ParamComposeAppendSeparator] = value ?? ", "; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        /// <summary>T43 — this card's segment rank in the sorted assembly (placing_order). Default "0".</summary>
        public string ComposeOutputPlacing
        {
            get => Card.Params.TryGetValue(ParamComposeOutputPlacing, out var v) ? v : "0";
            set { if (ComposeOutputPlacing == (value ?? "0")) return; Card.Params[ParamComposeOutputPlacing] = value ?? "0"; _onChanged?.Invoke(); OnPropertyChanged(); }
        }

        // ── Basic Logic card params ─────────────────────────────────────────

        /// <summary>BL formula expression (see <see cref="FormulaEngine"/> syntax).</summary>
        public string FormulaText
        {
            get => Card.Params.TryGetValue(ParamFormula, out var v) ? v : "";
            set
            {
                string cur = FormulaText;
                if (cur == (value ?? "")) return;
                Card.Params[ParamFormula] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasExpertRef));
            }
        }

        /// <summary>True when the BL formula contains at least one <c>$[...]</c> Expert Mode reference. Drives ⚡ indicator in the Logics-Constructor.</summary>
        public bool HasExpertRef => FormulaEngine.HasExpertRef(FormulaText);

        /// <summary>Optional override: field key that receives the formula result. Empty = group's TargetFieldKey.</summary>
        public string FormulaTargetFieldKey
        {
            get => Card.Params.TryGetValue(ParamFormulaTargetFieldKey, out var v) ? v : "";
            set
            {
                string cur = FormulaTargetFieldKey;
                if (cur == (value ?? "")) return;
                Card.Params[ParamFormulaTargetFieldKey] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Link card params ────────────────────────────────────────────────

        /// <summary>Which Checkup field key is the locked companion row below this Logic row.</summary>
        public string PartnerFieldKey
        {
            get => Card.Params.TryGetValue(ParamLinkPartnerFieldKey, out var v) ? v : "";
            set
            {
                string cur = PartnerFieldKey;
                if (cur == (value ?? "")) return;
                Card.Params[ParamLinkPartnerFieldKey] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardFieldPickerDropText));
            }
        }

        // ── Sync card params ────────────────────────────────────────────────

        /// <summary>Which catalog role value is written to the companion field (default "SEC").</summary>
        public string CompanionRole
        {
            get => Card.Params.TryGetValue(ParamCompanionRole, out var v) ? v : "SEC";
            set
            {
                string cur = CompanionRole;
                if (cur == (value ?? "")) return;
                Card.Params[ParamCompanionRole] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        /// <summary>Which Checkup field the Sync card writes to.</summary>
        public string CompanionFieldKey
        {
            get => Card.Params.TryGetValue(ParamCompanionFieldKey, out var v) ? v : "";
            set
            {
                string cur = CompanionFieldKey;
                if (cur == (value ?? "")) return;
                Card.Params[ParamCompanionFieldKey] = value ?? "";
                _onChanged?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardFieldPickerDropText));
            }
        }

        // ── Card Field Picker (P3-C enhanced popup) ──────────────────────────

        private string       _cardFieldPickerFilter = "";
        private bool         _isCardFieldPickerOpen;
        private List<string> _cardFieldPickerPinnedKeys = new();

        /// <summary>True for card types that have a companion/partner field picker (Link, Sync, MultiPick, PairTransform, Compose).</summary>
        public bool HasCardFieldPicker => IsLink || IsSync || IsMultiPick || IsPairTransform || IsCompose;

        /// <summary>Unified field key: PartnerFieldKey for Link cards, CompanionFieldKey for all other field-picker card types.</summary>
        public string CardFieldPickerKey
        {
            get => IsLink ? PartnerFieldKey : CompanionFieldKey;
            set
            {
                if (IsLink) PartnerFieldKey    = value;
                else        CompanionFieldKey  = value;
            }
        }

        public string CardFieldPickerDropText
        {
            get
            {
                string key = CardFieldPickerKey;
                if (string.IsNullOrEmpty(key)) return "—";
                var fi = AvailableTargetFields.FirstOrDefault(f => f.Key == key);
                return fi?.DropText ?? key;
            }
        }

        public bool IsCardFieldPickerOpen
        {
            get => _isCardFieldPickerOpen;
            set { _isCardFieldPickerOpen = value; OnPropertyChanged(); }
        }

        public string CardFieldPickerFilter
        {
            get => _cardFieldPickerFilter;
            set { _cardFieldPickerFilter = value ?? ""; OnPropertyChanged(); ApplyCardFieldPickerFilter(); }
        }

        public ObservableCollection<FieldSelectorGroupVm> CardFieldPickerGroups    { get; } = new();
        public ObservableCollection<PinnedFieldEntry>      CardFieldPickerFavoriten { get; } = new();
        public bool HasCardFieldPickerFavoriten => CardFieldPickerFavoriten.Count > 0;

        public void OpenCardFieldPicker()
        {
            if (CardFieldPickerGroups.Count == 0)
                BuildCardFieldPickerGroups();
            RebuildCardFieldPickerFavoriten();
            IsCardFieldPickerOpen = true;
        }

        public void OnCardFieldPickerClosed()
        {
            _cardFieldPickerFilter = "";
            OnPropertyChanged(nameof(CardFieldPickerFilter));
            ApplyCardFieldPickerFilter();
        }

        private void BuildCardFieldPickerGroups()
        {
            var byGroup = AvailableTargetFields
                .Where(f => !string.IsNullOrEmpty(f.Key) && !f.IsActionItem)
                .GroupBy(f => f.GroupName)
                .OrderBy(g => CfGroupOrder(g.Key))
                .ToList();

            foreach (var g in byGroup)
            {
                if (string.IsNullOrEmpty(g.Key)) continue;

                var allItems = g
                    .OrderBy(f => f.DropText, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                bool isCollapsed    = Services.UiStateStore.LoadFieldSelGroupCollapsed(g.Key);
                string displayName  = LanguageLoader.Get(g.Key);

                var gvm = new FieldSelectorGroupVm
                {
                    GroupName        = g.Key,
                    GroupDisplayName = displayName,
                    AllItems         = allItems,
                    FilteredItems    = allItems,
                    IsCollapsed      = isCollapsed,
                    IsChevronEnabled = true,
                };
                var captured = gvm;
                gvm.ToggleCollapseCommand = new RelayCommand(() =>
                {
                    if (!captured.IsChevronEnabled) return;
                    captured.IsCollapsed = !captured.IsCollapsed;
                    Services.UiStateStore.SaveFieldSelGroupCollapsed(captured.GroupName, captured.IsCollapsed);
                });

                CardFieldPickerGroups.Add(gvm);
            }
        }

        private static int CfGroupOrder(string groupName) => groupName switch
        {
            "Grp_Special"           => 0,
            "Grp_iPropertiesCustom" => 1,
            "Grp_ParamUser"         => 2,
            "Grp_iProperties"       => 3,
            "Grp_Document"          => 4,
            "Grp_ParamModel"        => 5,
            _                       => 99,
        };

        private void ApplyCardFieldPickerFilter()
        {
            string filter = _cardFieldPickerFilter?.Trim() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var gvm in CardFieldPickerGroups)
            {
                if (hasFilter)
                {
                    var matched = gvm.AllItems
                        .Where(f => f.DropText.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        .ToList();
                    gvm.FilteredItems = matched;
                    if (matched.Count > 0) gvm.IsCollapsed = false;
                }
                else
                {
                    gvm.FilteredItems = gvm.AllItems;
                    gvm.IsCollapsed   = Services.UiStateStore.LoadFieldSelGroupCollapsed(gvm.GroupName);
                }
            }
        }

        public void RebuildCardFieldPickerFavoriten()
        {
            string saved = Services.UiStateStore.LoadFieldSelPinnedFields();
            _cardFieldPickerPinnedKeys = string.IsNullOrEmpty(saved)
                ? new List<string>()
                : saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            CardFieldPickerFavoriten.Clear();
            foreach (string key in _cardFieldPickerPinnedKeys)
            {
                var fi     = AvailableTargetFields.FirstOrDefault(f => f.Key == key);
                bool avail = fi != null;
                if (!avail) fi = new FieldItem(key, key, key);
                CardFieldPickerFavoriten.Add(new PinnedFieldEntry { Item = fi, IsAvailable = avail });
            }
            OnPropertyChanged(nameof(HasCardFieldPickerFavoriten));
        }

        public void ToggleCardFieldPin(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_cardFieldPickerPinnedKeys.Contains(key))
                _cardFieldPickerPinnedKeys.Remove(key);
            else
                _cardFieldPickerPinnedKeys.Insert(0, key);
            Services.UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _cardFieldPickerPinnedKeys));
            RebuildCardFieldPickerFavoriten();
        }

        public void ReorderCardFieldPin(string fromKey, string toKey)
        {
            int fromIdx = _cardFieldPickerPinnedKeys.IndexOf(fromKey);
            int toIdx   = _cardFieldPickerPinnedKeys.IndexOf(toKey);
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
            _cardFieldPickerPinnedKeys.RemoveAt(fromIdx);
            _cardFieldPickerPinnedKeys.Insert(toIdx, fromKey);
            Services.UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _cardFieldPickerPinnedKeys));
            RebuildCardFieldPickerFavoriten();
        }

        // Natural sort for role badges: alphabetical on the text part, numeric on trailing digits.
        // "AUX" < "AUX1" < "AUX2" < ... < "GRP" < "PRI" < "SEC" < ...
        private static int NaturalRoleCompare(string a, string b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int ia = a.Length, ib = b.Length;
            while (ia > 0 && char.IsDigit(a[ia - 1])) ia--;
            while (ib > 0 && char.IsDigit(b[ib - 1])) ib--;
            int cmp = string.Compare(a.Substring(0, ia), b.Substring(0, ib), StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            string na = ia < a.Length ? a.Substring(ia) : "";
            string nb = ib < b.Length ? b.Substring(ib) : "";
            if (na == "" && nb == "") return 0;
            if (na == "") return -1;
            if (nb == "") return 1;
            if (ulong.TryParse(na, out ulong va) && ulong.TryParse(nb, out ulong vb))
                return va.CompareTo(vb);
            return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// ViewModel for one Card Group within a Capability Set in the Logic Builder.
    /// Owns the group's TargetFieldKey and the list of CardRowVm items.
    /// Exposes per-group card management commands and drag-state properties.
    /// </summary>
    public sealed class CardGroupVm : INotifyPropertyChanged
    {
        internal CardGroup Group { get; }
        private readonly Action _onSave;
        private readonly Action<CardGroupVm> _onRemove;
        private readonly Func<string, IReadOnlyList<string>> _getRoles;
        private readonly ObservableCollection<CardGroupVm> _allGroups;
        private readonly Action<CardGroupVm, CardRowVm>    _activateCard;
        private readonly Action<CardGroupVm>               _onExpertModeChanged;
        private readonly Action<int, int>                  _moveGroup;       // from, to
        private readonly Action<CardGroupVm>               _duplicateGroup;

        public IReadOnlyList<FieldItem>   AvailableTargetFields  { get; }
        public ListCollectionView         GroupedTargetFields     { get; }
        public IReadOnlyList<CatalogData> AvailableCatalogs       { get; }

        // ── Accent color (stable per group ID, cycles through a palette) ──────────
        private static readonly System.Windows.Media.Brush[] _palette = BuildPalette();
        private static System.Windows.Media.Brush[] BuildPalette()
        {
            var colors = new[]
            {
                System.Windows.Media.Color.FromRgb(0x5B, 0xA3, 0xDE), // blue
                System.Windows.Media.Color.FromRgb(0x6C, 0xB8, 0x7A), // green
                System.Windows.Media.Color.FromRgb(0xC8, 0x98, 0x5A), // amber
                System.Windows.Media.Color.FromRgb(0xBF, 0x6F, 0xC8), // purple
                System.Windows.Media.Color.FromRgb(0xDE, 0x6A, 0x6A), // red
                System.Windows.Media.Color.FromRgb(0x5B, 0xC8, 0xC8), // teal
                System.Windows.Media.Color.FromRgb(0xDE, 0xB8, 0x5A), // gold
                System.Windows.Media.Color.FromRgb(0x9A, 0xDE, 0x5A), // lime
            };
            var brushes = colors.Select(c => { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return (System.Windows.Media.Brush)b; }).ToArray();
            return brushes;
        }
        public System.Windows.Media.Brush AccentBrush { get; }

        public ObservableCollection<CardRowVm> Cards { get; } = new();

        // ── Commands ──────────────────────────────────────────────────────────
        public RelayCommand AddDropdownCardCommand  { get; }
        public RelayCommand AddSyncCardCommand      { get; }
        public RelayCommand AddLinkCardCommand      { get; }
        public RelayCommand AddButtonCardCommand    { get; }
        public RelayCommand AddSearchCardCommand    { get; }
        public RelayCommand RemoveCardCommand       { get; }
        public RelayCommand MoveCardUpCommand       { get; }
        public RelayCommand MoveCardDownCommand     { get; }
        public RelayCommand DuplicateCardCommand    { get; }
        public RelayCommand RemoveGroupCommand      { get; }
        public RelayCommand ToggleCollapseCommand   { get; }
        public RelayCommand ToggleExpertCommand     { get; }
        public RelayCommand ToggleSortCommand       { get; }
        public RelayCommand MoveGroupUpCommand      { get; }
        public RelayCommand MoveGroupDownCommand    { get; }
        public RelayCommand DuplicateGroupCommand   { get; }

        // ── Drag state ────────────────────────────────────────────────────────
        private bool _isDragging;
        public bool IsDragging
        {
            get => _isDragging;
            set { _isDragging = value; OnPropertyChanged(); }
        }

        private bool _isDragOver;
        public bool IsDragOver
        {
            get => _isDragOver;
            set { _isDragOver = value; OnPropertyChanged(); }
        }

        // ── Active/inactive state (for global ▲▼⧉× toolbar) ──────────────────
        private readonly Func<bool> _isAnyGroupActive;

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInactive));
            }
        }
        public bool IsInactive => !_isActive && _isAnyGroupActive();
        internal void NotifyActiveContextChanged() => OnPropertyChanged(nameof(IsInactive));

        private CardRowVm _selectedCard;
        public CardRowVm SelectedCard
        {
            get => _selectedCard;
            set { _selectedCard = value; OnPropertyChanged(); }
        }

        public CardGroupVm(CardGroup group,
                           Action onSave,
                           Action<CardGroupVm> onRemove,
                           IReadOnlyList<FieldItem> availableTargetFields,
                           ListCollectionView groupedTargetFields,
                           IReadOnlyList<CatalogData> availableCatalogs,
                           Func<string, IReadOnlyList<string>> getRoles,
                           Func<bool> isAnyGroupActive = null,
                           ObservableCollection<CardGroupVm> allGroups = null,
                           Action<CardGroupVm, CardRowVm> activateCard = null,
                           Action<CardGroupVm> onExpertModeChanged = null,
                           Action<int, int> moveGroup = null,
                           Action<CardGroupVm> duplicateGroup = null)
        {
            Group                 = group;
            _onSave               = onSave;
            _onRemove             = onRemove;
            _getRoles             = getRoles;
            _allGroups            = allGroups;
            _activateCard         = activateCard;
            _onExpertModeChanged  = onExpertModeChanged;
            _moveGroup            = moveGroup;
            _duplicateGroup       = duplicateGroup;
            AvailableTargetFields = availableTargetFields ?? Array.Empty<FieldItem>();
            GroupedTargetFields   = groupedTargetFields;
            AvailableCatalogs     = availableCatalogs    ?? Array.Empty<CatalogData>();
            AccentBrush           = _palette[Math.Abs((group.Id ?? "").GetHashCode()) % _palette.Length];
            _isAnyGroupActive     = isAnyGroupActive ?? (() => false);
            _isCollapsed          = Services.UiStateStore.LoadGroupCollapsed(group.Id ?? "", defaultCollapsed: false);

            foreach (var card in group.Cards)
            {
                var vm = MakeCardVm(card);
                vm.IsExpertModeGroup = group.IsExpert;
                vm.PropertyChanged += OnCardRowPropertyChanged;
                Cards.Add(vm);
            }
            Cards.CollectionChanged += OnCardsCollectionChanged;
            RenumberCards();

            AddDropdownCardCommand = new RelayCommand(() => AddCard(CardTypeDropdown));
            AddSyncCardCommand     = new RelayCommand(() => AddCard(CardTypeSync));
            AddLinkCardCommand     = new RelayCommand(() => AddCard(CardTypeLink));
            AddButtonCardCommand   = new RelayCommand(() => AddCard(CardTypeButton));
            AddSearchCardCommand   = new RelayCommand(() => AddCard(CardTypeSearch));
            RemoveCardCommand      = new RelayCommand(p => { if (p is CardRowVm vm) RemoveCard(vm); });
            MoveCardUpCommand      = new RelayCommand(
                p =>
                {
                    if (!(p is CardRowVm vm)) return;
                    int cardIdx = Cards.IndexOf(vm);
                    if (cardIdx > 0)
                    {
                        MoveCardUp(vm);
                    }
                    else if (_allGroups != null)
                    {
                        int groupIdx = _allGroups.IndexOf(this);
                        if (groupIdx > 0)
                        {
                            var target = _allGroups[groupIdx - 1];
                            // V1: cross-group card moves must stay within same Normal/Expert section
                            if (target.IsExpert != this.IsExpert) return;
                            var card = vm.Card;
                            RemoveCard(vm);
                            target.InsertCardAt(card, target.Cards.Count);
                            _activateCard?.Invoke(target, target.Cards[target.Cards.Count - 1]);
                        }
                    }
                },
                p =>
                {
                    if (!(p is CardRowVm vm)) return false;
                    int cardIdx = Cards.IndexOf(vm);
                    if (cardIdx > 0) return true;
                    if (_allGroups == null) return false;
                    int gIdx = _allGroups.IndexOf(this);
                    // V1: must have a previous group in the SAME section
                    return gIdx > 0 && _allGroups[gIdx - 1].IsExpert == this.IsExpert;
                });
            MoveCardDownCommand    = new RelayCommand(
                p =>
                {
                    if (!(p is CardRowVm vm)) return;
                    int cardIdx = Cards.IndexOf(vm);
                    if (cardIdx < Cards.Count - 1)
                    {
                        MoveCardDown(vm);
                    }
                    else if (_allGroups != null)
                    {
                        int groupIdx = _allGroups.IndexOf(this);
                        if (groupIdx < _allGroups.Count - 1)
                        {
                            var target = _allGroups[groupIdx + 1];
                            // V1: cross-group card moves must stay within same Normal/Expert section
                            if (target.IsExpert != this.IsExpert) return;
                            var card = vm.Card;
                            RemoveCard(vm);
                            target.InsertCardAt(card, 0);
                            _activateCard?.Invoke(target, target.Cards[0]);
                        }
                    }
                },
                p =>
                {
                    if (!(p is CardRowVm vm)) return false;
                    int cardIdx = Cards.IndexOf(vm);
                    if (cardIdx >= 0 && cardIdx < Cards.Count - 1) return true;
                    if (_allGroups == null) return false;
                    int gIdx = _allGroups.IndexOf(this);
                    // V1: must have a next group in the SAME section
                    return gIdx >= 0 && gIdx < _allGroups.Count - 1
                        && _allGroups[gIdx + 1].IsExpert == this.IsExpert;
                });
            DuplicateCardCommand   = new RelayCommand(p => { if (p is CardRowVm vm) DuplicateCard(vm); });
            RemoveGroupCommand     = new RelayCommand(() => _onRemove?.Invoke(this));
            ToggleCollapseCommand  = new RelayCommand(() => IsCollapsed = !IsCollapsed);
            ToggleExpertCommand    = new RelayCommand(() => IsExpert = !IsExpert);
            ToggleSortCommand      = new RelayCommand(() => OrderByPlacing = !OrderByPlacing);
            MoveGroupUpCommand     = new RelayCommand(MoveGroupUp,    CanMoveGroupUp);
            MoveGroupDownCommand   = new RelayCommand(MoveGroupDown,  CanMoveGroupDown);
            DuplicateGroupCommand  = new RelayCommand(() => _duplicateGroup?.Invoke(this));
        }

        // ── Per-group ▲▼ (used by collapsed-row buttons). Section-restricted: same IsExpert only. ──

        private bool CanMoveGroupUp()
        {
            if (_allGroups == null) return false;
            int idx = _allGroups.IndexOf(this);
            return idx > 0 && _allGroups[idx - 1].IsExpert == IsExpert;
        }
        private bool CanMoveGroupDown()
        {
            if (_allGroups == null) return false;
            int idx = _allGroups.IndexOf(this);
            return idx >= 0 && idx < _allGroups.Count - 1 && _allGroups[idx + 1].IsExpert == IsExpert;
        }
        private void MoveGroupUp()
        {
            if (!CanMoveGroupUp()) return;
            int idx = _allGroups.IndexOf(this);
            _moveGroup?.Invoke(idx, idx - 1);
        }
        private void MoveGroupDown()
        {
            if (!CanMoveGroupDown()) return;
            int idx = _allGroups.IndexOf(this);
            _moveGroup?.Invoke(idx, idx + 1);
        }

        // ── Target Field Picker (P3-B enhanced popup) ────────────────────────────

        private string _targetFieldPickerFilter = "";
        private bool   _isTargetFieldPickerOpen;
        private List<string> _targetFieldPickerPinnedKeys = new();

        public bool IsTargetFieldPickerOpen
        {
            get => _isTargetFieldPickerOpen;
            set { _isTargetFieldPickerOpen = value; OnPropertyChanged(); }
        }

        public string TargetFieldPickerFilter
        {
            get => _targetFieldPickerFilter;
            set { _targetFieldPickerFilter = value ?? ""; OnPropertyChanged(); ApplyTargetFieldPickerFilter(); }
        }

        public ObservableCollection<FieldSelectorGroupVm> TargetFieldPickerGroups   { get; } = new();
        public ObservableCollection<PinnedFieldEntry>      TargetFieldPickerFavoriten { get; } = new();

        public bool HasTargetFieldPickerFavoriten => TargetFieldPickerFavoriten.Count > 0;

        public string TargetFieldDropText
        {
            get
            {
                if (string.IsNullOrEmpty(Group.TargetFieldKey)) return "—";
                var fi = AvailableTargetFields.FirstOrDefault(f => f.Key == Group.TargetFieldKey);
                return fi?.DropText ?? Group.TargetFieldKey;
            }
        }

        public void OpenTargetFieldPicker()
        {
            if (TargetFieldPickerGroups.Count == 0)
                BuildTargetFieldPickerGroups();
            RebuildTargetFieldPickerFavoriten();
            IsTargetFieldPickerOpen = true;
        }

        public void OnTargetFieldPickerClosed()
        {
            _targetFieldPickerFilter = "";
            OnPropertyChanged(nameof(TargetFieldPickerFilter));
            ApplyTargetFieldPickerFilter();
        }

        private void BuildTargetFieldPickerGroups()
        {
            var byGroup = AvailableTargetFields
                .Where(f => !string.IsNullOrEmpty(f.Key) && !f.IsActionItem)
                .GroupBy(f => f.GroupName)
                .OrderBy(g => TfGroupOrder(g.Key))
                .ToList();

            foreach (var g in byGroup)
            {
                if (string.IsNullOrEmpty(g.Key)) continue; // GRP_NONE

                var allItems = g
                    .OrderBy(f => f.DropText, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                bool isCollapsed = Services.UiStateStore.LoadFieldSelGroupCollapsed(g.Key);
                string displayName = LanguageLoader.Get(g.Key);

                var gvm = new FieldSelectorGroupVm
                {
                    GroupName        = g.Key,
                    GroupDisplayName = displayName,
                    AllItems         = allItems,
                    FilteredItems    = allItems,
                    IsCollapsed      = isCollapsed,
                    IsChevronEnabled = true,
                };
                var captured = gvm;
                gvm.ToggleCollapseCommand = new RelayCommand(() =>
                {
                    if (!captured.IsChevronEnabled) return;
                    captured.IsCollapsed = !captured.IsCollapsed;
                    Services.UiStateStore.SaveFieldSelGroupCollapsed(captured.GroupName, captured.IsCollapsed);
                });

                TargetFieldPickerGroups.Add(gvm);
            }
        }

        private static int TfGroupOrder(string groupName) => groupName switch
        {
            "Grp_Special"            => 0,
            "Grp_iPropertiesCustom"  => 1,
            "Grp_ParamUser"          => 2,
            "Grp_iProperties"        => 3,
            "Grp_Document"           => 4,
            "Grp_ParamModel"         => 5,
            _                        => 99,
        };

        private void ApplyTargetFieldPickerFilter()
        {
            string filter = _targetFieldPickerFilter?.Trim() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var gvm in TargetFieldPickerGroups)
            {
                if (hasFilter)
                {
                    var matched = gvm.AllItems
                        .Where(f => f.DropText.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        .ToList();
                    gvm.FilteredItems = matched;
                    if (matched.Count > 0) gvm.IsCollapsed = false;
                }
                else
                {
                    gvm.FilteredItems = gvm.AllItems;
                    gvm.IsCollapsed   = Services.UiStateStore.LoadFieldSelGroupCollapsed(gvm.GroupName);
                }
            }
        }

        public void RebuildTargetFieldPickerFavoriten()
        {
            string saved = Services.UiStateStore.LoadFieldSelPinnedFields();
            _targetFieldPickerPinnedKeys = string.IsNullOrEmpty(saved)
                ? new List<string>()
                : saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            TargetFieldPickerFavoriten.Clear();
            foreach (string key in _targetFieldPickerPinnedKeys)
            {
                var fi    = AvailableTargetFields.FirstOrDefault(f => f.Key == key);
                bool avail = fi != null;
                if (!avail) fi = new FieldItem(key, key, key);
                TargetFieldPickerFavoriten.Add(new PinnedFieldEntry { Item = fi, IsAvailable = avail });
            }
            OnPropertyChanged(nameof(HasTargetFieldPickerFavoriten));
        }

        public void ToggleTargetFieldPin(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_targetFieldPickerPinnedKeys.Contains(key))
                _targetFieldPickerPinnedKeys.Remove(key);
            else
                _targetFieldPickerPinnedKeys.Insert(0, key);
            Services.UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _targetFieldPickerPinnedKeys));
            RebuildTargetFieldPickerFavoriten();
        }

        public void ReorderTargetFieldPin(string fromKey, string toKey)
        {
            int fromIdx = _targetFieldPickerPinnedKeys.IndexOf(fromKey);
            int toIdx   = _targetFieldPickerPinnedKeys.IndexOf(toKey);
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
            _targetFieldPickerPinnedKeys.RemoveAt(fromIdx);
            _targetFieldPickerPinnedKeys.Insert(toIdx, fromKey);
            Services.UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _targetFieldPickerPinnedKeys));
            RebuildTargetFieldPickerFavoriten();
        }

        public string TargetFieldKey
        {
            get => Group.TargetFieldKey;
            set
            {
                if (Group.TargetFieldKey == (value ?? "")) return;
                Group.TargetFieldKey = value ?? "";
                _onSave?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(TargetFieldDropText));
            }
        }

        public string Name
        {
            get => Group.Name;
            set
            {
                if (Group.Name == (value ?? "")) return;
                Group.Name = value ?? "";
                _onSave?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── F1 Collapsibility + V1 Expert Mode (per-group) ────────────────────
        private bool _isCollapsed;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed == value) return;
                _isCollapsed = value;
                Services.UiStateStore.SaveGroupCollapsed(Group.Id ?? "", value);
                OnPropertyChanged();
            }
        }

        public bool IsExpert
        {
            get => Group.IsExpert;
            set
            {
                if (Group.IsExpert == value) return;
                Group.IsExpert = value;
                _onSave?.Invoke();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SectionLabel));
                PropagateIsExpertToCards();
                _onExpertModeChanged?.Invoke(this);
            }
        }

        /// <summary>T43 — the group ⇅ sort toggle (D-sort-1). Mirrors IsExpert: persists to the model + saves.</summary>
        public bool OrderByPlacing
        {
            get => Group.OrderCompanionByPlacing;
            set
            {
                if (Group.OrderCompanionByPlacing == value) return;
                Group.OrderCompanionByPlacing = value;
                _onSave?.Invoke();
                OnPropertyChanged();
            }
        }

        private void PropagateIsExpertToCards()
        {
            foreach (var c in Cards) c.IsExpertModeGroup = Group.IsExpert;
        }

        /// <summary>Section label used by visual divider — "Normal" or "Expert".</summary>
        public string SectionLabel => Group.IsExpert ? "Expert" : "Normal";

        private int _orderNumber;
        /// <summary>1-based sequential number across Normal+Expert sections; set by parent VM.</summary>
        public int OrderNumber
        {
            get => _orderNumber;
            set { if (_orderNumber == value) return; _orderNumber = value; OnPropertyChanged(); }
        }

        private bool _isFirstExpert;
        /// <summary>True when this is the first group in the Expert section — drives section divider visibility.</summary>
        public bool IsFirstExpert
        {
            get => _isFirstExpert;
            internal set { if (_isFirstExpert == value) return; _isFirstExpert = value; OnPropertyChanged(); }
        }

        private int _expertTopoOrder;
        /// <summary>0 = Normal group; 1..N = evaluation order in topo sort; -1 = part of a cycle. Set by parent VM.</summary>
        public int ExpertTopoOrder
        {
            get => _expertTopoOrder;
            internal set
            {
                if (_expertTopoOrder == value) return;
                _expertTopoOrder = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpertTopoLabel));
                OnPropertyChanged(nameof(HasExpertTopoLabel));
            }
        }

        /// <summary>Display text for the topo order badge: "1".."N", "⟳" for cycle, "" for Normal groups.</summary>
        public string ExpertTopoLabel => _expertTopoOrder > 0 ? _expertTopoOrder.ToString()
                                       : _expertTopoOrder == -1 ? "⟳" : "";

        /// <summary>True when the topo badge should be shown (Expert group with resolved or cycle order).</summary>
        public bool HasExpertTopoLabel => _expertTopoOrder != 0;

        /// <summary>Count of enabled non-BasicLogic cards (for collapsed-group CardsPill).</summary>
        public int CardsCount
        {
            get
            {
                int n = 0;
                foreach (var c in Group.Cards)
                    if (c.Enabled && c.Type != CardTypeBasicLogic) n++;
                return n;
            }
        }

        /// <summary>Count of enabled BasicLogic cards (for collapsed-group BLPill).</summary>
        public int BLCount
        {
            get
            {
                int n = 0;
                foreach (var c in Group.Cards)
                    if (c.Enabled && c.Type == CardTypeBasicLogic) n++;
                return n;
            }
        }

        public bool HasCards => CardsCount > 0;
        public bool HasBLs   => BLCount   > 0;

        /// <summary>
        /// Aggregated per-type pills for the collapsed group header (F1 design 2026-05-20):
        /// one pill per distinct card type, with "×N" appended when multiple cards of that type exist.
        /// Counts only Enabled cards. Stable ordering by CardEngine type-constant declaration order.
        /// </summary>
        public IReadOnlyList<CardTypePill> CardTypePills
        {
            get
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var c in Group.Cards)
                    if (c.Enabled && !string.IsNullOrEmpty(c.Type))
                    {
                        counts.TryGetValue(c.Type, out int n);
                        counts[c.Type] = n + 1;
                    }
                // Stable order: declaration order from CardEngine constants
                var order = new[]
                {
                    CardTypeDropdown, CardTypeButton, CardTypeSearch, CardTypeMultiPick,
                    CardTypeLink, CardTypeSync, CardTypePairTransform,
                    CardTypePrefixSuffix, CardTypeSort, CardTypeCompose, CardTypeBasicLogic,
                };
                var result = new List<CardTypePill>(counts.Count);
                foreach (var t in order)
                {
                    if (counts.TryGetValue(t, out int n) && n > 0)
                        result.Add(new CardTypePill(t, n));
                }
                // Any unrecognised types (forward-compat)
                foreach (var kv in counts)
                {
                    if (Array.IndexOf(order, kv.Key) < 0)
                        result.Add(new CardTypePill(kv.Key, kv.Value));
                }
                return result;
            }
        }

        internal void NotifyCountsChanged()
        {
            OnPropertyChanged(nameof(CardsCount));
            OnPropertyChanged(nameof(BLCount));
            OnPropertyChanged(nameof(HasCards));
            OnPropertyChanged(nameof(HasBLs));
            OnPropertyChanged(nameof(CardTypePills));
        }

        /// <summary>Assigns 1-based OrderNumber to each card based on its current index.</summary>
        internal void RenumberCards()
        {
            for (int i = 0; i < Cards.Count; i++)
                Cards[i].OrderNumber = i + 1;
        }

        private void OnCardsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (CardRowVm vm in e.OldItems) vm.PropertyChanged -= OnCardRowPropertyChanged;
            if (e.NewItems != null)
                foreach (CardRowVm vm in e.NewItems) vm.PropertyChanged += OnCardRowPropertyChanged;
            NotifyCountsChanged();
            RenumberCards();
        }

        private void OnCardRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CardRowVm.Enabled))
                NotifyCountsChanged();
        }

        private CardRowVm MakeCardVm(CapabilityCard card)
        {
            var vm = new CardRowVm(card, _onSave, AvailableTargetFields, AvailableCatalogs, _getRoles);
            vm.IsExpertModeGroup = Group.IsExpert;
            return vm;
        }

        internal void AddCard(string type)
        {
            var card = new CapabilityCard { Type = type, Enabled = true };
            Group.Cards.Add(card);
            _onSave?.Invoke();
            Cards.Add(MakeCardVm(card));
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Adds a Basic Logic card with the given formula skeleton pre-filled.</summary>
        internal void AddBasicLogicCard(string formulaSkeleton)
        {
            var card = new CapabilityCard { Type = CardTypeBasicLogic, Enabled = true };
            card.Params[ParamFormula] = formulaSkeleton ?? "";
            Group.Cards.Add(card);
            _onSave?.Invoke();
            Cards.Add(MakeCardVm(card));
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void RemoveCard(CardRowVm vm)
        {
            var card = Group.Cards.Find(c => ReferenceEquals(c, vm.Card));
            if (card != null) Group.Cards.Remove(card);
            Cards.Remove(vm);
            _onSave?.Invoke();
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void MoveCardUp(CardRowVm vm)
        {
            int idx = Cards.IndexOf(vm);
            if (idx <= 0) return;
            Group.Cards.RemoveAt(idx);
            Group.Cards.Insert(idx - 1, vm.Card);
            _onSave?.Invoke();
            Cards.Move(idx, idx - 1);
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void MoveCardDown(CardRowVm vm)
        {
            int idx = Cards.IndexOf(vm);
            if (idx < 0 || idx >= Cards.Count - 1) return;
            Group.Cards.RemoveAt(idx);
            Group.Cards.Insert(idx + 1, vm.Card);
            _onSave?.Invoke();
            Cards.Move(idx, idx + 1);
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void DuplicateCard(CardRowVm vm)
        {
            int idx = Cards.IndexOf(vm);
            if (idx < 0) return;
            var newCard = new CapabilityCard
            {
                Type      = vm.Card.Type,
                Enabled   = vm.Card.Enabled,
                CatalogId = vm.Card.CatalogId,
                Params    = new Dictionary<string, string>(vm.Card.Params),
            };
            Group.Cards.Insert(idx + 1, newCard);
            _onSave?.Invoke();
            Cards.Insert(idx + 1, MakeCardVm(newCard));
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void InsertCardAt(CapabilityCard card, int idx)
        {
            idx = Math.Max(0, Math.Min(idx, Cards.Count));
            Group.Cards.Insert(idx, card);
            Cards.Insert(idx, MakeCardVm(card));
            _onSave?.Invoke();
            RelayCommand.RaiseCanExecuteChanged();
        }

        internal void MoveCardTo(int fromIdx, int toIdx)
        {
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
            if (fromIdx >= Cards.Count || toIdx >= Cards.Count) return;
            var card = Group.Cards[fromIdx];
            Group.Cards.RemoveAt(fromIdx);
            Group.Cards.Insert(toIdx, card);
            _onSave?.Invoke();
            Cards.Move(fromIdx, toIdx);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One display column slot in the Catalog Builder card editor (Dropdown / Button cards).
    /// Configures which catalog role is shown as an additional visual column in the picker window.
    /// Only the PRI column is written on selection — display columns are read-only in the UI.
    /// </summary>
    public sealed class DisplayColumnVm : INotifyPropertyChanged
    {
        private string _role = "";

        public int                   Index                 { get; }
        public IReadOnlyList<string> AvailableCatalogRoles { get; }

        internal Action<DisplayColumnVm, string, string> OnRoleChanged;

        public DisplayColumnVm(int index, string role, IReadOnlyList<string> catalogRoles)
        {
            Index                 = index;
            _role                 = role  ?? "";
            AvailableCatalogRoles = catalogRoles ?? Array.Empty<string>();
        }

        public string Role
        {
            get => _role;
            set
            {
                string old = _role;
                string nv  = value ?? "";
                if (old == nv) return;
                _role = nv;
                OnPropertyChanged();
                OnRoleChanged?.Invoke(this, old, nv);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Wraps a ColumnRole enum value with a display name and a one-line tooltip
    /// for the role ComboBox in the Catalog Builder bottom bar.
    /// </summary>
    public sealed class ColumnRoleItem
    {
        public ColumnRole Role        { get; }
        public string     DisplayName { get; }
        public string     Tooltip     { get; }

        public ColumnRoleItem(ColumnRole role, string displayName, string tooltip)
        {
            Role = role; DisplayName = displayName; Tooltip = tooltip;
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// One pill in the collapsed-group header showing a card type and its count
    /// (e.g. "Dropdown ×2"). Count of 1 is rendered without the "×1" suffix.
    /// </summary>
    public sealed class CardTypePill
    {
        public string CardType  { get; }
        public int    Count     { get; }
        public string TypeLabel => LanguageLoader.Get(CardType switch
        {
            CheckupAddIn.Services.CardEngine.CardTypeDropdown      => "CardType_Dropdown",
            CheckupAddIn.Services.CardEngine.CardTypeSync          => "CardType_Sync",
            CheckupAddIn.Services.CardEngine.CardTypeLink          => "CardType_Link",
            CheckupAddIn.Services.CardEngine.CardTypeButton        => "CardType_Button",
            CheckupAddIn.Services.CardEngine.CardTypeSearch        => "CardType_Search",
            CheckupAddIn.Services.CardEngine.CardTypeMultiPick     => "CardType_SmartComplete",
            CheckupAddIn.Services.CardEngine.CardTypePairTransform => "CardType_PairTransform",
            CheckupAddIn.Services.CardEngine.CardTypePrefixSuffix  => "CardType_PrefixSuffix",
            CheckupAddIn.Services.CardEngine.CardTypeSort          => "CardType_Sort",
            CheckupAddIn.Services.CardEngine.CardTypeCompose       => "CardType_Compose",
            CheckupAddIn.Services.CardEngine.CardTypeBasicLogic    => "CardType_BasicLogic",
            _                                                       => CardType,
        });
        public string DisplayText => Count > 1 ? $"{TypeLabel} ×{Count}" : TypeLabel;

        public CardTypePill(string cardType, int count) { CardType = cardType; Count = count; }
    }
}
