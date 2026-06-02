using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Inventor;
using CheckupAddIn.Models;
using CheckupAddIn.Services;

namespace CheckupAddIn.ViewModels
{
    /// <summary>
    /// Central ViewModel for the Checkup window. Owns all state, all commands, and all Inventor interactions.
    /// </summary>
    /// <remarks>
    /// Two constructors:
    ///   CheckupViewModel()               — parameterless, used by the VS XAML designer (dummy data only).
    ///   CheckupViewModel(Application)    — runtime constructor; wires up all services and Inventor events.
    ///
    /// Main data flow:
    ///   Inventor event → SafeRefresh → DoRefresh → DoRefreshCore
    ///     → DocumentResolver resolves active/selected document
    ///     → FieldCatalogBuilder builds/caches dropdown catalog
    ///     → Each RowModel.DisplayValue is updated from FieldCatalogBuilder.ResolveFieldValue
    ///     → Sheet metal rows (MiterGap, FlangeDistance) use SheetMetalReader
    ///
    /// Write flow (user edits a field):
    ///   StartInlineEditCommand → row.IsInlineEditing=true → user types → ApplyFieldEditCommand
    ///     → ApplyFieldEdit → FieldWriter.WriteFieldValue → DoRefresh
    ///   MiterGap writes go via ApplyMiterGap (special parameter-match logic).
    ///
    /// Row constraints (enforced by EnforceButtonRules after every mutation):
    ///   - MiterGap and FlangeDistance are always adjacent (MiterGap immediately above FlangeDistance).
    ///   - Both cannot be removed when only 2 rows remain.
    ///   - Maximum MAX_ROWS rows total.
    /// </remarks>
    public class CheckupViewModel : INotifyPropertyChanged
    {
        // ── Inventor interop services ──
        private readonly Inventor.Application _app;
        internal Inventor.Application AppInstance => _app;
        private readonly DocumentResolver     _docResolver;
        private readonly SheetMetalReader     _smReader;
        private readonly FieldCatalogBuilder  _catalogBuilder;
        private readonly StylePurger          _stylePurger;
        private readonly PresetsManager       _presetsManager;
        private readonly FieldWriter          _fieldWriter;
        // ── Catalog / Capability stores (wired in B5) ──
        internal CatalogStore    UserCatalogStore    { get; private set; }
        internal CapabilityStore UserCapabilityStore { get; private set; }

        // ── Runtime state ──
        private ApplicationEvents        _appEvents;
        private List<PresetData>         _presets;
        // Debounce timer: OnSelect fires many times per selection gesture; wait 80 ms for it to settle.
        private DispatcherTimer          _selectDebounce;
        // Polls SelectSet.Count every 200 ms. Fires DoRefresh when count drops to 0 without an event
        // (Inventor sometimes updates SelectSet asynchronously after ModelBrowser→ViewSpace deselect).
        private DispatcherTimer          _selectSetPoller;
        private int                      _lastPolledSelectSetCount = 0;
        private DocumentEvents           _docEvents;     // per-document OnChange subscription
        // ▼▼▼ FALLBACK INTERVAL — poll rate for iProperty changes not covered by OnChange ▼▼▼
        private const int                FALLBACK_INTERVAL_MS  = 15000;   // 15 s
        // ▲▲▲ ─────────────────────────────────────────────────────────────────────────────── ▲▲▲
        // ▼▼▼ IDLE STOP — stop fallback timer after this many ticks with no Inventor activity ▼▼▼
        private const int                IDLE_STOP_AFTER_TICKS = 4;       // 4 × 15 s = 60 s
        // ▲▲▲ ─────────────────────────────────────────────────────────────────────────────── ▲▲▲
        private int                      _noChangeTicks  = 0;
        private DispatcherTimer          _autoRefresh;
        private EventHandler             _selectDebounceTick;
        private EventHandler             _selectSetPollerTick;
        private EventHandler             _autoRefreshTick;
        // Guards against re-entrant refreshes (e.g. FieldCatalog setter triggering another DoRefresh).
        private bool                     _isRefreshing;
        // Remembered selection from the last write; persists until user selects something new
        // or switches document. Cleared only by a new non-empty selection or document activation —
        // NOT by deselect events, which Inventor fires unpredictably after document updates.
        private List<Document>           _stickyDocs = null;
        // Documents resolved on each refresh; used by ApplyFieldEdit/ApplyMiterGap for batch writes.
        private List<Document>           _selectedDocs = new List<Document>();
        private const int MAX_ROWS = 30;
        // Tracks which multi-token Logic row is currently in edit mode (for per-token autocomplete).
        private RowModel _activeMultiTokenEditRow;

        // ══════════════════════════════════════════════
        //  OBSERVABLE PROPERTIES
        // ══════════════════════════════════════════════

        private string _fileName = "";
        public string FileName
        {
            get => _fileName;
            set { _fileName = value ?? ""; OnPropertyChanged(); }
        }

