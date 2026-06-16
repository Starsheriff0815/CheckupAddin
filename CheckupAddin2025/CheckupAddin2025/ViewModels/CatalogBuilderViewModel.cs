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
        private readonly List<FieldItem> _availableTargetFields = new List<FieldItem>();

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
        public ObservableCollection<CatalogData> Catalogs { get; } = new ObservableCollection<CatalogData>();

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
        private bool   _lastSortAscending = true;

        public CatalogData SelectedCatalog
        {
            get => _selectedCatalog;
            set
            {
                if (value == _selectedCatalog) return;
                if (value != null) UiStateStore.SaveLastCatalogId(value.Id);

                if (_isDirty && _selectedCatalog != null)
                {
                    var choice = PromptUnsavedChanges?.Invoke();   // true=save, false=discard, null=cancel
                    if (choice == null)
                    {
                        OnPropertyChanged(nameof(SelectedCatalog));
                        return;
                    }
                    if (choice == true) CommitAndSave();
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

        public bool HasSelectedCatalog                => _workingCopy != null;
        public bool IsSelectedCatalogOnUncPath        => _selectedCatalog?.IsOnUncPath == true;
        public bool IsSelectedCatalogLocked           => _selectedCatalog?.IsOnUncPath == true
                                                      || _selectedCatalog?.IsLocked == true;
        public bool IsSelectedCatalogEditable         => HasSelectedCatalog && !IsSelectedCatalogLocked;
        public bool IsSelectedCatalogUpdateAvailable  => _selectedCatalog?.HasUpdateAvailable == true;
        public bool IsSelectedCatalogLockedNoUpdate   => IsSelectedCatalogLocked && !IsSelectedCatalogUpdateAvailable;

        // ── Capability sets ──
        public ObservableCollection<CapabilitySet> CapabilitySets { get; } = new ObservableCollection<CapabilitySet>();

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

        public bool HasSelectedCapabilitySet          => _selectedCapabilitySet != null;
        public bool HasNoSelectedCapabilitySet        => _selectedCapabilitySet == null;
        public bool HasAnyExpertGroup                 => CapSetGroups.Any(g => g.IsExpert);
        public bool IsSelectedCapSetOnUncPath         => _selectedCapabilitySet?.IsOnUncPath == true;
        public bool IsSelectedCapSetLocked            => _selectedCapabilitySet?.IsOnUncPath == true
                                                     || _selectedCapabilitySet?.IsLocked == true;
        public bool IsSelectedCapSetEditable          => HasSelectedCapabilitySet && !IsSelectedCapSetLocked;
        public bool IsSelectedCapSetUpdateAvailable   => _selectedCapabilitySet?.HasUpdateAvailable == true;
        public bool IsSelectedCapSetLockedNoUpdate    => IsSelectedCapSetLocked && !IsSelectedCapSetUpdateAvailable;

        /// <summary>All selectable target fields (excludes SPECIAL:LOGIC:* to prevent self-reference).</summary>
        public IReadOnlyList<FieldItem> AvailableTargetFields => _availableTargetFields;

        /// <summary>Same as AvailableTargetFields but wrapped in a ListCollectionView with group headers.</summary>
        public ListCollectionView GroupedAvailableTargetFields { get; private set; }

        // ── Columns ──
        public ObservableCollection<CatalogColumn> CurrentColumns { get; } = new ObservableCollection<CatalogColumn>();

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

        public bool HasSelectedColumn         => _selectedColumn != null;
        public bool HasEditableSelectedColumn  => HasSelectedColumn && IsSelectedCatalogEditable;

        public static ColumnRoleItem[] ColumnRoleItems { get; } = new[]
        {
            new ColumnRoleItem(ColumnRole.None,             "—   None",              "Keine besondere Funktion. Hilfsspalte oder interne Kennung."),
            new ColumnRoleItem(ColumnRole.PrimaryDisplay,   "PRI  PrimaryDisplay",   "Kurzform (Feld 1): z. B. der Token in SPEZIFIK 1."),
            new ColumnRoleItem(ColumnRole.SecondaryDisplay, "SEC  SecondaryDisplay", "Langform (Feld 2): z. B. der Token in SPEZIFIK 2."),
            new ColumnRoleItem(ColumnRole.TabId,            "TAB  TabId",            "Reiter-Kennung: jeder eindeutige Wert wird ein Tab in der Auswahlmaske."),
            new ColumnRoleItem(ColumnRole.GroupId,          "GRP  GroupId",          "Reiter-Titel: lesbares Label für den Reiter."),
            new ColumnRoleItem(ColumnRole.SortKey,          "SRT  SortKey",          "Sortierung der Einträge innerhalb einer Gruppe; mehrfach → SRT1, SRT2 …"),
            new ColumnRoleItem(ColumnRole.GroupSortKey,     "GST  GroupSortKey",     "Sortierung der Gruppen innerhalb eines Tabs; mehrfach → GST1, GST2 …"),
            new ColumnRoleItem(ColumnRole.TabSortKey,       "TST  TabSortKey",       "Sortierung der Tabs in der Auswahlmaske; mehrfach → TST1, TST2 …"),
            new ColumnRoleItem(ColumnRole.Auxiliary,        "AUX  Auxiliary",        "Hilfsdaten: z. B. Tooltip-Text — wird nicht in Felder geschrieben."),
        };

        public ColumnRole SelectedColumnRole
        {
            get => _selectedColumn?.Role ?? ColumnRole.None;
            set
            {
                if (_selectedColumn == null) return;
                var oldRole = _selectedColumn.Role;
                if (oldRole == value) return;

                _selectedColumn.Role      = ColumnRole.None;
                _selectedColumn.RoleIndex = 1;
                if (oldRole != ColumnRole.None)
                    CompactRoleIndices(oldRole);

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
            int tmp = sameType[pos].RoleIndex;
            sameType[pos].RoleIndex      = sameType[targetPos].RoleIndex;
            sameType[targetPos].RoleIndex = tmp;
            IsDirty = true;
            RoleIndicesChanged?.Invoke();
        }

        public event Action RoleIndicesChanged;

        // ── Entries ──
        public ObservableCollection<EntryRow> EntryRows { get; } = new ObservableCollection<EntryRow>();

        private EntryRow _selectedEntry;
        public EntryRow SelectedEntry
        {
            get => _selectedEntry;
            set { _selectedEntry = value; OnPropertyChanged(); }
        }

        public event Action ColumnsChanged;

        // ── Dialog delegates ──
        public Func<string, string, string> AskForText              { get; set; }
        public Func<bool>                   ConfirmDelete           { get; set; }
        public Func<bool?>                  PromptUnsavedChanges    { get; set; }
        public Func<string>                 PickSaveFile            { get; set; }
        public Func<string>                 PickOpenFile            { get; set; }
        public Func<string>                 PickCapSetSaveFile      { get; set; }
        public Func<string>                 PickCapSetOpenFile      { get; set; }

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
        public RelayCommand SwitchToCatalogsCommand     { get; }
        public RelayCommand SwitchToCapabilitiesCommand { get; }

        // ── Groups for the selected capability set ──
        public ObservableCollection<CardGroupVm> CapSetGroups { get; } = new ObservableCollection<CardGroupVm>();

        public bool HasCapSetGroups   => CapSetGroups.Count > 0;
        public bool HasNoCapSetGroups => CapSetGroups.Count == 0;

        // ── Active selection state ──
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

        public Func<bool> ConfirmUpdateCatalog { get; set; }
        public Func<bool> ConfirmUpdateCapSet  { get; set; }

        // ── Commands — global active-item actions ──
        public RelayCommand MoveActiveUpCommand              { get; }
        public RelayCommand MoveActiveDownCommand            { get; }
        public RelayCommand DuplicateActiveCommand           { get; }
        public RelayCommand RemoveActiveCommand              { get; }
        public RelayCommand AddDropdownToActiveCommand       { get; }
        public RelayCommand AddSyncToActiveCommand           { get; }
        public RelayCommand AddLinkToActiveCommand           { get; }
        public RelayCommand AddButtonToActiveCommand         { get; }
        public RelayCommand AddSearchToActiveCommand         { get; }
        public RelayCommand AddMultiPickToActiveCommand      { get; }
        public RelayCommand AddPairTransformToActiveCommand  { get; }
        public RelayCommand AddFormulaToActiveCommand        { get; }
        public RelayCommand AddPrefixSuffixToActiveCommand   { get; }
        public RelayCommand AddSortToActiveCommand           { get; }
        public RelayCommand ToggleBasicLogicsPanelCommand    { get; }
        public RelayCommand ToggleCardPanelCommand           { get; }

        // ── Basic Logic formula shortcuts ──
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

        public CatalogBuilderViewModel(CatalogStore store, CapabilityStore capStore = null,
                                       IReadOnlyList<FieldItem> availableFields = null)
        {
            _store    = store;
            _capStore = capStore;
            foreach (var c in store.Catalogs) Catalogs.Add(c);
            if (capStore != null)
                foreach (var s in capStore.CapabilitySets) CapabilitySets.Add(s);

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
                    if (ConfirmUpdateCatalog != null && !ConfirmUpdateCatalog()) return;
                    var updated = _store.RevertToDistribution(_selectedCatalog);
                    if (updated == null) return;
                    int idx = Catalogs.IndexOf(_selectedCatalog);
                    if (idx >= 0) Catalogs[idx] = updated;
                    else Catalogs.Add(updated);
                    SelectedCatalog = updated;
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => _selectedCatalog?.HasUpdateAvailable == true);

            UpdateCapSetCommand = new RelayCommand(
                () =>
                {
                    if (_selectedCapabilitySet == null) return;
                    if (ConfirmUpdateCapSet != null && !ConfirmUpdateCapSet()) return;
                    var updated = _capStore?.RevertToDistribution(_selectedCapabilitySet);
                    if (updated == null) return;
                    int idx = CapabilitySets.IndexOf(_selectedCapabilitySet);
                    if (idx >= 0) CapabilitySets[idx] = updated;
                    else CapabilitySets.Add(updated);
                    SelectedCapabilitySet = updated;
                    RelayCommand.RaiseCanExecuteChanged();
                },
                () => _selectedCapabilitySet?.HasUpdateAvailable == true);

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
                                var card = _activeCardVm.Card;
                                _activeGroupVm.RemoveCard(_activeCardVm);
                                var target = CapSetGroups[groupIdx - 1];
                                target.InsertCardAt(card, target.Cards.Count);
                                OnGroupCardActivated(target, target.Cards[target.Cards.Count - 1]);
                            }
                        }
                    }
                    else if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        if (idx > 0) OnGroupDragDropCompleted(idx, idx - 1);
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
                        return CapSetGroups.IndexOf(_activeGroupVm) > 0;
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
                                var card = _activeCardVm.Card;
                                _activeGroupVm.RemoveCard(_activeCardVm);
                                var target = CapSetGroups[groupIdx + 1];
                                target.InsertCardAt(card, 0);
                                OnGroupCardActivated(target, target.Cards[0]);
                            }
                        }
                    }
                    else if (_activeGroupVm != null)
                    {
                        int idx = CapSetGroups.IndexOf(_activeGroupVm);
                        if (idx < CapSetGroups.Count - 1) OnGroupDragDropCompleted(idx, idx + 1);
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
                        return idx >= 0 && idx < CapSetGroups.Count - 1;
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

            AddDropdownToActiveCommand      = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeDropdown),     () => HasEditableActiveGroup);
            AddSyncToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSync),         () => HasEditableActiveGroup);
            AddLinkToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeLink),         () => HasEditableActiveGroup);
            AddButtonToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeButton),       () => HasEditableActiveGroup);
            AddSearchToActiveCommand        = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSearch),       () => HasEditableActiveGroup);
            AddMultiPickToActiveCommand     = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeMultiPick),    () => HasEditableActiveGroup);
            AddPairTransformToActiveCommand = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypePairTransform),() => HasEditableActiveGroup);
            AddPrefixSuffixToActiveCommand  = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypePrefixSuffix), () => HasEditableActiveGroup);
            AddSortToActiveCommand          = new RelayCommand(() => _activeGroupVm?.AddCard(CardTypeSort),         () => HasEditableActiveGroup);
            ToggleBasicLogicsPanelCommand   = new RelayCommand(() => IsBasicLogicsPanelOpen = !IsBasicLogicsPanelOpen);
            ToggleCardPanelCommand          = new RelayCommand(() => IsCardPanelOpen         = !IsCardPanelOpen);

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

            CapSetGroups.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasCapSetGroups));
                OnPropertyChanged(nameof(HasNoCapSetGroups));
            };

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

        // ── Capability set operations ──

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
            catch (Exception ex) { DiagLogger.Log("catalog", "ExportCapSet failed: " + DiagLogger.S(ex.Message)); }
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
            catch (Exception ex) { DiagLogger.Log("catalog", "ImportCapSet failed: " + DiagLogger.S(ex.Message)); }
        }

        // ── Group management ──

        internal IReadOnlyList<string> GetCatalogRolesForId(string catalogId)
        {
            if (string.IsNullOrEmpty(catalogId)) return System.Array.Empty<string>();
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
            foreach (var g in ordered) _selectedCapabilitySet.Groups.Add(g);

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

        /// <summary>Called when a group's IsExpert flag flips. Moves the group to the correct section + renumbers.</summary>
        internal void OnGroupExpertModeChanged(CardGroupVm groupVm)
        {
            int oldIdx = CapSetGroups.IndexOf(groupVm);
            if (oldIdx < 0) return;
            var group = groupVm.Group;
            _selectedCapabilitySet.Groups.Remove(group);
            int insertAt = group.IsExpert
                ? _selectedCapabilitySet.Groups.Count
                : _selectedCapabilitySet.Groups.FindIndex(g => g.IsExpert);
            if (insertAt < 0) insertAt = _selectedCapabilitySet.Groups.Count;
            _selectedCapabilitySet.Groups.Insert(insertAt, group);
            CapSetGroups.Move(oldIdx, insertAt);
            RenumberGroups();
            RecomputeExpertTopoOrder();
            OnPropertyChanged(nameof(HasAnyExpertGroup));
        }

        /// <summary>Computes topological evaluation order for Expert groups.</summary>
        private void RecomputeExpertTopoOrder()
        {
            try
            {
                foreach (var g in CapSetGroups)
                    if (!g.IsExpert) g.ExpertTopoOrder = 0;

                var expertVms = new List<CardGroupVm>();
                foreach (var g in CapSetGroups)
                    if (g.IsExpert) expertVms.Add(g);

                if (expertVms.Count == 0) return;

                var gidToVm    = new Dictionary<string, CardGroupVm>(expertVms.Count);
                foreach (var g in expertVms) gidToVm[g.Group.Id] = g;
                var expertGids = new System.Collections.Generic.HashSet<string>(gidToVm.Keys);
                var inDegree   = new Dictionary<string, int>(expertVms.Count);
                var outEdges   = new Dictionary<string, List<string>>(expertVms.Count);
                foreach (string id in expertGids) { inDegree[id] = 0; outEdges[id] = new List<string>(); }

                foreach (var g in expertVms)
                {
                    string gid = g.Group.Id;
                    foreach (var card in g.Cards)
                    {
                        if (card.Card.Type != CardEngine.CardTypeBasicLogic) continue;
                        string formula;
                        if (!card.Card.Params.TryGetValue(CardEngine.ParamFormula, out formula)) continue;
                        foreach (string refKey in FormulaEngine.GetExpertRefs(formula))
                        {
                            if (!refKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                            string refGid = refKey.Substring("SPECIAL:LOGIC:".Length);
                            if (!expertGids.Contains(refGid) || refGid == gid) continue;
                            inDegree[gid]++;
                            outEdges[refGid].Add(gid);
                        }
                    }
                }

                var queue     = new Queue<string>();
                foreach (var kv in inDegree) if (kv.Value == 0) queue.Enqueue(kv.Key);
                var topoOrder = new List<string>();
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
                {
                    CardGroupVm vm;
                    if (kv.Value > 0 && gidToVm.TryGetValue(kv.Key, out vm))
                        vm.ExpertTopoOrder = -1;
                }
            }
            catch { }
        }

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
                IsExpert       = src.IsExpert,
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
            var ownView = new ListCollectionView(_availableTargetFields);
            ownView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldItem.GroupName)));
            return new CardGroupVm(group,
                () => _capStore?.Save(_selectedCapabilitySet),
                RemoveGroupVm,
                _availableTargetFields,
                ownView,
                _store?.Catalogs ?? (IReadOnlyList<CatalogData>)System.Array.Empty<CatalogData>(),
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

        // ── Catalog-level operations ──

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
            if (_workingCopy == null) return;
            string path = PickSaveFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;
            try { _store.ExportCatalog(_workingCopy, path); }
            catch (Exception ex) { DiagLogger.Log("catalog", "ExportCatalog failed: " + DiagLogger.S(ex.Message)); }
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
            catch (Exception ex) { DiagLogger.Log("catalog", "ImportCatalog failed: " + DiagLogger.S(ex.Message)); }
        }

        // ── Entry operations ──

        private void AddEntry()
        {
            if (_workingCopy == null) return;
            var entry = new CatalogEntry();

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

        // ── Column operations ──

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

        // ── Sort ──

        public bool SortByColumnKey(string key, bool? forceAscending = null)
        {
            bool ascending = forceAscending ?? !(key == _lastSortKey && _lastSortAscending);
            _lastSortKey       = key;
            _lastSortAscending = ascending;

            if (_workingCopy == null) return ascending;

            var sorted = _workingCopy.Entries.ToList();
            sorted.Sort((a, b) =>
            {
                a.Values.TryGetValue(key, out var va); if (va == null) va = "";
                b.Values.TryGetValue(key, out var vb); if (vb == null) vb = "";
                int cmp = NaturalCompare(va, vb);
                return ascending ? cmp : -cmp;
            });

            _workingCopy.Entries.Clear();
            foreach (var e in sorted) _workingCopy.Entries.Add(e);
            RefreshEntries();
            return ascending;
        }

        public bool SortRangeByColumnKey(string key, bool ascending, IList<int> rowIndices)
        {
            if (_workingCopy == null || rowIndices.Count == 0) return ascending;
            _lastSortKey       = key;
            _lastSortAscending = ascending;

            var toSort = rowIndices.Select(i => _workingCopy.Entries[i]).ToList();
            toSort.Sort((a, b) =>
            {
                a.Values.TryGetValue(key, out var va); if (va == null) va = "";
                b.Values.TryGetValue(key, out var vb); if (vb == null) vb = "";
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

        // ── Column reorder ──

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

        public IReadOnlyList<FieldItem>   AvailableTargetFields { get; }
        public IReadOnlyList<CatalogData> AvailableCatalogs     { get; }

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
            AvailableTargetFields = fields   ?? System.Array.Empty<FieldItem>();
            AvailableCatalogs     = catalogs ?? System.Array.Empty<CatalogData>();

            _availableCatalogRoles = BuildRoleList(getRoles?.Invoke(card.CatalogId));

            _isCollapsed = UiStateStore.LoadCardCollapsed(card.Id ?? "", defaultCollapsed: false);
            ToggleCollapseCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);

            InitDisplayColumns();
        }

        // ── F1 Collapsibility (per-card) ──
        private bool _isCollapsed;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed == value) return;
                _isCollapsed = value;
                UiStateStore.SaveCardCollapsed(Card.Id ?? "", value);
                OnPropertyChanged();
            }
        }
        public RelayCommand ToggleCollapseCommand { get; }

        private int _orderNumber;
        public int OrderNumber
        {
            get => _orderNumber;
            set { if (_orderNumber == value) return; _orderNumber = value; OnPropertyChanged(); }
        }

        private bool _isExpertModeGroup;
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

        // ── Per-card CatalogId ──

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
                AvailableCatalogRoles = BuildRoleList(_getRoles?.Invoke(Card.CatalogId));
            }
        }

        // ── Display column VMs ──
        public ObservableCollection<DisplayColumnVm> DisplayColumnVms { get; } = new ObservableCollection<DisplayColumnVm>();

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
                for (int n = idx; n < MaxDisplayColumns; n++)
                    Card.Params.Remove(DisplayRoleKey(n));
                while (DisplayColumnVms.Count > idx + 1)
                    DisplayColumnVms.RemoveAt(DisplayColumnVms.Count - 1);
            }
            else
            {
                Card.Params[DisplayRoleKey(idx)] = newRole;
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
            CardTypeBasicLogic    => "CardType_BasicLogic",
            CardTypeMultiPick     => "CardType_SmartComplete",
            CardTypePairTransform => "CardType_PairTransform",
            CardTypePrefixSuffix  => "CardType_PrefixSuffix",
            CardTypeSort          => "CardType_Sort",
            string t              => t,
        });

        public bool IsSync               => Card.Type == CardTypeSync;
        public bool IsDropdown           => Card.Type == CardTypeDropdown;
        public bool IsLink               => Card.Type == CardTypeLink;
        public bool IsButton             => Card.Type == CardTypeButton;
        public bool IsSearch             => Card.Type == CardTypeSearch;
        public bool IsBasicLogic         => Card.Type == CardTypeBasicLogic;
        public bool IsMultiPick          => Card.Type == CardTypeMultiPick;
        public bool IsPairTransform      => Card.Type == CardTypePairTransform;
        public bool IsPrefixSuffix       => Card.Type == CardTypePrefixSuffix;
        public bool IsSort               => Card.Type == CardTypeSort;
        public bool IsDropdownOrButton   => Card.Type == CardTypeDropdown || Card.Type == CardTypeButton;
        public bool IsDropdownButtonSearch => Card.Type == CardTypeDropdown || Card.Type == CardTypeButton
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

        // ── Dropdown card params ──

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

        // ── Search card params ──

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

        // ── Multi-Pick card params ──

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

        // ── Pair Transform card params ──

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

        // ── Link card params ──

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

        // ── Sync card params ──

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
        private List<string> _cardFieldPickerPinnedKeys = new List<string>();

        public bool HasCardFieldPicker => IsLink || IsSync || IsMultiPick || IsPairTransform;

        public string CardFieldPickerKey
        {
            get => IsLink ? PartnerFieldKey : CompanionFieldKey;
            set
            {
                if (IsLink) PartnerFieldKey   = value;
                else        CompanionFieldKey = value;
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

        public ObservableCollection<FieldSelectorGroupVm> CardFieldPickerGroups    { get; } = new ObservableCollection<FieldSelectorGroupVm>();
        public ObservableCollection<PinnedFieldEntry>      CardFieldPickerFavoriten { get; } = new ObservableCollection<PinnedFieldEntry>();
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

                bool isCollapsed   = Services.UiStateStore.LoadFieldSelGroupCollapsed(g.Key);
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

                CardFieldPickerGroups.Add(gvm);
            }
        }

        private static int CfGroupOrder(string groupName)
        {
            switch (groupName)
            {
                case "Grp_Special":           return 0;
                case "Grp_iPropertiesCustom": return 1;
                case "Grp_ParamUser":         return 2;
                case "Grp_iProperties":       return 3;
                case "Grp_Document":          return 4;
                case "Grp_ParamModel":        return 5;
                default:                      return 99;
            }
        }

        private void ApplyCardFieldPickerFilter()
        {
            string filter  = _cardFieldPickerFilter != null ? _cardFieldPickerFilter.Trim() : "";
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
                : new List<string>(saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

            CardFieldPickerFavoriten.Clear();
            foreach (string key in _cardFieldPickerPinnedKeys)
            {
                var fi    = AvailableTargetFields.FirstOrDefault(f => f.Key == key);
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

        // ── Formula card params ──

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
            }
        }

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

        // ── PrefixSuffix card params ──

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

        public bool PsIsRemoveMode
        {
            get => Card.Params.TryGetValue(ParamIsRemoveMode, out var v) && v == "true";
            set
            {
                bool cur = PsIsRemoveMode;
                if (cur == value) return;
                Card.Params[ParamIsRemoveMode] = value ? "true" : "false";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }

        // ── Sort card params ──

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

        public bool SrtIsInvert
        {
            get => Card.Params.TryGetValue(ParamSortInvert, out var v) && v == "true";
            set
            {
                bool cur = SrtIsInvert;
                if (cur == value) return;
                Card.Params[ParamSortInvert] = value ? "true" : "false";
                _onChanged?.Invoke();
                OnPropertyChanged();
            }
        }


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
        private readonly Action<int, int>                  _moveGroup;
        private readonly Action<CardGroupVm>               _duplicateGroup;

        public IReadOnlyList<FieldItem>   AvailableTargetFields { get; }
        public ListCollectionView         GroupedTargetFields    { get; }
        public IReadOnlyList<CatalogData> AvailableCatalogs      { get; }

        // ── Accent color ──
        private static readonly System.Windows.Media.Brush[] _palette = BuildPalette();
        private static System.Windows.Media.Brush[] BuildPalette()
        {
            var colors = new[]
            {
                System.Windows.Media.Color.FromRgb(0x5B, 0xA3, 0xDE),
                System.Windows.Media.Color.FromRgb(0x6C, 0xB8, 0x7A),
                System.Windows.Media.Color.FromRgb(0xC8, 0x98, 0x5A),
                System.Windows.Media.Color.FromRgb(0xBF, 0x6F, 0xC8),
                System.Windows.Media.Color.FromRgb(0xDE, 0x6A, 0x6A),
                System.Windows.Media.Color.FromRgb(0x5B, 0xC8, 0xC8),
                System.Windows.Media.Color.FromRgb(0xDE, 0xB8, 0x5A),
                System.Windows.Media.Color.FromRgb(0x9A, 0xDE, 0x5A),
            };
            var brushes = colors.Select(c => { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return (System.Windows.Media.Brush)b; }).ToArray();
            return brushes;
        }
        public System.Windows.Media.Brush AccentBrush { get; }

        public ObservableCollection<CardRowVm> Cards { get; } = new ObservableCollection<CardRowVm>();

        // ── Collapse state ──
        private bool _isCollapsed;

        // ── Commands ──
        public RelayCommand AddDropdownCardCommand { get; }
        public RelayCommand AddSyncCardCommand     { get; }
        public RelayCommand AddLinkCardCommand     { get; }
        public RelayCommand AddButtonCardCommand   { get; }
        public RelayCommand AddSearchCardCommand   { get; }
        public RelayCommand RemoveCardCommand      { get; }
        public RelayCommand MoveCardUpCommand      { get; }
        public RelayCommand MoveCardDownCommand    { get; }
        public RelayCommand DuplicateCardCommand   { get; }
        public RelayCommand RemoveGroupCommand     { get; }
        public RelayCommand ToggleCollapseCommand  { get; }
        public RelayCommand ToggleExpertCommand    { get; }
        public RelayCommand MoveGroupUpCommand     { get; }
        public RelayCommand MoveGroupDownCommand   { get; }
        public RelayCommand DuplicateGroupCommand  { get; }

        // ── Drag state ──
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

        // ── Active/inactive state ──
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
            AvailableTargetFields = availableTargetFields ?? System.Array.Empty<FieldItem>();
            GroupedTargetFields   = groupedTargetFields;
            AvailableCatalogs     = availableCatalogs    ?? System.Array.Empty<CatalogData>();
            AccentBrush           = _palette[Math.Abs((group.Id ?? "").GetHashCode()) % _palette.Length];
            _isAnyGroupActive     = isAnyGroupActive ?? (() => false);
            _isCollapsed          = UiStateStore.LoadGroupCollapsed(group.Id ?? "", defaultCollapsed: false);

            foreach (var card in group.Cards)
            {
                var vm = MakeCardVm(card);
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
            MoveCardUpCommand = new RelayCommand(
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
                    return gIdx > 0 && _allGroups[gIdx - 1].IsExpert == this.IsExpert;
                });
            MoveCardDownCommand = new RelayCommand(
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
                    return gIdx >= 0 && gIdx < _allGroups.Count - 1
                        && _allGroups[gIdx + 1].IsExpert == this.IsExpert;
                });
            DuplicateCardCommand  = new RelayCommand(p => { if (p is CardRowVm vm) DuplicateCard(vm); });
            RemoveGroupCommand    = new RelayCommand(() => _onRemove?.Invoke(this));
            ToggleCollapseCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
            ToggleExpertCommand   = new RelayCommand(() => IsExpert = !IsExpert);
            MoveGroupUpCommand    = new RelayCommand(MoveGroupUp,   CanMoveGroupUp);
            MoveGroupDownCommand  = new RelayCommand(MoveGroupDown, CanMoveGroupDown);
            DuplicateGroupCommand = new RelayCommand(() => _duplicateGroup?.Invoke(this));
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

        // ── Target Field Picker (P3-B enhanced popup) ────────────────────────────

        private string _targetFieldPickerFilter = "";
        private bool   _isTargetFieldPickerOpen;
        private List<string> _targetFieldPickerPinnedKeys = new List<string>();

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

        public ObservableCollection<FieldSelectorGroupVm> TargetFieldPickerGroups    { get; } = new ObservableCollection<FieldSelectorGroupVm>();
        public ObservableCollection<PinnedFieldEntry>      TargetFieldPickerFavoriten { get; } = new ObservableCollection<PinnedFieldEntry>();

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
                if (string.IsNullOrEmpty(g.Key)) continue;

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

        private static int TfGroupOrder(string groupName)
        {
            switch (groupName)
            {
                case "Grp_Special":           return 0;
                case "Grp_iPropertiesCustom": return 1;
                case "Grp_ParamUser":         return 2;
                case "Grp_iProperties":       return 3;
                case "Grp_Document":          return 4;
                case "Grp_ParamModel":        return 5;
                default:                      return 99;
            }
        }

        private void ApplyTargetFieldPickerFilter()
        {
            string filter  = _targetFieldPickerFilter != null ? _targetFieldPickerFilter.Trim() : "";
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
                : new List<string>(saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

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

        // ── F1 Collapsibility + V1 Expert Mode ──

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed == value) return;
                _isCollapsed = value;
                UiStateStore.SaveGroupCollapsed(Group.Id ?? "", value);
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

        private void PropagateIsExpertToCards()
        {
            foreach (var c in Cards) c.IsExpertModeGroup = Group.IsExpert;
        }

        public string SectionLabel => Group.IsExpert ? "Expert" : "Normal";

        private int _orderNumber;
        public int OrderNumber
        {
            get => _orderNumber;
            set { if (_orderNumber == value) return; _orderNumber = value; OnPropertyChanged(); }
        }

        private bool _isFirstExpert;
        public bool IsFirstExpert
        {
            get => _isFirstExpert;
            internal set { if (_isFirstExpert == value) return; _isFirstExpert = value; OnPropertyChanged(); }
        }

        private int _expertTopoOrder;
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

        public string ExpertTopoLabel    => _expertTopoOrder > 0 ? _expertTopoOrder.ToString()
                                         : _expertTopoOrder == -1 ? "⟳" : "";
        public bool   HasExpertTopoLabel => _expertTopoOrder != 0;

        public int  CardsCount => Group.Cards.Count(c => c.Enabled && c.Type != CardTypeBasicLogic);
        public int  BLCount    => Group.Cards.Count(c => c.Enabled && c.Type == CardTypeBasicLogic);
        public bool HasCards   => CardsCount > 0;
        public bool HasBLs     => BLCount > 0;

        public IReadOnlyList<CardTypePill> CardTypePills
        {
            get
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var c in Group.Cards)
                    if (c.Enabled && !string.IsNullOrEmpty(c.Type))
                    {
                        int n;
                        counts.TryGetValue(c.Type, out n);
                        counts[c.Type] = n + 1;
                    }
                var order = new[]
                {
                    CardTypeDropdown, CardTypeButton, CardTypeSearch, CardTypeMultiPick,
                    CardTypeLink, CardTypeSync, CardTypePairTransform,
                    CardTypePrefixSuffix, CardTypeSort, CardTypeBasicLogic,
                };
                var result = new List<CardTypePill>(counts.Count);
                foreach (var t in order)
                {
                    int n;
                    if (counts.TryGetValue(t, out n) && n > 0)
                        result.Add(new CardTypePill(t, n));
                }
                foreach (var kv in counts)
                    if (System.Array.IndexOf(order, kv.Key) < 0)
                        result.Add(new CardTypePill(kv.Key, kv.Value));
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

        // ── Per-group ▲▼ (section-restricted: same IsExpert only) ──

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

        private CardRowVm MakeCardVm(CapabilityCard card)
        {
            var vm = new CardRowVm(card, _onSave, AvailableTargetFields, AvailableCatalogs, _getRoles);
            vm.IsExpertModeGroup = Group.IsExpert;
            return vm;
        }

        internal void AddCard(string type, string formulaSkeleton = null)
        {
            var card = new CapabilityCard { Type = type, Enabled = true };
            if (type == CardTypeBasicLogic && formulaSkeleton != null)
                card.Params[ParamFormula] = formulaSkeleton;
            Group.Cards.Add(card);
            _onSave?.Invoke();
            Cards.Add(MakeCardVm(card));
            RelayCommand.RaiseCanExecuteChanged();
        }

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
    /// A type+count pill shown in a collapsed CardGroup header.
    /// </summary>
    public sealed class CardTypePill
    {
        public string Type  { get; }
        public int    Count { get; }
        public string Label => Count > 1 ? $"{Type} ×{Count}" : Type;
        public CardTypePill(string type, int count) { Type = type; Count = count; }
    }

    /// <summary>
    /// One display column slot in the Catalog Builder card editor (Dropdown / Button cards).
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
            _role                 = role        ?? "";
            AvailableCatalogRoles = catalogRoles ?? System.Array.Empty<string>();
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
}