        private string _statusMessage = "Ready.";
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? ""; OnPropertyChanged(); }
        }

        private ObservableCollection<RowModel> _rows = new ObservableCollection<RowModel>();
        public ObservableCollection<RowModel> Rows
        {
            get => _rows;
            set { _rows = value; OnPropertyChanged(); }
        }

        private List<FieldItem> _fieldCatalog = new List<FieldItem>();
        public List<FieldItem> FieldCatalog
        {
            get => _fieldCatalog;
            set
            {
                if (ReferenceEquals(_fieldCatalog, value)) return;
                _fieldCatalog = value ?? new List<FieldItem>();
                OnPropertyChanged();
                RebuildGroupedCatalog();
            }
        }

        private ListCollectionView _groupedFieldCatalog;
        public ListCollectionView GroupedFieldCatalog
        {
            get => _groupedFieldCatalog;
            private set { _groupedFieldCatalog = value; OnPropertyChanged(); }
        }

        private string _preset1Name = "Preset 1";
        public string Preset1Name
        {
            get => _preset1Name;
            set { _preset1Name = value ?? ""; OnPropertyChanged(); }
        }

        private string _preset2Name = "Preset 2";
        public string Preset2Name
        {
            get => _preset2Name;
            set { _preset2Name = value ?? ""; OnPropertyChanged(); }
        }

        private string _preset3Name = "Preset 3";
        public string Preset3Name
        {
            get => _preset3Name;
            set { _preset3Name = value ?? ""; OnPropertyChanged(); }
        }

        private int _activePresetIndex = -1;
        public bool IsPreset1Active => _activePresetIndex == 0;
        public bool IsPreset2Active => _activePresetIndex == 1;
        public bool IsPreset3Active => _activePresetIndex == 2;
        private void SetActivePreset(int index)
        {
            if (_activePresetIndex == index) return;
            _activePresetIndex = index;
            UiStateStore.SaveActivePresetIndex(index);
            OnPropertyChanged(nameof(IsPreset1Active));
            OnPropertyChanged(nameof(IsPreset2Active));
            OnPropertyChanged(nameof(IsPreset3Active));
        }

        private bool _isMultiSelection;
        public bool IsMultiSelection
        {
            get => _isMultiSelection;
            set
            {
                if (_isMultiSelection == value) return;
                _isMultiSelection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSingleSelection));
            }
        }

        // Exposed for XAML binding — ComboBox IsEnabled binds to this.
        public bool IsSingleSelection => !_isMultiSelection;

        // ══════════════════════════════════════════════
        //  COMMANDS
        // ══════════════════════════════════════════════

        public RelayCommand PurgeStylesCommand         { get; }
        public RelayCommand Preset1Command             { get; }
        public RelayCommand Preset2Command             { get; }
        public RelayCommand Preset3Command             { get; }
        public RelayCommand InfoCommand                { get; }
        public RelayCommand ResetCommand               { get; }
        public RelayCommand CloseCommand               { get; }
        public RelayCommand AddRowCommand              { get; }
        public RelayCommand RemoveRowCommand           { get; }
        public RelayCommand StartInlineEditCommand     { get; }
        public RelayCommand ApplyFieldEditCommand      { get; }
        public RelayCommand CancelFieldEditCommand     { get; }
        public RelayCommand FieldSelectionChangedCommand { get; }
        public RelayCommand OpenCatalogPickerCommand     { get; }
        public RelayCommand ApplyExpertValueCommand      { get; }
        public RelayCommand ToggleFieldPinCommand        { get; private set; }
        public RelayCommand ClearFieldSelPrefsCommand    { get; private set; }

        // ── V1 Expert Mode ──
        private static readonly System.Windows.Media.SolidColorBrush _expertAmberBrush = MakeAmberBrush();
        private static System.Windows.Media.SolidColorBrush MakeAmberBrush()
        {
            var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xA0, 0x17));
            b.Freeze();
            return b;
        }

        // ── F1 Field Selector ──
        private IReadOnlyList<FieldItem> _stickyFieldItems = new FieldItem[0];
        public IReadOnlyList<FieldItem> StickyFieldItems
        {
            get => _stickyFieldItems;
            private set { _stickyFieldItems = value; OnPropertyChanged(); }
        }

        private IReadOnlyList<FieldSelectorGroupVm> _scrollableGroups = new FieldSelectorGroupVm[0];
        public IReadOnlyList<FieldSelectorGroupVm> ScrollableGroups
        {
            get => _scrollableGroups;
            private set { _scrollableGroups = value; OnPropertyChanged(); }
        }

        private readonly List<string> _pinnedFieldKeys = new List<string>();

        private IReadOnlyList<PinnedFieldEntry> _favoritenItems = new PinnedFieldEntry[0];
        public IReadOnlyList<PinnedFieldEntry> FavoritenItems
        {
            get => _favoritenItems;
            private set { _favoritenItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPinnedItems)); }
        }

        public bool HasPinnedItems => _favoritenItems != null && _favoritenItems.Count > 0;

        private string _fieldSelectorFilterText = "";
        public string FieldSelectorFilterText
        {
            get => _fieldSelectorFilterText;
            set
            {
                if (_fieldSelectorFilterText == value) return;
                _fieldSelectorFilterText = value ?? "";
                OnPropertyChanged();
                ApplyFieldSelectorFilter();
            }
        }

        public event Action RequestClose;
        public event Action<RowModel> RequestOpenCatalogPicker;
        public event Action RequestResetWindowSize;

        // ══════════════════════════════════════════════
        //  DESIGN-TIME CONSTRUCTOR (parameterless)
        // ══════════════════════════════════════════════

        public CheckupViewModel()
        {
            // VS Designer uses this constructor — populate with dummy data only.
            PurgeStylesCommand          = new RelayCommand(() => { });
            Preset1Command              = new RelayCommand(() => { });
            Preset2Command              = new RelayCommand(() => { });
            Preset3Command              = new RelayCommand(() => { });
            InfoCommand                 = new RelayCommand(() => { });
            ResetCommand                = new RelayCommand(() => { });
            CloseCommand                = new RelayCommand(() => { });
            AddRowCommand               = new RelayCommand(_ => { });
            RemoveRowCommand            = new RelayCommand(_ => { });
            StartInlineEditCommand      = new RelayCommand(_ => { });
            ApplyFieldEditCommand       = new RelayCommand(_ => { });
            CancelFieldEditCommand      = new RelayCommand(_ => { });
            FieldSelectionChangedCommand = new RelayCommand(_ => { });
            OpenCatalogPickerCommand     = new RelayCommand(_ => { });
            ApplyExpertValueCommand      = new RelayCommand(_ => { });

            Preset1Name = "Bauteil";
            Preset2Name = "Baugruppe";
            Preset3Name = "Gehrungslücke";

            FieldCatalog = new List<FieldItem>
            {
                new FieldItem("", "(kein)", "", ""),
                new FieldItem("SPECIAL:MiterGap",      "Gehrungslücke",     "Gehrungslücke",     "Grp_SheetMetal",        true),
                new FieldItem("SPECIAL:FlangeDistance","2te Lasche C-Kante","2te Lasche C-Kante","Grp_SheetMetal",        false),
                new FieldItem("UDEF:ISO",              "ISO",               "ISO",               "Grp_iPropertiesCustom", true),
                new FieldItem("DOC:Material",          "Material",          "Material",          "Grp_Document",          true),
                new FieldItem("PARAM:User:Thickness",  "Thickness",         "Thickness",         "Grp_ParamUser",         true),
            };

            Rows.Add(new RowModel { FieldKey = "SPECIAL:MiterGap",       FieldLabel = "Gehrungslücke",      IsWritableField = true, IsMiterGapRow = true,
                                    IsInlineEditing = true, EditText = "0.12" });
            Rows.Add(new RowModel { FieldKey = "SPECIAL:FlangeDistance",  FieldLabel = "2te Lasche C-Kante", DisplayValue = "25.000 mm", ValueForeground = Brushes.Red });
            Rows.Add(new RowModel { FieldKey = "UDEF:ISO",                FieldLabel = "ISO",                DisplayValue = "1234-567-A",  IsWritableField = true });
            Rows.Add(new RowModel { FieldKey = "DOC:Material",            FieldLabel = "Material",           DisplayValue = "Steel",       IsWritableField = true });
            Rows.Add(new RowModel { FieldKey = "PARAM:User:Thickness",    FieldLabel = "Thickness",          DisplayValue = "2.000 mm",    IsWritableField = true });

            // Show preset 1 as active so the indicator dot is visible in the designer.
            _activePresetIndex = 0;

            FileName      = "ExamplePart.ipt";
            StatusMessage = "Design-time preview";
            EnforceButtonRules();
        }

        // ══════════════════════════════════════════════
        //  RUNTIME CONSTRUCTOR
        // ══════════════════════════════════════════════

        public CheckupViewModel(Inventor.Application app, UserSettings settings = null,
                                CatalogStore catalogStore = null, CapabilityStore capabilityStore = null)
        {
            _app            = app;
            UserCatalogStore    = catalogStore;
            UserCapabilityStore = capabilityStore;
            _docResolver    = new DocumentResolver(app);
            _smReader       = new SheetMetalReader();
            _catalogBuilder = new FieldCatalogBuilder(_app, capabilityStore);
            _stylePurger    = new StylePurger(_app, settings?.StylePurge ?? new UserSettings.StylePurgeSection());
            _presetsManager = new PresetsManager(settings?.Presets);
            _fieldWriter    = new FieldWriter(app);

            // Load persisted presets (falls back to built-in defaults)
            _presets = _presetsManager.Load();
            Preset1Name = _presets[0].Name;
            Preset2Name = _presets[1].Name;
            Preset3Name = _presets[2].Name;

            PurgeStylesCommand = new RelayCommand(DoPurgeStyles);
            Preset1Command     = new RelayCommand(() => ApplyPreset(0));
            Preset2Command     = new RelayCommand(() => ApplyPreset(1));
            Preset3Command     = new RelayCommand(() => ApplyPreset(2));
            InfoCommand        = new RelayCommand(ShowInfo);
            ResetCommand       = new RelayCommand(ResetToDefaults);
            CloseCommand       = new RelayCommand(() => RequestClose?.Invoke());
            AddRowCommand      = new RelayCommand(p => InsertRowBelow(p as RowModel));
            RemoveRowCommand   = new RelayCommand(p => RemoveRow(p as RowModel));

            StartInlineEditCommand = new RelayCommand(
                p =>
                {
                    var row = p as RowModel;
                    if (row == null || !row.IsWritableField || row.IsEditable) return;

                    // Logic rows: check for formula-only vs. picker-driven groups.
                    bool isFormulaOnlyGroup = false;
                    if (row.FieldKey.StartsWith("SPECIAL:LOGIC:"))
                    {
                        string slGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                        (CapabilitySet CapSet, CardGroup Group)? slFound = UserCapabilityStore?.FindGroup(slGroupId);
                        if (slFound != null)
                        {
                            var slGroup = slFound.Value.Group;
                            bool hasPrimary = CardEngine.HasCard(slGroup, CardEngine.CardTypeDropdown)
                                          || CardEngine.HasCard(slGroup, CardEngine.CardTypeSearch)
                                          || CardEngine.HasCard(slGroup, CardEngine.CardTypeButton)
                                          || CardEngine.HasCard(slGroup, CardEngine.CardTypeMultiPick);
                            if (!hasPrimary && (CardEngine.HasBasicLogicCards(slGroup) || CardEngine.HasPairTransformCard(slGroup) || CardEngine.HasPrefixSuffixCard(slGroup)))
                                isFormulaOnlyGroup = true;
                            else
                            {
                                string catId = CardEngine.GetPrimaryCatalogId(slGroup);
                                CatalogData catalog = catId != null && UserCatalogStore != null
                                    ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                                    : null;
                                if (CardEngine.HasCard(slGroup, CardEngine.CardTypeSearch))
                                {
                                    row.CatalogDropdownItems = CardEngine.GetSearchItemsForCard(slGroup, catalog);
                                    row.IsLogicSearchMode    = true;
                                    PopulateLogicDropdownColumns(row, slGroup, catalog);
                                }
                                else if (CardEngine.HasCard(slGroup, CardEngine.CardTypeDropdown))
                                {
                                    row.CatalogDropdownItems = CardEngine.GetDropdownItemsForCard(slGroup, catalog);
                                    row.IsLogicSearchMode    = false;
                                    PopulateLogicDropdownColumns(row, slGroup, catalog);
                                }
                            }
                        }
                    }

                    // Close any active multi-token autocomplete before switching rows.
                    if (_activeMultiTokenEditRow != null && _activeMultiTokenEditRow != row)
                    {
                        _activeMultiTokenEditRow.PropertyChanged -= OnActiveMultiTokenRowPropertyChanged;
                        _activeMultiTokenEditRow.IsMultiTokenAutoCompleteOpen = false;
                        _activeMultiTokenEditRow = null;
                    }

                    // Close any other row that's already in edit mode.
                    foreach (var r in Rows)
                        if (r != row && r.IsInlineEditing) r.IsInlineEditing = false;

                    // Pre-fill with common value when all docs agree; blank for formula groups and when values differ.
                    string startText = (isFormulaOnlyGroup || row.DisplayValue.IndexOf(" | ", StringComparison.Ordinal) >= 0) ? "" : row.DisplayValue;
                    // Set OriginalValue BEFORE EditText so TextChanged guards (EditText != OriginalValue) work correctly.
                    row.OriginalValue = startText;
                    // Use suppress-filter variant so Logic Search rows and AllowedValues rows do not
                    // instantly filter/auto-open their popup on edit-mode entry (TDD §5.13).
                    row.SetEditTextSuppressFilter(startText);
                    row.IsInlineEditing = true;

                    if (row.IsMultiTokenMode)
                    {
                        string mtGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                        (CapabilitySet CapSet, CardGroup Group)? mtFound = UserCapabilityStore?.FindGroup(mtGroupId);
                        if (mtFound != null)
                        {
                            string mtCatId = CardEngine.GetPrimaryCatalogId(mtFound.Value.Group);
                            CatalogData mtCatalog = mtCatId != null && UserCatalogStore != null
                                ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == mtCatId)
                                : null;
                            var mtCfg = CardEngine.GetMultiTokenAutoCompleteConfig(mtFound.Value.Group, mtCatalog);
                            row.MultiTokenSeparator = !string.IsNullOrEmpty(mtCfg.Separator) ? mtCfg.Separator : "-";
                        }
                        _activeMultiTokenEditRow = row;
                        row.PropertyChanged += OnActiveMultiTokenRowPropertyChanged;
                    }
                },
                p => (p as RowModel)?.IsWritableField == true && (p as RowModel)?.IsEditable == false);

            ApplyFieldEditCommand = new RelayCommand(
                p => { if (p is RowModel row) ApplyFieldEdit(row); });

            CancelFieldEditCommand = new RelayCommand(
                p => { if (p is RowModel row) row.IsInlineEditing = false; });

            FieldSelectionChangedCommand = new RelayCommand(
                p => OnFieldSelectionChanged(p as RowModel));

            ToggleFieldPinCommand = new RelayCommand(p =>
            {
                if (p is string key) ToggleFieldPin(key);
            });
            ClearFieldSelPrefsCommand = new RelayCommand(_ =>
            {
                _pinnedFieldKeys.Clear();
                UiStateStore.SaveFieldSelPinnedFields("");
                UiStateStore.ClearFieldSelUserPrefs();
                RebuildGroupedCatalog();
            });

            OpenCatalogPickerCommand = new RelayCommand(p => { var h = RequestOpenCatalogPicker; if (h != null) h(p as RowModel); });
            ApplyExpertValueCommand  = new RelayCommand(p => { if (p is RowModel r) ApplyExpertValue(r); });

            string pinnedRaw = UiStateStore.LoadFieldSelPinnedFields();
            if (!string.IsNullOrEmpty(pinnedRaw))
            {
                foreach (var k in pinnedRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string kt = k.Trim();
                    if (kt.Length > 0) _pinnedFieldKeys.Add(kt);
                }
            }

            // Restore last-used preset (may differ from default preset 0).
            int savedIdx = UiStateStore.LoadActivePresetIndex();
            if (savedIdx > 0 && savedIdx < _presets.Count)
            {
                Rows.Clear();
                foreach (var key in _presets[savedIdx].FieldKeys)
                    Rows.Add(new RowModel { FieldKey = key });
                _activePresetIndex = savedIdx;
                EnforceButtonRules();
            }
            else
            {
                InitializeDefaultRows();
                _activePresetIndex = 0;
            }
            SubscribeToInventorEvents();
            DoRefresh();
        }

        // ══════════════════════════════════════════════
        //  GROUPED CATALOG
        // ══════════════════════════════════════════════

        private void RebuildGroupedCatalog()
        {
            if (_fieldCatalog == null || _fieldCatalog.Count == 0) return;

            // Zone 2 — fixed actions (Add/Remove Row + kein Feld); never filtered
            var keinFeld = _fieldCatalog.FirstOrDefault(f => f.GroupName == "");
            StickyFieldItems = new List<FieldItem>
            {
                new FieldItem("__ADD_ROW__",    LanguageLoader.Get("Action_AddRow"),    "", "", false, true),
                new FieldItem("__REMOVE_ROW__", LanguageLoader.Get("Action_RemoveRow"), "", "", false, true),
                keinFeld ?? new FieldItem("", LanguageLoader.Get("Field_None"), "", ""),
            };

            // Favoriten zone — rebuild from pinned keys
            RebuildFavoritenZone();

            // Scrollable groups — build FieldSelectorGroupVm per group (GRP_NONE excluded; in StickyFieldItems)
            var groupedItems = _fieldCatalog
                .Where(f => f.GroupName != "")
                .GroupBy(f => f.GroupName)
                .OrderBy(g => GroupOrder(g.Key))
                .ToList();

            var groups = new List<FieldSelectorGroupVm>();
            foreach (var grp in groupedItems)
            {
                bool isSpecial = grp.Key == "Grp_Special";
                var items = grp.ToList();

                bool hasActiveLcEntry = isSpecial && items.Any(f => f.Key.StartsWith("SPECIAL:LOGIC:"));
                bool autoCollapsed    = isSpecial && !hasActiveLcEntry;
                bool collapsed        = autoCollapsed
                    || UiStateStore.LoadFieldSelGroupCollapsed(grp.Key, false);

                var gvm = new FieldSelectorGroupVm
                {
                    GroupName        = grp.Key,
                    GroupDisplayName = LanguageLoader.Get(grp.Key),
                    AllItems         = items,
                    FilteredItems    = items,
                    IsCollapsed      = collapsed,
                    IsChevronEnabled = !autoCollapsed,
                };
                groups.Add(gvm);
            }

            foreach (var gvm in groups)
            {
                var captured = gvm;
                captured.ToggleCollapseCommand = new RelayCommand(_ =>
                {
                    if (!captured.IsChevronEnabled) return;
                    captured.IsCollapsed = !captured.IsCollapsed;
                    UiStateStore.SaveFieldSelGroupCollapsed(captured.GroupName, captured.IsCollapsed);
                });
            }

            ScrollableGroups = groups;

            ApplyFieldSelectorFilter();

            // GroupedFieldCatalog kept for legacy references (Target Field ComboBox in CatalogBuilder)
            var actionItems = new List<FieldItem>
            {
                new FieldItem("__ADD_ROW__",    LanguageLoader.Get("Action_AddRow"),    "", "", false, true),
                new FieldItem("__REMOVE_ROW__", LanguageLoader.Get("Action_RemoveRow"), "", "", false, true),
            };
            var combined = new List<FieldItem>(actionItems);
            combined.AddRange(_fieldCatalog);
            var view = new ListCollectionView(combined);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldItem.GroupName)));
            GroupedFieldCatalog = view;
        }

        private static int GroupOrder(string g)
        {
            switch (g)
            {
                case "Grp_Special":          return 0;
                case "Grp_iPropertiesCustom": return 1;
                case "Grp_ParamUser":         return 2;
                case "Grp_iProperties":       return 3;
                case "Grp_Document":          return 4;
                case "Grp_ParamModel":        return 5;
                default:                      return 6;
            }
        }

        private void ApplyFieldSelectorFilter()
        {
            string filter    = _fieldSelectorFilterText;
            bool   hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var gvm in _scrollableGroups)
            {
                if (!hasFilter)
                {
                    gvm.FilteredItems = gvm.AllItems;
                    if (!gvm.IsChevronEnabled)
                        gvm.IsCollapsed = true;
                }
                else
                {
                    var filtered = new List<FieldItem>();
                    foreach (var f in gvm.AllItems)
                        if (f.DropText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            filtered.Add(f);
                    gvm.FilteredItems = filtered;
                    if (gvm.IsChevronEnabled) gvm.IsCollapsed = false;
                    else if (gvm.FilteredItems.Count > 0) gvm.IsCollapsed = false;
                }
            }
        }

        public void OnFieldSelectorClosed()
        {
            if (_fieldSelectorFilterText.Length > 0)
                FieldSelectorFilterText = "";
        }

        private void RebuildFavoritenZone()
        {
            if (_fieldCatalog == null) { FavoritenItems = new PinnedFieldEntry[0]; return; }
            var available = new System.Collections.Generic.HashSet<string>(
                _fieldCatalog.Select(f => f.Key), StringComparer.Ordinal);
            var entries = new List<PinnedFieldEntry>();
            foreach (var key in _pinnedFieldKeys)
            {
                var item = _fieldCatalog.FirstOrDefault(f => f.Key == key);
                if (item == null) item = new FieldItem(key, key, key, "");
                entries.Add(new PinnedFieldEntry { Item = item, IsAvailable = available.Contains(key) });
            }
            FavoritenItems = entries;
        }

        private void ToggleFieldPin(string fieldKey)
        {
            if (string.IsNullOrEmpty(fieldKey)) return;
            if (_pinnedFieldKeys.Contains(fieldKey))
                _pinnedFieldKeys.Remove(fieldKey);
            else
                _pinnedFieldKeys.Add(fieldKey);
            UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _pinnedFieldKeys));
            RebuildFavoritenZone();
        }

        public void ReorderPinnedField(string fromKey, string toKey)
        {
            int fromIdx = _pinnedFieldKeys.IndexOf(fromKey);
            int toIdx   = _pinnedFieldKeys.IndexOf(toKey);
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
            _pinnedFieldKeys.RemoveAt(fromIdx);
            _pinnedFieldKeys.Insert(toIdx, fromKey);
            UiStateStore.SaveFieldSelPinnedFields(string.Join(";", _pinnedFieldKeys));
            RebuildFavoritenZone();
        }

        // ══════════════════════════════════════════════
        //  INVENTOR EVENT SUBSCRIPTIONS
        // ══════════════════════════════════════════════

        private void SubscribeToInventorEvents()
        {
            try
            {
                _appEvents = _app.ApplicationEvents;
                _appEvents.OnActivateDocument += OnDocumentActivated;
                _appEvents.OnDeactivateDocument += OnDocumentDeactivated;
            }
            catch { }

            try
            {
                _app.CommandManager.UserInputEvents.OnSelect   += OnSelectionChanged;
                _app.CommandManager.UserInputEvents.OnUnSelect += OnUnSelectionChanged;
            }
            catch { }

            _selectDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _selectDebounceTick = (_, _) => { _selectDebounce.Stop(); DoRefresh(); };
            _selectDebounce.Tick += _selectDebounceTick;

            _selectSetPoller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _selectSetPollerTick = (_, _) =>
            {
                try
                {
                    int cnt = 0;
                    if (_app.ActiveDocument is AssemblyDocument asm)
                        cnt = asm.SelectSet.Count;
                    if (cnt != _lastPolledSelectSetCount)
                    {
                        _lastPolledSelectSetCount = cnt;
                        if (cnt == 0) DoRefresh();
                    }
                }
                catch { }
            };
            _selectSetPoller.Tick += _selectSetPollerTick;
            _selectSetPoller.Start();

            // ── Fallback refresh: catches iProperty changes not covered by DocumentEvents.OnChange.
            //    Self-stops after IDLE_STOP_AFTER_TICKS consecutive ticks with no Inventor activity;
            //    any subscribed Inventor event calls ResetFallbackTimer() to restart it.
            _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FALLBACK_INTERVAL_MS) };
            _autoRefreshTick = (_, _) =>
            {
                try
                {
                    if (!Rows.Any(r => r.IsInlineEditing))
                        DoRefresh();
                    if (++_noChangeTicks >= IDLE_STOP_AFTER_TICKS)
                        _autoRefresh.Stop();
                }
                catch { }
            };
            _autoRefresh.Tick += _autoRefreshTick;
            _autoRefresh.Start();

            // Subscribe to the current document's OnChange for reactive parameter-update detection.
            try { SubscribeDocEvents(_app.ActiveDocument); } catch { }
        }

        public void UnsubscribeFromInventorEvents()
        {
            try
            {
                if (_selectDebounceTick != null && _selectDebounce != null) { _selectDebounce.Tick -= _selectDebounceTick; _selectDebounceTick = null; }
                _selectDebounce?.Stop();
                _selectDebounce = null;
            }
            catch { }

            try
            {
                if (_appEvents != null)
                {
                    _appEvents.OnActivateDocument   -= OnDocumentActivated;
                    _appEvents.OnDeactivateDocument -= OnDocumentDeactivated;
                    _appEvents = null;
                }
            }
            catch { }

            try
            {
                if (_selectSetPollerTick != null && _selectSetPoller != null) { _selectSetPoller.Tick -= _selectSetPollerTick; _selectSetPollerTick = null; }
                _selectSetPoller?.Stop();
                _selectSetPoller = null;
            }
            catch { }

            try
            {
                if (_autoRefreshTick != null && _autoRefresh != null) { _autoRefresh.Tick -= _autoRefreshTick; _autoRefreshTick = null; }
                _autoRefresh?.Stop();
                _autoRefresh = null;
            }
            catch { }

            UnsubscribeDocEvents();

            try { _app.CommandManager.UserInputEvents.OnSelect   -= OnSelectionChanged; }
            catch { }
            try { _app.CommandManager.UserInputEvents.OnUnSelect -= OnUnSelectionChanged; }
            catch { }
        }

        private void OnDocumentActivated(_Document docObj, EventTimingEnum before,
            NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (before == EventTimingEnum.kAfter)
            {
                _stickyDocs = null;   // document switch always resets sticky selection
                _catalogBuilder.InvalidateCache();
                SubscribeDocEvents(docObj as Document);
                SafeRefresh();
            }
        }

        private void OnDocumentDeactivated(_Document docObj, EventTimingEnum before,
            NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (before == EventTimingEnum.kAfter)
                UnsubscribeDocEvents();
        }

        private void OnSelectionChanged(ObjectsEnumerator justSelected,
            ref ObjectCollection moreSelected, SelectionDeviceEnum device,
            Inventor.Point modelPosition, Point2d viewPosition, Inventor.View view)
        {
            // Read count on the COM thread while the enumerator is still valid.
            // Count == 0 means a click selected nothing (empty-area click in viewport) —
            // treat it like OnUnSelect so browser selections are also cleared.
            int count = 0;
            try { count = justSelected?.Count ?? 0; } catch { }

            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ResetFallbackTimer();
                        _selectDebounce?.Stop();
                        if (count == 0)
                        {
                            // Do NOT clear sticky here. Inventor fires this event unpredictably
                            // after document updates (iProperty writes, part.Update, etc.) and
                            // we cannot distinguish a write-triggered deselect from a genuine
                            // empty-click at this point. Sticky is released only when the user
                            // makes a new explicit selection (count > 0) or switches document.
                            try
                            {
                                if (_app.ActiveDocument is AssemblyDocument asmDoc)
                                    asmDoc.SelectSet.Clear();
                            }
                            catch { }
                            DoRefresh();
                        }
                        else
                        {
                            _stickyDocs = null;  // new explicit selection → release sticky
                            _selectDebounce?.Start();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnUnSelectionChanged(ObjectsEnumerator justUnSelected,
            SelectionDeviceEnum device,
            Inventor.Point modelPosition, Point2d viewPosition, Inventor.View view)
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ResetFallbackTimer(); _selectDebounce?.Stop(); DoRefresh(); }
                    catch { }
                }));
            }
            catch { }
        }

        private void SafeRefresh()
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        new Action(() => { try { ResetFallbackTimer(); DoRefresh(); } catch { } }));
                else
                {
                    ResetFallbackTimer();
                    DoRefresh();
                }
            }
            catch { }
        }

        private void SubscribeDocEvents(Document doc)
        {
            UnsubscribeDocEvents();
            if (doc == null) return;
            try
            {
                _docEvents = ((dynamic)doc).DocumentEvents as DocumentEvents;
                if (_docEvents != null)
                    _docEvents.OnChange += OnInventorDocumentChanged;
            }
            catch { }
        }

        private void UnsubscribeDocEvents()
        {
            try
            {
                if (_docEvents != null)
                {
                    _docEvents.OnChange -= OnInventorDocumentChanged;
                    _docEvents = null;
                }
            }
            catch { }
        }

        private void OnInventorDocumentChanged(CommandTypesEnum reasonsForChange, EventTimingEnum before,
                                               NameValueMap context, out HandlingCodeEnum handlingCode)
        {
            handlingCode = HandlingCodeEnum.kEventNotHandled;
            if (before != EventTimingEnum.kAfter) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ResetFallbackTimer();
                    if (!Rows.Any(r => r.IsInlineEditing))
                        DoRefresh();
                }
                catch { }
            }));
        }

        private void ResetFallbackTimer()
        {
            _noChangeTicks = 0;
            if (_autoRefresh != null && !_autoRefresh.IsEnabled)
                _autoRefresh.Start();
        }

        // ══════════════════════════════════════════════
        //  INITIALIZATION
        // ══════════════════════════════════════════════

        private void InitializeDefaultRows()
        {
            Rows.Clear();
            if (_presets != null && _presets.Count > 0 && _presets[0].FieldKeys.Count > 0)
            {
                foreach (var key in _presets[0].FieldKeys)
                    Rows.Add(new RowModel { FieldKey = key });
            }
            else
            {
                Rows.Add(new RowModel
                {
                    FieldKey        = FieldCatalogBuilder.FIELD_MITER_GAP,
                    FieldLabel      = LanguageLoader.Get("Field_MiterGap"),
                    IsMiterGapRow   = true,
                    IsWritableField = true
                });
                Rows.Add(new RowModel
                {
                    FieldKey        = FieldCatalogBuilder.FIELD_FLANGE_DISTANCE,
                    FieldLabel      = LanguageLoader.Get("Field_FlangeDistance"),
                    ValueForeground = Brushes.Red
                });
            }
            EnforceButtonRules();
        }

        // ══════════════════════════════════════════════
        //  MAIN REFRESH
        // ══════════════════════════════════════════════

        /// <summary>
        /// Refreshes all row values from the active Inventor document.
        /// Sets _isRefreshing to block re-entrant calls (FieldCatalog setter calls RebuildGroupedCatalog,
        /// which must not trigger another full refresh).
        /// </summary>
        public void DoRefresh()
        {
            _isRefreshing = true;
            try { DoRefreshCore(); }
            finally { _isRefreshing = false; }
        }

        public void InvalidateFieldCatalog()
        {
            _catalogBuilder.InvalidateCache();
            DoRefresh();
        }

        private void DoRefreshCore()
        {
            _selectedDocs = _docResolver.GetAllSelectedDocuments(out bool isMulti, out bool isAssemblyFallback);
            IsMultiSelection = isMulti;

            // When Inventor clears the IAM SelectSet after a document update, DocumentResolver
            // falls back to the assembly document (isAssemblyFallback=true). If we have a
            // remembered sticky selection from a recent write, use it instead so the display
            // stays on the previously selected component(s).
            if (isAssemblyFallback && _stickyDocs != null && _stickyDocs.Count > 0)
            {
                var validSticky = new List<Document>();
                foreach (var d in _stickyDocs)
                {
                    try { var _ = d.FullFileName; validSticky.Add(d); } catch { }
                }
                if (validSticky.Count > 0)
                {
                    _selectedDocs    = validSticky;
                    isMulti          = validSticky.Count > 1;
                    IsMultiSelection = isMulti;
                }
                else
                {
                    _stickyDocs = null;
                }
            }

            if (_selectedDocs.Count == 0) { StatusMessage = LanguageLoader.Get("Msg_NoDocument"); return; }

            var primaryDoc = _selectedDocs[0];

            FileName = string.Join(" / ", _selectedDocs.Select(d =>
            {
                try { return System.IO.Path.GetFileName(d.FullFileName); }
                catch { return d.DisplayName; }
            }));

            FieldCatalog = _catalogBuilder.GetCatalog(primaryDoc);

            bool needSM = Rows.Any(r =>
                r.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP ||
                r.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE);

            // Pre-compute sheet metal data per selected doc (null entries for non-sheet-metal docs).
            var smData = new List<(PartDocument part, FlangeFeature flange)>();
            if (needSM)
            {
                foreach (var doc in _selectedDocs)
                {
                    PartDocument smPart = null;
                    FlangeFeature smFlange = null;
                    try
                    {
                        if (doc is PartDocument pd &&
                            pd.ComponentDefinition is SheetMetalComponentDefinition)
                        {
                            smPart = pd;
                            smFlange = _smReader.FindSecondFlange(smPart);
                        }
                    }
                    catch { }
                    smData.Add((smPart, smFlange));
                }
            }

            foreach (var row in Rows)
            {
                // Don't disrupt an active inline edit
                if (row.IsInlineEditing) continue;

                row.SelectedField  = FieldCatalog.FirstOrDefault(f => f.Key == row.FieldKey);
                row.IsFieldMissing = row.SelectedField == null && !string.IsNullOrEmpty(row.FieldKey)
                    && !row.IsHalbzeugRow
                    && row.FieldKey != FieldCatalogBuilder.FIELD_FLANGE_DISTANCE;
                if (row.SelectedField != null)
                {
                    row.FieldLabel      = row.SelectedField.RowLabel;
                    row.IsWritableField = row.SelectedField.IsWritable;
                    row.AllowedValues   = row.SelectedField.AllowedValues;
                }
                else if (string.IsNullOrEmpty(row.FieldLabel) && !string.IsNullOrEmpty(row.FieldKey))
                {
                    row.FieldLabel = DeriveFieldLabel(row.FieldKey);
                }

                if (row.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP)
                {
                    row.IsMiterGapRow   = true;
                    row.IsEditable      = false;
                    row.IsWritableField = true;
                    var vals = smData.Select(sd =>
                        (sd.part == null || sd.flange == null) ? "n/a" :
                        TryRead(() => _smReader.CmToDisplayString(
                            _smReader.ReadMiterGapCm(sd.flange.Definition), sd.part))
                    ).ToList();
                    SetAggregatedValue(row, vals, Brushes.Black);
                }
                else if (row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE)
                {
                    row.IsMiterGapRow = false;
                    row.IsEditable    = false;
                    if (string.IsNullOrEmpty(row.FieldLabel))
                        row.FieldLabel = LanguageLoader.Get("Field_FlangeDistance");
                    var vals = smData.Select(sd =>
                        (sd.part == null || sd.flange == null) ? "n/a" :
                        TryRead(() => _smReader.CmToDisplayString(
                            _smReader.ReadFlangeDistanceCm(sd.flange.Definition), sd.part))
                    ).ToList();
                    // FlangeDistance is always red — pass Red as singleColor so it stays red even when uniform
                    SetAggregatedValue(row, vals, Brushes.Red);
                }
                else if (string.IsNullOrEmpty(row.FieldKey))
                {
                    row.IsMiterGapRow   = false;
                    row.IsEditable      = false;
                    row.IsWritableField = false;
                    row.DisplayValue    = "";
                    row.ValueForeground = Brushes.Black;
                }
                else
                {
                    row.IsMiterGapRow = false;
                    row.IsEditable    = false;

                    if (row.IsFieldMissing)
                    {
                        row.IsWritableField = false;
                        row.HasPickerButton  = false;
                    }

                    var vals = _selectedDocs
                        .Select(doc => _catalogBuilder.ResolveFieldValue(row.FieldKey, doc))
                        .ToList();
                    SetAggregatedValue(row, vals, Brushes.Black);

                    if (!row.IsFieldMissing)
                    {
                        if (row.IsHalbzeugRow)
                        {
                            row.IsWritableField = true;
                            if (string.IsNullOrEmpty(row.FieldLabel))
                                row.FieldLabel = row.IsHalbzeugNameRow ? "ROHTEILNAME" : "ROHTEILIDENT";
                        }

                        if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
                        {
                            string lgGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                            (CapabilitySet CapSet, CardGroup Group)? lgFound = UserCapabilityStore?.FindGroup(lgGroupId);
                            row.HasPickerButton = lgFound != null &&
                                CardEngine.HasCard(lgFound.Value.Group, CardEngine.CardTypeButton);
                        }
                        else
                        {
                            row.HasPickerButton = false;
                        }
                    }
                }
            }

            // Post-pass: Formula-only Logic rows — show live formula preview as display value.
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing) continue;
                if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                string fGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                (CapabilitySet CapSet, CardGroup Group)? fFound = UserCapabilityStore?.FindGroup(fGroupId);
                var fGroup = fFound?.Group;
                if (fGroup == null || !CardEngine.HasBasicLogicCards(fGroup)) continue;
                bool fHasPrimary = CardEngine.HasCard(fGroup, CardEngine.CardTypeDropdown)
                                || CardEngine.HasCard(fGroup, CardEngine.CardTypeSearch)
                                || CardEngine.HasCard(fGroup, CardEngine.CardTypeButton)
                                || CardEngine.HasCard(fGroup, CardEngine.CardTypeMultiPick);
                if (fHasPrimary) continue;
                if (CardEngine.HasInputReference(fGroup)) continue;
                var fCtx = BuildFormulaContext(fGroup, null, "");
                foreach (var pair in CardEngine.EvaluateBasicLogicCards(fGroup, fCtx, fGroup.TargetFieldKey))
                {
                    if (pair.FieldKey == fGroup.TargetFieldKey) { row.DisplayValue = pair.Value; break; }
                }
            }

            // Post-pass: PrefixSuffix card — transform display value for Logic rows
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing) continue;
                if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                string psGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                (CapabilitySet CapSet, CardGroup Group)? psFound = UserCapabilityStore?.FindGroup(psGroupId);
                var psGroup = psFound?.Group;
                if (psGroup == null || !CardEngine.HasPrefixSuffixCard(psGroup)) continue;
                var psConfig = CardEngine.GetPrefixSuffixConfig(psGroup);
                row.DisplayValue = CardEngine.ApplyPrefixSuffix(row.DisplayValue, psConfig.Prefix, psConfig.Suffix, psConfig.IsRemoveMode);
            }

            // Post-pass: V1 Expert Mode — auto-evaluate BL formulas for Expert groups with $[...] refs.
            // Pre-reset: clear stale amber/Apply button on all SPECIAL:LOGIC: rows.
            foreach (var row in Rows)
            {
                if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                if (!row.IsExpertPendingApply) continue;
                row.IsExpertPendingApply = false;
                row.ExpertComputedValue  = null;
                row.ValueForeground      = Brushes.Black;
            }
            try
            {
                var expertByGid = new Dictionary<string, System.Tuple<RowModel, CardGroup>>();
                foreach (var row in Rows)
                {
                    if (row.IsInlineEditing) continue;
                    if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                    string gid = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                    if (expertByGid.ContainsKey(gid)) continue;
                    var found = UserCapabilityStore?.FindGroup(gid);
                    var grp   = found?.Group;
                    if (grp == null || !grp.IsExpert) continue;
                    bool hasExpertBl = false;
                    foreach (var card in grp.Cards)
                    {
                        if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                        string f;
                        if (card.Params.TryGetValue(CardEngine.ParamFormula, out f) && FormulaEngine.HasExpertRef(f))
                        { hasExpertBl = true; break; }
                    }
                    if (!hasExpertBl) continue;
                    expertByGid[gid] = System.Tuple.Create(row, grp);
                }

                if (expertByGid.Count > 0)
                {
                    var expertGids = new System.Collections.Generic.HashSet<string>(expertByGid.Keys);
                    var inDegree   = new Dictionary<string, int>();
                    var outEdges   = new Dictionary<string, List<string>>();
                    foreach (string gid in expertByGid.Keys) { inDegree[gid] = 0; outEdges[gid] = new List<string>(); }

                    foreach (var kv in expertByGid)
                    {
                        string gid = kv.Key;
                        CardGroup grp = kv.Value.Item2;
                        foreach (var card in grp.Cards)
                        {
                            if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                            string formula;
                            if (!card.Params.TryGetValue(CardEngine.ParamFormula, out formula)) continue;
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

                    foreach (var kv in expertByGid)
                    {
                        if (inDegree[kv.Key] <= 0) continue;
                        RowModel cycleRow = kv.Value.Item1;
                        cycleRow.DisplayValue        = LanguageLoader.Get("Cycle_DisplayLabel");
                        cycleRow.ValueForeground     = Brushes.Red;
                        cycleRow.IsExpertPendingApply = false;
                        cycleRow.ExpertComputedValue  = null;
                    }

                    foreach (string gid in topoOrder)
                    {
                        var tuple  = expertByGid[gid];
                        RowModel topoRow = tuple.Item1;
                        CardGroup group  = tuple.Item2;
                        string catId     = CardEngine.GetPrimaryCatalogId(group);
                        CatalogData catalog = catId != null && UserCatalogStore != null
                            ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                            : null;
                        var ctx = BuildBasicLogicContext(catalog, "");
                        foreach (var card in group.Cards)
                        {
                            if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                            string formula;
                            if (!card.Params.TryGetValue(CardEngine.ParamFormula, out formula) || string.IsNullOrWhiteSpace(formula)) continue;
                            if (!FormulaEngine.HasExpertRef(formula)) continue;
                            string targetKey;
                            card.Params.TryGetValue(CardEngine.ParamFormulaTargetFieldKey, out targetKey);
                            if (string.IsNullOrEmpty(targetKey)) targetKey = group.TargetFieldKey;
                            if (!string.Equals(targetKey, group.TargetFieldKey, StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                string result = FormulaEngine.Evaluate(formula, ctx);
                                if (!result.StartsWith("#ERROR", StringComparison.Ordinal))
                                {
                                    topoRow.DisplayValue = result;
                                    string currentDocValue = "";
                                    foreach (var r in Rows)
                                        if (string.Equals(r.FieldKey, group.TargetFieldKey, StringComparison.OrdinalIgnoreCase))
                                        { currentDocValue = r.DisplayValue; break; }
                                    bool isPending = !string.Equals(result, currentDocValue, StringComparison.Ordinal);
                                    topoRow.IsExpertPendingApply = isPending;
                                    topoRow.ExpertComputedValue  = isPending ? result : null;
                                    topoRow.ValueForeground      = isPending ? _expertAmberBrush : Brushes.Black;
                                }
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }
            catch { }

            // Post-pass: Logic mismatch detection + multi-token segment computation.
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing || row.IsHalbzeugRow) continue;
                if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
                {
                    string pGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                    (CapabilitySet CapSet, CardGroup Group)? pFound = UserCapabilityStore?.FindGroup(pGroupId);
                    var pGroup = pFound?.Group;
                    if (pGroup != null)
                    {
                        // Single-value cards (Search/Dropdown) take priority over multi-token cards.
                        // A group that has Search or Dropdown always uses single-value display mode
                        // even when PairTransform or MultiPick are also present.
                        if (CardEngine.HasCard(pGroup, CardEngine.CardTypeDropdown) ||
                            CardEngine.HasCard(pGroup, CardEngine.CardTypeSearch))
                        {
                            row.IsMultiTokenMode = false;
                            var mismatch = ComputeLogicMismatch(row.DisplayValue, pGroup);
                            row.MatchedPart   = mismatch.Item1;
                            row.UnmatchedPart = mismatch.Item2;
                            continue;
                        }
                        if (CardEngine.HasMultiPickCard(pGroup) || CardEngine.HasPairTransformCard(pGroup) ||
                            CardEngine.HasSortCard(pGroup))
                        {
                            row.IsMultiTokenMode   = true;
                            row.MultiTokenSegments = ComputeMultiTokenSegments(row.DisplayValue, pGroup);
                            row.MatchedPart   = row.DisplayValue;
                            row.UnmatchedPart = "";
                            continue;
                        }
                    }
                }
                row.IsMultiTokenMode = false;
                row.MatchedPart   = row.DisplayValue;
                row.UnmatchedPart = "";
            }

            UpdateSyncIndicators();

            StatusMessage = string.Format(LanguageLoader.Get("Msg_Updated"), FileName, DateTime.Now.ToString("HH:mm:ss"));
            EnforceButtonRules();
        }

        private static string TryRead(Func<string> fn)
        {
            try { return fn(); } catch { return "n/a"; }
        }

        // Sets DisplayValue and ValueForeground from a list of per-doc values (selection order preserved).
        // All identical → show once, singleColor. Any difference → all values joined by " | ", Red.
        private static void SetAggregatedValue(RowModel row, List<string> values, Brush singleColor)
        {
            if (values.Count == 0) { row.DisplayValue = ""; row.ValueForeground = singleColor; return; }
            bool allSame = values.Count == 1 || values.All(v => v == values[0]);
            if (allSame)
            {
                row.DisplayValue    = values[0];
                row.ValueForeground = singleColor;
            }
            else
            {
                row.DisplayValue    = string.Join(" | ", values);
                row.ValueForeground = Brushes.Red;
            }
        }

        // ══════════════════════════════════════════════
        //  FORMULA ENGINE HELPERS
        // ══════════════════════════════════════════════

        private FormulaContext BuildFormulaContext(CardGroup group, CatalogData catalog, string priValue,
            string inputValue = null, string interceptFieldKey = null)
        {
            var rowCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (catalog != null && !string.IsNullOrEmpty(priValue))
            {
                string priKey = null;
                foreach (var c in catalog.Columns)
                    if (c.Role == ColumnRole.PrimaryDisplay && c.RoleIndex == 1) { priKey = c.Key; break; }
                if (priKey != null)
                {
                    foreach (var entry in catalog.Entries)
                    {
                        string v;
                        if (!entry.Values.TryGetValue(priKey, out v) ||
                            !string.Equals(v, priValue, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var col in catalog.Columns)
                        {
                            string colVal;
                            if (!entry.Values.TryGetValue(col.Key, out colVal)) continue;
                            if (!string.IsNullOrEmpty(col.Label)) rowCols[col.Label] = colVal;
                            string badge = CardEngine.RoleBadge(col.Role);
                            if (!string.IsNullOrEmpty(badge))   rowCols[badge]      = colVal;
                        }
                        break;
                    }
                }
            }

            string capturedInputValue        = inputValue;
            string capturedInterceptFieldKey = interceptFieldKey;
            var capturedGroup                = group;

            return new FormulaContext
            {
                RowColumns    = rowCols,
                GetFieldValue = fk =>
                {
                    if (capturedInterceptFieldKey != null
                        && string.Equals(fk, capturedInterceptFieldKey, StringComparison.Ordinal)
                        && capturedInputValue != null)
                        return capturedInputValue;
                    string dv = GetDisplayValueForField(fk);
                    if (!string.IsNullOrEmpty(dv)) return dv;
                    var doc = _selectedDocs.FirstOrDefault();
                    return doc != null ? (_catalogBuilder.ResolveFieldValue(fk, doc) ?? "") : "";
                },
                Lookup        = (key, searchCol, returnCol, catName)
                    => DoFormulaCatalogLookup(key, searchCol, returnCol, catName, capturedGroup),
                InputValue    = inputValue,
            };
        }

        private string GetDisplayValueForField(string fieldKey)
        {
            foreach (var row in Rows)
                if (row.FieldKey == fieldKey && !string.IsNullOrEmpty(row.DisplayValue))
                    return row.DisplayValue;
            return "";
        }

        private string DeriveFieldLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.StartsWith("IPROP|", StringComparison.Ordinal))
            {
                int last = key.LastIndexOf('|');
                return last >= 0 ? key.Substring(last + 1) : key.Substring("IPROP|".Length);
            }
            if (key.StartsWith("UDEF:", StringComparison.Ordinal))  return key.Substring("UDEF:".Length);
            if (key.StartsWith("DOC:",  StringComparison.Ordinal))  return key.Substring("DOC:".Length);
            if (key.StartsWith("PARAM:", StringComparison.Ordinal))
            {
                int last = key.LastIndexOf(':');
                return last >= 0 ? key.Substring(last + 1) : key.Substring("PARAM:".Length);
            }
            if (key.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
            {
                string groupId = key.Substring("SPECIAL:LOGIC:".Length);
                var found = UserCapabilityStore != null ? UserCapabilityStore.FindGroup(groupId) : null;
                return found != null ? found.Value.Group.Name ?? groupId : groupId;
            }
            return key;
        }

        private FormulaContext BuildBasicLogicContext(CatalogData catalog, string inputValue)
        {
            return new FormulaContext
            {
                InputValue        = inputValue ?? "",
                ResolveFieldValue = key =>
                {
                    if (string.IsNullOrEmpty(key)) return "";
                    foreach (var r in Rows)
                        if (r.FieldKey == key) return r.DisplayValue ?? "";
                    return "";
                },
                Lookup = (key, searchCol, returnCol, catName) =>
                {
                    CatalogData cat = null;
                    if (!string.IsNullOrEmpty(catName) && UserCatalogStore != null)
                        cat = UserCatalogStore.Catalogs.FirstOrDefault(c =>
                            string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Id,   catName, StringComparison.OrdinalIgnoreCase));
                    if (cat == null) cat = catalog;
                    return cat == null ? "" : (CardEngine.LookupByColumn(cat, key, searchCol, returnCol) ?? "");
                },
            };
        }

        private void ApplyExpertValue(RowModel row)
        {
            if (row == null || !row.IsExpertPendingApply || row.ExpertComputedValue == null) return;
            if (_selectedDocs.Count == 0) { StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite"); return; }

            string gid   = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
            var found    = UserCapabilityStore?.FindGroup(gid);
            if (found == null) return;
            string targetKey = found.Value.Group.TargetFieldKey;

            _stickyDocs = new List<Document>(_selectedDocs);
            var errors  = new List<System.Tuple<string, string>>();
            foreach (var doc in _selectedDocs)
            {
                string err = _fieldWriter.WriteFieldValue(doc, targetKey, row.ExpertComputedValue);
                if (err != null)
                {
                    string name = "";
                    try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                    errors.Add(System.Tuple.Create(name, err));
                }
            }

            if (errors.Count > 0)
            {
                _stickyDocs = null;
                StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
            }
            else
            {
                row.IsExpertPendingApply = false;
                row.ExpertComputedValue  = null;
                _catalogBuilder.InvalidateCache();
                DoRefresh();
            }
        }

        private string DoFormulaCatalogLookup(string key, string searchCol, string returnCol,
            string catalogName, CardGroup defaultGroup)
        {
            CatalogData cat = null;
            if (!string.IsNullOrEmpty(catalogName) && UserCatalogStore != null)
                cat = UserCatalogStore.Catalogs.FirstOrDefault(c =>
                    string.Equals(c.Name, catalogName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Id,   catalogName, StringComparison.OrdinalIgnoreCase));
            if (cat == null && defaultGroup != null && UserCatalogStore != null)
            {
                string catId = CardEngine.GetPrimaryCatalogId(defaultGroup);
                if (catId != null)
                    cat = UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId);
            }
            if (cat == null) return "";
            return CardEngine.LookupByColumn(cat, key, searchCol, returnCol) ?? "";
        }

        private List<Models.SpeziSegment> ComputeMultiTokenSegments(string displayValue, CardGroup group)
        {
            var result = new List<Models.SpeziSegment>();
            if (string.IsNullOrEmpty(displayValue)) return result;

            string catId  = CardEngine.GetPrimaryCatalogId(group);
            CatalogData catalog = catId != null && UserCatalogStore != null
                ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                : null;

            string sep;
            var validSet = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (catalog != null)
            {
                var cfg = CardEngine.GetMultiTokenAutoCompleteConfig(group, catalog);
                sep = !string.IsNullOrEmpty(cfg.Separator) ? cfg.Separator : "-";
                if (cfg.LookupColKey != null)
                    foreach (var entry in catalog.Entries)
                    {
                        string pv;
                        if (entry.Values.TryGetValue(cfg.LookupColKey, out pv) && !string.IsNullOrEmpty(pv))
                            validSet.Add(pv);
                    }
            }
            else
            {
                if (CardEngine.HasMultiPickCard(group))
                    sep = CardEngine.GetMultiPickConfig(group).PrimarySep;
                else if (CardEngine.HasPairTransformCard(group))
                    sep = CardEngine.GetPairTransformConfig(group).SourceSep;
                else if (CardEngine.HasSortCard(group))
                    sep = CardEngine.GetSortConfig(group).TokenSep;
                else
                    sep = "-";
            }

            string[] tokens = displayValue.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                bool isValid  = validSet.Count == 0 || validSet.Contains(token);
                string trailingSep = i < tokens.Length - 1 ? sep : "";
                result.Add(new Models.SpeziSegment(token, isValid, trailingSep));
            }
            return result;
        }

        private System.Tuple<string, string> ComputeLogicMismatch(string displayValue, CardGroup group)
        {
            if (string.IsNullOrEmpty(displayValue)) return System.Tuple.Create("", "");
            string catId  = CardEngine.GetPrimaryCatalogId(group);
            CatalogData catalog = catId != null && UserCatalogStore != null
                ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                : null;
            if (catalog == null) return System.Tuple.Create(displayValue, "");
            string priKey = null;
            foreach (var col in catalog.Columns)
                if (col.Role == ColumnRole.PrimaryDisplay && col.RoleIndex == 1) { priKey = col.Key; break; }
            if (priKey == null) return System.Tuple.Create(displayValue, "");

            string bestPrefix = null;
            foreach (var entry in catalog.Entries)
            {
                string pri;
                if (!entry.Values.TryGetValue(priKey, out pri) || string.IsNullOrEmpty(pri)) continue;
                if (string.Equals(pri, displayValue, StringComparison.OrdinalIgnoreCase))
                    return System.Tuple.Create(displayValue, "");
                if (displayValue.StartsWith(pri, StringComparison.OrdinalIgnoreCase))
                    if (bestPrefix == null || pri.Length > bestPrefix.Length)
                        bestPrefix = pri;
            }
            if (bestPrefix != null)
                return System.Tuple.Create(displayValue.Substring(0, bestPrefix.Length),
                                           displayValue.Substring(bestPrefix.Length));
            return System.Tuple.Create("", displayValue);
        }

        private void UpdateSyncIndicators()
        {
            if (UserCapabilityStore == null)
            {
                foreach (var row in Rows) row.IsConnected = false;
                return;
            }

            var syncSources = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var syncTargets = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cs in UserCapabilityStore.CapabilitySets)
            {
                foreach (var group in cs.Groups)
                {
                    if (!CardEngine.HasCard(group, CardEngine.CardTypeSync)) continue;
                    syncSources.Add("SPECIAL:LOGIC:" + group.Id);
                    foreach (var card in group.Cards)
                    {
                        if (!card.Enabled || card.Type != CardEngine.CardTypeSync) continue;
                        string key;
                        if (card.Params.TryGetValue(CardEngine.ParamCompanionFieldKey, out key) && !string.IsNullOrEmpty(key))
                            syncTargets.Add(key);
                    }
                }
            }

            foreach (var row in Rows)
                row.IsConnected = syncSources.Contains(row.FieldKey) || syncTargets.Contains(row.FieldKey);
        }

        // ══════════════════════════════════════════════
        //  ROW OPERATIONS
        // ══════════════════════════════════════════════

        private void EnsureLogicLinkAdjacency()
        {
            if (UserCapabilityStore == null) return;

            var alreadyPlacedAsPartner = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cs in UserCapabilityStore.CapabilitySets)
            {
                foreach (var group in cs.Groups)
                {
                    var partnerKeys = CardEngine.GetAllLinkPartnerFieldKeys(group);
                    if (partnerKeys.Count == 0) continue;

                    string logicKey = "SPECIAL:LOGIC:" + group.Id;
                    if (alreadyPlacedAsPartner.Contains(logicKey)) continue;

                    RowModel primary = null;
                    foreach (var r in Rows) { if (r.FieldKey == logicKey) { primary = r; break; } }
                    if (primary == null) continue;

                    primary.LinkedGroupId = group.Id;

                    RowModel prevRow = primary;
                    for (int slot = 0; slot < partnerKeys.Count; slot++)
                    {
                        string partnerKey = partnerKeys[slot];
                        RowModel partner  = null;
                        foreach (var r in Rows)
                        {
                            if (r.FieldKey == partnerKey && r != primary &&
                                (string.IsNullOrEmpty(r.LinkedGroupId) || r.LinkedGroupId == group.Id))
                            { partner = r; break; }
                        }

                        if (partner == null)
                        {
                            int prevIdx = Rows.IndexOf(prevRow);
                            var catalogItem = _fieldCatalog?.FirstOrDefault(f => f.Key == partnerKey);
                            partner = new RowModel
                            {
                                FieldKey        = partnerKey,
                                FieldLabel      = catalogItem != null ? catalogItem.RowLabel : partnerKey,
                                IsWritableField = catalogItem != null && catalogItem.IsWritable,
                                AllowedValues   = catalogItem != null ? catalogItem.AllowedValues : null,
                                LinkedGroupId   = group.Id,
                            };
                            Rows.Insert(Math.Min(prevIdx + 1, Rows.Count), partner);
                            return;
                        }

                        partner.LinkedGroupId = group.Id;
                        alreadyPlacedAsPartner.Add(partnerKey);

                        int prevIdx2   = Rows.IndexOf(prevRow);
                        int currentIdx = Rows.IndexOf(partner);
                        if (currentIdx != prevIdx2 + 1)
                        {
                            Rows.RemoveAt(currentIdx);
                            int newPrevIdx = Rows.IndexOf(prevRow);
                            Rows.Insert(Math.Min(newPrevIdx + 1, Rows.Count), partner);
                        }

                        prevRow = partner;
                    }
                }
            }
        }

        private void InsertRowBelow(RowModel refRow)
        {
            if (Rows.Count >= MAX_ROWS) { StatusMessage = string.Format(LanguageLoader.Get("Msg_MaxRows"), MAX_ROWS); return; }

            int idx = refRow != null ? Rows.IndexOf(refRow) : Rows.Count - 1;
            if (idx < 0) idx = Rows.Count - 1;

            Rows.Insert(idx + 1, new RowModel());
            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnsureMiterFlangeAdjacency();
            EnsureHalbzeugPairAdjacency();
            EnforceButtonRules();
            DoRefresh();
        }

        private void RemoveRow(RowModel row)
        {
            if (row == null || Rows.Count <= 1) return;

            if (Rows.Count <= 2 &&
                (row.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP ||
                 row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE))
                return;

            if (row.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP)
            {
                var dist = Rows.FirstOrDefault(r => r.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE);
                if (dist != null) Rows.Remove(dist);
                Rows.Remove(row);
                EnsureHalbzeugPairAdjacency();
                EnforceButtonRules();
                DoRefresh();
                return;
            }

            if (row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE)
            {
                var miterRow = null as RowModel;
                foreach (var r in Rows) { if (r.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP) { miterRow = r; break; } }
                if (miterRow != null) Rows.Remove(miterRow);
                Rows.Remove(row);
                EnsureHalbzeugPairAdjacency();
                EnforceButtonRules();
                DoRefresh();
                return;
            }

            // HalbzeugName removal: also remove HalbzeugIdent
            if (row.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_NAME)
            {
                var identRow = Rows.FirstOrDefault(r => r.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_IDENT);
                if (identRow != null) Rows.Remove(identRow);
                Rows.Remove(row);
                EnsureMiterFlangeAdjacency();
                EnforceButtonRules();
                DoRefresh();
                return;
            }

            // HalbzeugIdent cannot be removed while HalbzeugName is present
            if (row.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_IDENT &&
                Rows.Any(r => r.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_NAME))
                return;

            // Linked row removal — remove all rows sharing the same LinkedGroupId.
            if (!string.IsNullOrEmpty(row.LinkedGroupId))
            {
                foreach (var linked in Rows.Where(r => r.LinkedGroupId == row.LinkedGroupId && r != row).ToList())
                    Rows.Remove(linked);
            }
            else if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
            {
                string logicId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                RowModel partner = null;
                foreach (var r in Rows) { if (r.LinkedGroupId == logicId && r != row) { partner = r; break; } }
                if (partner != null) Rows.Remove(partner);
            }

            Rows.Remove(row);
            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnsureMiterFlangeAdjacency();
            EnsureHalbzeugPairAdjacency();
            EnforceButtonRules();
            DoRefresh();
        }

        public void OnRowDragDropCompleted(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return;
            if (fromIndex >= Rows.Count || toIndex >= Rows.Count) return;

            Rows.Move(fromIndex, toIndex);
            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnsureMiterFlangeAdjacency();
            EnsureHalbzeugPairAdjacency();
            EnforceButtonRules();
            DoRefresh();
        }

        private int IndexOfMiterGap()
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP) return i;
            return -1;
        }

        private int IndexOfFlangeDistance()
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE) return i;
            return -1;
        }

        private void EnsureMiterFlangeAdjacency()
        {
            int mIdx = IndexOfMiterGap();
            if (mIdx < 0) return;

            int dIdx = IndexOfFlangeDistance();
            if (dIdx < 0)
            {
                var distRow = new RowModel
                {
                    FieldKey = FieldCatalogBuilder.FIELD_FLANGE_DISTANCE,
                    FieldLabel = LanguageLoader.Get("Field_FlangeDistance"),
                    ValueForeground = Brushes.Red
                };
                Rows.Insert(Math.Min(mIdx + 1, Rows.Count), distRow);
                return;
            }

            if (dIdx != mIdx + 1)
            {
                var distRow = Rows[dIdx];
                Rows.RemoveAt(dIdx);
                mIdx = IndexOfMiterGap();
                Rows.Insert(Math.Min(mIdx + 1, Rows.Count), distRow);
            }
        }

        private int IndexOfHalbzeugName()
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_NAME) return i;
            return -1;
        }

        private int IndexOfHalbzeugIdent()
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_IDENT) return i;
            return -1;
        }

        private void EnsureHalbzeugPairAdjacency()
        {
            int n = IndexOfHalbzeugName();
            if (n < 0)
            {
                var orphan = Rows.FirstOrDefault(r => r.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_IDENT);
                if (orphan != null) Rows.Remove(orphan);
                return;
            }

            int id = IndexOfHalbzeugIdent();
            if (id < 0)
            {
                var identRow = new RowModel
                {
                    FieldKey        = FieldCatalogBuilder.FIELD_HALBZEUG_IDENT,
                    FieldLabel      = "ROHTEILIDENT",
                    IsWritableField = true
                };
                Rows.Insert(Math.Min(n + 1, Rows.Count), identRow);
                return;
            }

            if (id != n + 1)
            {
                var identRow = Rows[id];
                Rows.RemoveAt(id);
                n = IndexOfHalbzeugName();
                Rows.Insert(Math.Min(n + 1, Rows.Count), identRow);
            }
        }

        private void OnActiveMultiTokenRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var row = sender as RowModel;
            if (row == null) return;
            if (e.PropertyName == nameof(RowModel.IsInlineEditing) && !row.IsInlineEditing)
            {
                row.PropertyChanged -= OnActiveMultiTokenRowPropertyChanged;
                row.IsMultiTokenAutoCompleteOpen = false;
                if (_activeMultiTokenEditRow == row) _activeMultiTokenEditRow = null;
            }
        }

        public void UpdateMultiTokenAutoComplete(RowModel row, string editText, int caretIndex)
        {
            if (row == null || !row.IsMultiTokenMode) return;

            string groupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
            (CapabilitySet CapSet, CardGroup Group)? found = UserCapabilityStore?.FindGroup(groupId);
            if (found == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            string catId = CardEngine.GetPrimaryCatalogId(found.Value.Group);
            CatalogData catalog = null;
            if (catId != null && UserCatalogStore != null)
                foreach (var c in UserCatalogStore.Catalogs)
                    if (c.Id == catId) { catalog = c; break; }
            if (catalog == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            var cfg = CardEngine.GetMultiTokenAutoCompleteConfig(found.Value.Group, catalog);
            if (cfg.LookupColKey == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            string sep     = row.MultiTokenSeparator;
            string text    = editText ?? "";
            string partial = GetTokenAtCaret(text, sep, caretIndex);

            var items = new List<Models.SpeziAutoCompleteItem>();
            foreach (var entry in catalog.Entries)
            {
                string val;
                if (!entry.Values.TryGetValue(cfg.LookupColKey, out val) || string.IsNullOrEmpty(val)) continue;
                if (!val.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                string sec;
                entry.Values.TryGetValue(cfg.SecColKey ?? "", out sec);
                items.Add(new Models.SpeziAutoCompleteItem(val, sec ?? ""));
                if (items.Count >= 80) break;
            }

            row.AutoCompleteItems             = items;
            row.IsMultiTokenAutoCompleteOpen  = items.Count > 0;
        }

        public int InsertMultiToken(RowModel row, string value, int caretIndex)
        {
            if (row == null) return 0;
            string sep     = row.MultiTokenSeparator;
            string text    = row.EditText ?? "";
            string[] parts = text.Split(new[] { sep }, StringSplitOptions.None);

            int pos = 0;
            int activeIdx = parts.Length - 1;
            for (int i = 0; i < parts.Length; i++)
            {
                int end = pos + parts[i].Length;
                if (caretIndex <= end || i == parts.Length - 1) { activeIdx = i; break; }
                pos += parts[i].Length + sep.Length;
            }

            int tokenStart = 0;
            for (int i = 0; i < activeIdx; i++) tokenStart += parts[i].Length + sep.Length;

            parts[activeIdx]                  = value;
            row.EditText                      = string.Join(sep, parts);
            row.IsMultiTokenAutoCompleteOpen  = false;
            return tokenStart + value.Length;
        }

        private static string GetTokenAtCaret(string text, string sep, int caretIndex)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] parts = text.Split(new[] { sep }, StringSplitOptions.None);
            int pos = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                int end = pos + parts[i].Length;
                if (caretIndex <= end || i == parts.Length - 1) return parts[i];
                pos += parts[i].Length + sep.Length;
            }
            return "";
        }

        public void ApplyLogicPickerResult(RowModel row, string selectedPriValue)
        {
            if (row == null || string.IsNullOrEmpty(selectedPriValue)) return;
            if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string apGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
            (CapabilitySet CapSet, CardGroup Group)? apFound = UserCapabilityStore?.FindGroup(apGroupId);
            var group = apFound?.Group;
            if (group == null || string.IsNullOrEmpty(group.TargetFieldKey)) return;

            string catId   = CardEngine.GetPrimaryCatalogId(group);
            CatalogData catalog = catId != null && UserCatalogStore != null
                ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                : null;

            if (_selectedDocs.Count == 0) { StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite"); return; }

            _stickyDocs = new List<Document>(_selectedDocs);
            var errors = new List<string>();
            foreach (var doc in _selectedDocs)
            {
                string err = _fieldWriter.WriteFieldValue(doc, group.TargetFieldKey, selectedPriValue);
                if (err != null)
                {
                    string name = "";
                    try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                    errors.Add(string.Format("  {0}: {1}", name, err));
                }
            }

            if (errors.Count > 0)
            {
                _stickyDocs = null;
                string details = string.Join("\n", errors);
                MessageBox.Show(
                    string.Format("Write failed for {0} document(s):\n\n{1}", errors.Count, details),
                    "Checkup 2024 – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
                return;
            }

            if (catalog != null)
            {
                foreach (var syncPair in CardEngine.GetSyncWrites(group, catalog, selectedPriValue))
                    foreach (var doc in _selectedDocs)
                        _fieldWriter.WriteFieldValue(doc, syncPair.FieldKey, syncPair.Value);
            }

            if (CardEngine.HasBasicLogicCards(group))
            {
                var fCtx = BuildFormulaContext(group, catalog, selectedPriValue);
                foreach (var pair in CardEngine.EvaluateBasicLogicCards(group, fCtx, group.TargetFieldKey))
                    foreach (var doc in _selectedDocs)
                        _fieldWriter.WriteFieldValue(doc, pair.FieldKey, pair.Value);
            }

            row.IsInlineEditing = false;
            _catalogBuilder.InvalidateCache();
            DoRefresh();
        }

        public void ApplyMultiPickResult(RowModel row, IReadOnlyList<string> selectedPriValues)
        {
            if (row == null || selectedPriValues == null) return;
            if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string apGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
            (CapabilitySet CapSet, CardGroup Group)? apFound = UserCapabilityStore?.FindGroup(apGroupId);
            var group = apFound?.Group;
            if (group == null || string.IsNullOrEmpty(group.TargetFieldKey)) return;

            string catId   = CardEngine.GetPrimaryCatalogId(group);
            CatalogData catalog = catId != null && UserCatalogStore != null
                ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == catId)
                : null;

            var mpCfg    = CardEngine.GetMultiPickConfig(group);
            string priValue = string.Join(mpCfg.PrimarySep, selectedPriValues);

            if (_selectedDocs.Count == 0) { StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite"); return; }

            _stickyDocs = new List<Document>(_selectedDocs);
            var errors = new List<string>();
            foreach (var doc in _selectedDocs)
            {
                string err = _fieldWriter.WriteFieldValue(doc, group.TargetFieldKey, priValue);
                if (err != null)
                {
                    string name = "";
                    try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                    errors.Add(string.Format("  {0}: {1}", name, err));
                }
            }

            if (errors.Count > 0)
            {
                _stickyDocs = null;
                string details = string.Join("\n", errors);
                MessageBox.Show(
                    string.Format("Write failed for {0} document(s):\n\n{1}", errors.Count, details),
                    "Checkup 2024 – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
                return;
            }

            if (!string.IsNullOrEmpty(mpCfg.CompanionFieldKey) && catalog != null)
            {
                string companionValue = CardEngine.BuildMultiPickCompanionValue(
                    selectedPriValues, catalog, mpCfg.CompanionRole, mpCfg.CompanionSep);
                foreach (var doc in _selectedDocs)
                    _fieldWriter.WriteFieldValue(doc, mpCfg.CompanionFieldKey, companionValue);
            }

            row.IsInlineEditing = false;
            _catalogBuilder.InvalidateCache();
            DoRefresh();
        }

        /// <summary>
        /// Keeps row-level UI constraints consistent after any row add/remove/reorder operation.
        /// Called after every mutation. Rules:
        ///   - CanRemove=false when only 1 row would remain.
        ///   - MiterGap and FlangeDistance cannot be removed when only 2 rows remain (they form a pair).
        ///   - FlangeDistance CanRemove is locked whenever MiterGap is present.
        /// </summary>
        private void EnforceButtonRules()
        {
            int n          = Rows.Count;
            bool hasMiter  = IndexOfMiterGap()     >= 0;
            bool hasHalbzeug = IndexOfHalbzeugName() >= 0;

            for (int i = 0; i < n; i++)
            {
                var row = Rows[i];
                row.CanRemove = n > 1;
                if (n <= 2 &&
                    (row.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP ||
                     row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE))
                    row.CanRemove = false;
                if (hasMiter    && row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE)
                    row.CanRemove = false;
                if (hasHalbzeug && row.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG_IDENT)
                    row.CanRemove = false;
            }

            RelayCommand.RaiseCanExecuteChanged();
        }

        // ══════════════════════════════════════════════
        //  FIELD SELECTION
        // ══════════════════════════════════════════════

        private void OnFieldSelectionChanged(RowModel row)
        {
            if (_isRefreshing) return;
            if (row?.SelectedField == null) return;
            if (row.SelectedField.Key == row.FieldKey) return;

            row.FieldKey        = row.SelectedField.Key;
            row.FieldLabel      = row.SelectedField.RowLabel;
            row.IsWritableField = row.SelectedField.IsWritable;
            row.AllowedValues   = row.SelectedField.AllowedValues;
            row.IsMiterGapRow   = row.FieldKey == FieldCatalogBuilder.FIELD_MITER_GAP;
            row.IsEditable      = false;
            row.IsInlineEditing = false;

            row.ValueForeground = row.FieldKey == FieldCatalogBuilder.FIELD_FLANGE_DISTANCE
                ? Brushes.Red : Brushes.Black;

            // Pseudo-key: convert to real HalbzeugName and add HalbzeugIdent row below
            if (row.FieldKey == FieldCatalogBuilder.FIELD_HALBZEUG)
            {
                row.FieldKey        = FieldCatalogBuilder.FIELD_HALBZEUG_NAME;
                row.FieldLabel      = "ROHTEILNAME";
                row.IsWritableField = true;
                row.AllowedValues   = null;
                row.IsMiterGapRow   = false;
                row.ValueForeground = Brushes.Black;
                row.SelectedField   = null;
                EnsureHalbzeugPairAdjacency();
                EnsureMiterFlangeAdjacency();
                EnforceButtonRules();
                DoRefresh();
                return;
            }

            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnsureMiterFlangeAdjacency();
            EnsureHalbzeugPairAdjacency();
            EnforceButtonRules();
            DoRefresh();
        }

        // ══════════════════════════════════════════════
        //  FIELD EDIT (APPLY)
        // ══════════════════════════════════════════════

        private void ApplyFieldEdit(RowModel row)
        {
            if (row == null) return;

            if (row.IsMiterGapRow)
            {
                ApplyMiterGap(row);
                return;
            }

            if (!row.HasValueChanged) return;

            string newValue = row.EditText?.Trim() ?? "";

            if (_selectedDocs.Count == 0)
            {
                StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite");
                row.IsInlineEditing = false;
                return;
            }

            // Resolve write target for SPECIAL:LOGIC:* rows (redirect to the group's TargetFieldKey)
            string writeFieldKey = row.FieldKey;
            CardGroup   logicGroup   = null;
            CatalogData logicCatalog = null;
            if (row.FieldKey.StartsWith("SPECIAL:LOGIC:"))
            {
                string apGroupId = row.FieldKey.Substring("SPECIAL:LOGIC:".Length);
                (CapabilitySet CapSet, CardGroup Group)? apFound = UserCapabilityStore?.FindGroup(apGroupId);
                logicGroup = apFound?.Group;
                if (logicGroup == null || string.IsNullOrEmpty(logicGroup.TargetFieldKey))
                {
                    StatusMessage = "Logic Set has no target field configured.";
                    row.IsInlineEditing = false;
                    return;
                }
                writeFieldKey = logicGroup.TargetFieldKey;
                string apCatId = CardEngine.GetPrimaryCatalogId(logicGroup);
                logicCatalog = apCatId != null && UserCatalogStore != null
                    ? UserCatalogStore.Catalogs.FirstOrDefault(c => c.Id == apCatId)
                    : null;

                if (CardEngine.HasBasicLogicCards(logicGroup))
                {
                    var fCtx = BuildFormulaContext(logicGroup, null, "",
                        inputValue: newValue, interceptFieldKey: logicGroup.TargetFieldKey);
                    foreach (var pair in CardEngine.EvaluateBasicLogicCards(logicGroup, fCtx, logicGroup.TargetFieldKey))
                        foreach (var doc in _selectedDocs)
                            _fieldWriter.WriteFieldValue(doc, pair.FieldKey, pair.Value);
                    _stickyDocs = new List<Document>(_selectedDocs);
                    row.IsInlineEditing = false;
                    _catalogBuilder.InvalidateCache();
                    DoRefresh();
                    return;
                }

                // PrefixSuffix card: inverse-transform newValue before storing.
                // Add mode: user sees prefix+raw+suffix → strip to get raw.
                // Remove mode: user sees stripped value → add back to get stored form.
                if (CardEngine.HasPrefixSuffixCard(logicGroup))
                {
                    var psConfig = CardEngine.GetPrefixSuffixConfig(logicGroup);
                    newValue = CardEngine.ApplyPrefixSuffix(newValue, psConfig.Prefix, psConfig.Suffix, !psConfig.IsRemoveMode);
                }

                // Sort card: sort the tokens by SRT column before storing.
                if (CardEngine.HasSortCard(logicGroup))
                {
                    var srtConfig = CardEngine.GetSortConfig(logicGroup);
                    newValue = CardEngine.BuildSortedValue(newValue, logicCatalog, srtConfig.LookupRole, srtConfig.TokenSep, srtConfig.IsInvert);
                }
            }

            // Snapshot the current selection before writing; Inventor may clear SelectSet
            // after a document update. DoRefreshCore will use this sticky list if SelectSet
            // is empty after the write completes.
            _stickyDocs = new List<Document>(_selectedDocs);

            var errors = new List<string>();
            foreach (var doc in _selectedDocs)
            {
                string err = _fieldWriter.WriteFieldValue(doc, writeFieldKey, newValue);
                if (err != null)
                {
                    string name = "";
                    try { name = System.IO.Path.GetFileName(doc.FullFileName); }
                    catch { name = doc.DisplayName; }
                    errors.Add(string.Format("  {0}: {1}", name, err));
                }
            }

            if (errors.Count > 0)
            {
                _stickyDocs = null;
                string details = string.Join("\n", errors);
                MessageBox.Show(
                    string.Format("Write failed for {0} document(s):\n\n{1}", errors.Count, details),
                    "Checkup 2024 – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
            }
            else
            {
                // Sync card + PairTransform writes for Logic Set rows
                if (logicGroup != null && logicCatalog != null)
                {
                    foreach (var syncPair in CardEngine.GetSyncWrites(logicGroup, logicCatalog, newValue))
                        foreach (var doc in _selectedDocs)
                            _fieldWriter.WriteFieldValue(doc, syncPair.FieldKey, syncPair.Value);

                    if (CardEngine.HasPairTransformCard(logicGroup))
                    {
                        var ptCfg = CardEngine.GetPairTransformConfig(logicGroup);
                        if (!string.IsNullOrEmpty(ptCfg.CompanionFieldKey))
                        {
                            string transformed = CardEngine.BuildPairTransformValue(
                                newValue, logicCatalog, ptCfg.SourceSep, ptCfg.LookupRole, ptCfg.OutputRole, ptCfg.OutputSep);
                            foreach (var doc in _selectedDocs)
                                _fieldWriter.WriteFieldValue(doc, ptCfg.CompanionFieldKey, transformed);
                        }
                    }
                }

                row.IsInlineEditing = false;
                _catalogBuilder.InvalidateCache();
                DoRefresh();
            }
        }

        private void ApplyMiterGap(RowModel row)
        {
            if (row == null) return;

            string exprIn = row.EditText?.Trim() ?? "";
            if (exprIn == "") return;

            var smParts = _selectedDocs
                .OfType<PartDocument>()
                .Where(p => p.ComponentDefinition is SheetMetalComponentDefinition)
                .ToList();

            if (smParts.Count == 0)
            {
                MessageBox.Show("No Sheet Metal parts in selection.", "Checkup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Snapshot the current selection before writing; part.Update() triggers Inventor
            // events that may clear the IAM SelectSet before we call DoRefresh.
            _stickyDocs = new List<Document>(_selectedDocs);

            var errors  = new List<string>();
            int success = 0;

            foreach (var part in smParts)
            {
                string partName = "";
                try { partName = System.IO.Path.GetFileName(part.FullFileName); }
                catch { partName = part.DisplayName; }

                var flange2 = _smReader.FindSecondFlange(part);
                if (flange2 == null) { errors.Add($"{partName}: No 2nd flange found."); continue; }

                double currentCm;
                try { currentCm = _smReader.ReadMiterGapCm(flange2.Definition); }
                catch (Exception ex) { errors.Add($"{partName}: Miter gap read failed: {ex.Message}"); continue; }

                ModelParameter miterParam = null;
                foreach (ModelParameter p in part.ComponentDefinition.Parameters.ModelParameters)
                {
                    if (Math.Abs((double)p.Value - currentCm) < 0.0000001)
                    {
                        miterParam = p;
                        break;
                    }
                }

                if (miterParam == null)
                {
                    errors.Add($"{partName}: Could not identify Miter Gap parameter by value match.");
                    continue;
                }

                // If the user typed a plain number append the document's length unit ("1.5 mm").
                // If they typed an expression ("d25" or "1.5 mm") pass it through unchanged.
                string finalExpr = exprIn;
                string normalized = exprIn.Replace(",", ".").Trim();
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double numVal))
                {
                    string unit = SheetMetalReader.UnitAbbreviation(part.UnitsOfMeasure.LengthUnits);
                    finalExpr = numVal.ToString("0.######", CultureInfo.InvariantCulture) + " " + unit;
                }

                try
                {
                    miterParam.Expression = finalExpr;
                    part.Update();
                    success++;
                }
                catch (Exception ex) { errors.Add($"{partName}: Could not set Miter Gap: {ex.Message}"); }
            }

            if (errors.Count > 0)
            {
                if (success == 0) _stickyDocs = null; // all writes failed — release sticky
                MessageBox.Show(
                    $"Miter Gap set on {success} part(s).\nErrors ({errors.Count}):\n\n" +
                    string.Join("\n", errors),
                    "Checkup 2024", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            row.IsInlineEditing = false;
            DoRefresh();
        }

        // ══════════════════════════════════════════════
        //  PRESETS
        // ══════════════════════════════════════════════

        private void ApplyPreset(int index)
        {
            if (_presets == null || index < 0 || index >= _presets.Count) return;

            Rows.Clear();
            foreach (var key in _presets[index].FieldKeys)
                Rows.Add(new RowModel { FieldKey = key });

            SetActivePreset(index);
            DoRefresh();
            StatusMessage = string.Format(LanguageLoader.Get("Msg_PresetApplied"), _presets[index].Name, DateTime.Now.ToString("HH:mm:ss"));
        }

        public string GetPresetName(int index) =>
            (index >= 0 && index < _presets?.Count) ? _presets[index].Name : "";

        public void SavePreset(int index, string name)
        {
            if (_presets == null || index < 0 || index >= _presets.Count) return;

            _presets[index].Name      = name;
            _presets[index].FieldKeys = Rows.Select(r => r.FieldKey).ToList();
            _presetsManager.Save(_presets);

            switch (index)
            {
                case 0: Preset1Name = name; break;
                case 1: Preset2Name = name; break;
                case 2: Preset3Name = name; break;
            }

            StatusMessage = string.Format(LanguageLoader.Get("Msg_PresetSaved"), name, DateTime.Now.ToString("HH:mm:ss"));
        }

        public void ExportPreset(int slotIndex, string path)
        {
            try
            {
                _presetsManager.ExportPresetToLibrary(_presets[slotIndex], path);
                StatusMessage = string.Format(LanguageLoader.Get("Msg_PresetExported"),
                    _presets[slotIndex].Name, DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(LanguageLoader.Get("Msg_ExportFailed"), DiagLogger.S(ex.Message));
            }
        }

        public void ExportAllPresets(string path)
        {
            try
            {
                _presetsManager.ExportAllPresetsToLibrary(_presets, path);
                StatusMessage = string.Format(LanguageLoader.Get("Msg_AllPresetsExported"), DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(LanguageLoader.Get("Msg_ExportFailed"), DiagLogger.S(ex.Message));
            }
        }

        public List<PresetData> ReadLibraryPresets(string path)
        {
            try
            {
                return _presetsManager.ReadLibrary(path);
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(LanguageLoader.Get("Msg_ImportFailed"), DiagLogger.S(ex.Message));
                return null;
            }
        }

        public void ImportPresetIntoSlot(int slotIndex, PresetData data)
        {
            _presets[slotIndex] = new PresetData { Name = data.Name, FieldKeys = new List<string>(data.FieldKeys) };
            _presetsManager.Save(_presets);
            if (slotIndex == 0) Preset1Name = _presets[0].Name;
            else if (slotIndex == 1) Preset2Name = _presets[1].Name;
            else Preset3Name = _presets[2].Name;
            int activeIdx = UiStateStore.LoadActivePresetIndex();
            if (activeIdx == slotIndex) ApplyPreset(slotIndex);
            StatusMessage = string.Format(LanguageLoader.Get("Msg_PresetImported"),
                data.Name, DateTime.Now.ToString("HH:mm:ss"));
        }

        // ══════════════════════════════════════════════
        //  RESET
        // ══════════════════════════════════════════════

        private void ResetToDefaults()
        {
            UiStateStore.ClearCatalogBuilderPanelStates();
            UiStateStore.ClearWindowSizes();
            RequestResetWindowSize?.Invoke();
            _presetsManager?.ResetToDefaults();
            _presets = _presetsManager?.GetDefaults() ?? new List<PresetData>();

            if (_presets.Count == 3)
            {
                Preset1Name = _presets[0].Name;
                Preset2Name = _presets[1].Name;
                Preset3Name = _presets[2].Name;
            }

            _catalogBuilder?.InvalidateCache();
            InitializeDefaultRows();
            SetActivePreset(0);
            DoRefresh();
            StatusMessage = string.Format(LanguageLoader.Get("Msg_ResetDone"), DateTime.Now.ToString("HH:mm:ss"));
        }

        // ══════════════════════════════════════════════
        //  PURGE STYLES
        // ══════════════════════════════════════════════

        private void DoPurgeStyles()
        {
            var doc = _docResolver.GetActiveOrSelectedDocument(out string error);
            if (doc == null) { StatusMessage = error; return; }
            string result;
            try
            {
                result = _stylePurger.UpdateAndPurge(doc);
            }
            catch (Exception ex)
            {
                result = $"Fehler beim Bereinigen: {ex.Message}";
            }
            // DoRefresh overwrites StatusMessage — set the purge result after it.
            DoRefresh();
            StatusMessage = result;
        }

        // ══════════════════════════════════════════════
        //  INFO
        // ══════════════════════════════════════════════

        private void ShowInfo()
        {
            new Views.InfoDialog(Services.InfoPanelBuilder.BuildMainWindowHelp(),
                "MainAddin", "Win_Title_CheckupInfo", 520, 480).ShowDialog();
        }

        // ══════════════════════════════════════════════
        //  MULTI-COLUMN LOGIC DROPDOWN (U1)
        // ══════════════════════════════════════════════

        private const double MinColumnWidth  = 40.0;
        private const double ColumnPaddingPx = 20.0;

        private static readonly System.Windows.Media.Typeface _tfNormal = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        private static readonly System.Windows.Media.Typeface _tfBold = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);

        public static double MeasureLogicDropdownText(string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var ft = new System.Windows.Media.FormattedText(
                text, System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, bold ? _tfBold : _tfNormal, 11,
                System.Windows.Media.Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }

        public void RescaleLogicDropdownColumns(RowModel row, double fieldWidth)
        {
            if (fieldWidth < 1 || row == null) return;
            row.LogicDropdownFieldWidth = fieldWidth;
            if (row.LogicDropdownColumns == null || row.LogicDropdownColumns.Count == 0) return;
            double totalWidth = 0;
            foreach (var col in row.LogicDropdownColumns) totalWidth += col.Width;
            if (totalWidth > fieldWidth)
            {
                double scale = fieldWidth / totalWidth;
                foreach (var col in row.LogicDropdownColumns)
                    col.Width = Math.Max(MinColumnWidth, col.Width * scale);
            }
        }

        private void PopulateLogicDropdownColumns(RowModel row, CardGroup group, CatalogData catalog)
        {
            if (row == null) return;
            if (catalog == null || row.CatalogDropdownItems == null || row.CatalogDropdownItems.Count == 0)
            {
                row.LogicDropdownColumns = System.Array.Empty<LogicDropdownColumn>();
                row.LogicDropdownRows    = System.Array.Empty<LogicDropdownItemRow>();
                return;
            }

            var specs = CardEngine.GetLogicDropdownColumnSpecs(group, catalog);
            if (specs.Count == 0)
            {
                row.LogicDropdownColumns = System.Array.Empty<LogicDropdownColumn>();
                row.LogicDropdownRows    = System.Array.Empty<LogicDropdownItemRow>();
                return;
            }

            string ctxKey = catalog.Id ?? "";
            row.LogicDropdownContextKey = ctxKey;

            double savedHeight;
            double savedWidthIgnored;
            if (UiStateStore.TryLoadLogicDropdownSize(ctxKey, out savedWidthIgnored, out savedHeight) && savedHeight >= 80)
                row.LogicDropdownPopupHeight = savedHeight;

            double[] savedWidths;
            UiStateStore.TryLoadLogicDropdownColumnWidths(ctxKey, out savedWidths);

            var columns = new List<LogicDropdownColumn>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                bool isLast = i == specs.Count - 1;
                double w;
                if (savedWidths != null && i < savedWidths.Length && savedWidths[i] >= MinColumnWidth)
                {
                    w = savedWidths[i];
                }
                else
                {
                    w = MeasureLogicDropdownText(specs[i].Label, bold: true);
                    int srcIdx = specs[i].SourceIndex;
                    foreach (var it in row.CatalogDropdownItems)
                    {
                        if (srcIdx < 0 || srcIdx >= it.AllDisplayValues.Count) continue;
                        double cw = MeasureLogicDropdownText(it.AllDisplayValues[srcIdx], bold: false);
                        if (cw > w) w = cw;
                    }
                    w += ColumnPaddingPx;
                    if (w < MinColumnWidth) w = MinColumnWidth;
                }
                columns.Add(new LogicDropdownColumn(specs[i].Label, w, isLast, specs[i].SourceIndex));
            }
            row.LogicDropdownColumns = columns;

            var rows = new List<LogicDropdownItemRow>(row.CatalogDropdownItems.Count);
            foreach (var it in row.CatalogDropdownItems)
                rows.Add(new LogicDropdownItemRow(it, columns));
            row.LogicDropdownRows = rows;
        }

        public void SaveLogicDropdownColumnWidths(RowModel row)
        {
            if (row == null || row.LogicDropdownColumns == null || row.LogicDropdownColumns.Count == 0) return;
            if (string.IsNullOrEmpty(row.LogicDropdownContextKey)) return;
            var widths = new double[row.LogicDropdownColumns.Count];
            for (int i = 0; i < widths.Length; i++) widths[i] = row.LogicDropdownColumns[i].Width;
            UiStateStore.SaveLogicDropdownColumnWidths(row.LogicDropdownContextKey, widths);
        }

        // ══════════════════════════════════════════════
        //  INotifyPropertyChanged
        // ══════════════════════════════════════════════

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
