using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    ///
    /// Write flow (user edits a field):
    ///   StartInlineEditCommand → row.IsInlineEditing=true → user types → ApplyFieldEditCommand
    ///     → ApplyFieldEdit → FieldWriter.WriteFieldValue → DoRefresh
    ///
    /// Row constraints (enforced by EnforceButtonRules after every mutation):
    ///   - Maximum MAX_ROWS rows total.
    /// </remarks>
    public class CheckupViewModel : INotifyPropertyChanged
    {
        // ── Inventor interop services ──
        private readonly Inventor.Application _app;
        internal Inventor.Application AppInstance => _app;
        private readonly DocumentResolver     _docResolver;
        private readonly FieldCatalogBuilder  _catalogBuilder;
        private readonly StylePurger          _stylePurger;
        private readonly PresetsManager       _presetsManager;
        private readonly FieldWriter          _fieldWriter;
        private readonly CatalogStore         _catalogStore;
        public           CatalogStore         UserCatalogStore     => _catalogStore;
        private readonly CapabilityStore      _capabilityStore;
        public           CapabilityStore      UserCapabilityStore  => _capabilityStore;

        // ── Runtime state ──
        private ApplicationEvents        _appEvents;
        private UserInputEvents          _userInputEvents;   // held to prevent GC + ensure same RCW for unsub
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
        // Refresh value cache — skips all COM reads on unchanged-assembly ticks (doc-set signature match).
        private bool                                _refreshCacheInvalid    = true;
        private string                              _refreshCacheSig        = "";
        private readonly Dictionary<string, string> _refreshValueCache      = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _refreshFormulaCache    = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool>   _refreshHasFormulaCache = new(StringComparer.Ordinal);
        private DispatcherTimer          _autoRefresh;
        private EventHandler             _selectDebounceTick;
        private EventHandler             _selectSetPollerTick;
        private EventHandler             _autoRefreshTick;
        // Guards against re-entrant refreshes (e.g. FieldCatalog setter triggering another DoRefresh).
        private bool                     _isRefreshing;
        // Guards against UnsubscribeFromInventorEvents being called more than once (Deactivate,
        // OnClosing, and the Closed event lambda can all call it in sequence).
        private bool                     _eventsUnsubscribed;
        // Remembered selection from the last write; persists until user selects something new
        // or switches document. Cleared only by a new non-empty selection or document activation —
        // NOT by deselect events, which Inventor fires unpredictably after document updates.
        private List<Document>           _stickyDocs   = null;
        // Documents resolved on each refresh; used by ApplyFieldEdit for batch writes.
        private List<Document>           _selectedDocs = new();
        // Instance counts per FullFileName from the last SelectSet read (only entries > 1 stored).
        private Dictionary<string, int>  _instanceCounts = new(StringComparer.OrdinalIgnoreCase);
        // Sub-assembly grouping: parentIamFilename → (partFilename → count).  Empty key = top-level.
        private Dictionary<string, Dictionary<string, int>> _subAsmGroups = new(StringComparer.OrdinalIgnoreCase);
        // 0=Plain, 1=Compact, 2=Detailed — persisted via UiStateStore.
        private int                      _fileNameViewMode = 0;
        private int                      _activePresetIndex = -1;
        private const int MAX_ROWS = 30;
        // Tracks which multi-token Logic row is currently in edit mode (for per-token autocomplete).
        private RowModel _activeMultiTokenEditRow;

        // ── Demo mode warning ──
        private const string DemoPresetName = "Demo";
        private static readonly string[] _demoDefaultFieldKeys =
        {
            "IPROP|Description", "IPROP|Part Number", "DOC:Material", "DOC:Appearance",
            "IPROP|Revision Number", "SPECIAL:LOGIC:demo-g03",
            "SPECIAL:LOGIC:demo-g01", "SPECIAL:LOGIC:demo-g08"
        };
        // Static: persists across window close/reopen within the same Inventor session (AppDomain lifetime).
        private static int  _demoWindowOpenCount  = 0;
        private static bool _demoShownThisSession = false;


        // ══════════════════════════════════════════════
        //  OBSERVABLE PROPERTIES
        // ══════════════════════════════════════════════

        private string _fileName = "";
        public string FileName
        {
            get => _fileName;
            set { _fileName = value ?? ""; OnPropertyChanged(); }
        }

        // View-mode cycle button label, fully managed by the language catalog (letter + symbol).
        // Each click advances the mode, which refreshes this label.
        public string FileNameViewModeLabel
        {
            get
            {
                string key = _fileNameViewMode == 1 ? "Lbl_ViewMode_Compact"
                           : _fileNameViewMode == 2 ? "Lbl_ViewMode_Detailed"
                           : "Lbl_ViewMode_Plain";
                return LanguageLoader.Get(key);
            }
        }

        // 12px font ≈ 16px line height; 2 lines ≈ 36, 5 lines ≈ 84 (incl. ascender/descender slack).
        public double FileNameMaxHeight => _fileNameViewMode == 2 ? 84 : 36;

        private string _statusMessage = "Ready.";
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? ""; OnPropertyChanged(); }
        }

        private ObservableCollection<RowModel> _rows = new();
        public ObservableCollection<RowModel> Rows
        {
            get => _rows;
            set { _rows = value; OnPropertyChanged(); }
        }

        private List<FieldItem> _fieldCatalog = new();
        public List<FieldItem> FieldCatalog
        {
            get => _fieldCatalog;
            set
            {
                if (ReferenceEquals(_fieldCatalog, value)) return;
                _fieldCatalog = value ?? new();
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

        private IReadOnlyList<FieldItem> _stickyFieldItems = Array.Empty<FieldItem>();
        public IReadOnlyList<FieldItem> StickyFieldItems
        {
            get => _stickyFieldItems;
            private set { _stickyFieldItems = value; OnPropertyChanged(); }
        }

        private ListCollectionView _scrollableGroupedCatalog;
        public ListCollectionView ScrollableGroupedCatalog
        {
            get => _scrollableGroupedCatalog;
            private set { _scrollableGroupedCatalog = value; OnPropertyChanged(); }
        }

        // ── Field Selector: Favoriten zone + search filter + collapsible groups ──

        private readonly List<string> _pinnedFieldKeys = new();

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

        private IReadOnlyList<PinnedFieldEntry> _favoritenItems = System.Array.Empty<PinnedFieldEntry>();
        public IReadOnlyList<PinnedFieldEntry> FavoritenItems
        {
            get => _favoritenItems;
            private set { _favoritenItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPinnedItems)); }
        }

        public bool HasPinnedItems => _favoritenItems.Count > 0;

        private IReadOnlyList<FieldSelectorGroupVm> _scrollableGroups = System.Array.Empty<FieldSelectorGroupVm>();
        public IReadOnlyList<FieldSelectorGroupVm> ScrollableGroups
        {
            get => _scrollableGroups;
            private set { _scrollableGroups = value; OnPropertyChanged(); }
        }

        public RelayCommand ToggleFieldPinCommand       { get; private set; }
        public RelayCommand ClearFieldSelPrefsCommand   { get; private set; }

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

        public RelayCommand PurgeStylesCommand             { get; }
        public RelayCommand Preset1Command                 { get; }
        public RelayCommand Preset2Command                 { get; }
        public RelayCommand Preset3Command                 { get; }
        public RelayCommand InfoCommand                    { get; }
        public RelayCommand ResetCommand                   { get; }
        public RelayCommand CloseCommand                   { get; }
        public RelayCommand AddRowCommand                  { get; }
        public RelayCommand RemoveRowCommand               { get; }
        public RelayCommand StartInlineEditCommand         { get; }
        public RelayCommand ApplyFieldEditCommand          { get; }
        public RelayCommand CancelFieldEditCommand         { get; }
        public RelayCommand FieldSelectionChangedCommand   { get; }
        public RelayCommand ApplyExpertValueCommand        { get; }
        public RelayCommand ToggleFormulaEditCommand       { get; }
        public RelayCommand CycleFileNameViewModeCommand   { get; }

        private static readonly System.Windows.Media.SolidColorBrush _expertAmberBrush =
            new(System.Windows.Media.Color.FromRgb(0xD4, 0xA0, 0x17));

        public event Action RequestClose;
        public event Action RequestResetWindowSize;

        // ══════════════════════════════════════════════
        //  DESIGN-TIME CONSTRUCTOR (parameterless)
        // ══════════════════════════════════════════════

        public CheckupViewModel()
        {
            // VS Designer uses this constructor — populate with dummy data only.
            PurgeStylesCommand           = new RelayCommand(() => { });
            Preset1Command               = new RelayCommand(() => { });
            Preset2Command               = new RelayCommand(() => { });
            Preset3Command               = new RelayCommand(() => { });
            InfoCommand                  = new RelayCommand(() => { });
            ResetCommand                 = new RelayCommand(() => { });
            CloseCommand                 = new RelayCommand(() => { });
            AddRowCommand                = new RelayCommand(_ => { });
            RemoveRowCommand             = new RelayCommand(_ => { });
            StartInlineEditCommand       = new RelayCommand(_ => { });
            ApplyFieldEditCommand        = new RelayCommand(_ => { });
            CancelFieldEditCommand       = new RelayCommand(_ => { });
            FieldSelectionChangedCommand = new RelayCommand(_ => { });
            ApplyExpertValueCommand      = new RelayCommand(_ => { });
            ToggleFormulaEditCommand     = new RelayCommand(_ => { });

            Preset1Name = "Allgemein";
            Preset2Name = "Baugruppe";
            Preset3Name = "Bauteil";

            FieldCatalog = new List<FieldItem>
            {
                new FieldItem("", "(kein)", "", ""),
                new FieldItem("UDEF:ISO",              "ISO",        "ISO",        "Grp_iPropertiesCustom", true),
                new FieldItem("DOC:Material",          "Material",   "Material",   "Grp_Document",          true),
                new FieldItem("PARAM:User:Thickness",  "Thickness",  "Thickness",  "Grp_ParamUser",         true),
            };

            Rows.Add(new RowModel { FieldKey = "UDEF:ISO",                FieldLabel = "ISO",                DisplayValue = "1234-567-A",  IsWritableField = true });
            Rows.Add(new RowModel { FieldKey = "DOC:Material",            FieldLabel = "Material",           DisplayValue = "Steel",       IsWritableField = true });
            Rows.Add(new RowModel { FieldKey = "PARAM:User:Thickness",    FieldLabel = "Thickness",          DisplayValue = "2.000 mm",    IsWritableField = true });

            // Link each row's SelectedField to its matching FieldItem so the ComboBox header shows a label.
            foreach (var row in Rows)
                row.SelectedField = _fieldCatalog.FirstOrDefault(fi => fi.Key == row.FieldKey);

            // Show preset 1 as active so the indicator dot is visible in the designer.
            _activePresetIndex = 0;

            FileName      = "ExamplePart.ipt";
            StatusMessage = "Design-time preview";
            EnforceButtonRules();
        }

        // ══════════════════════════════════════════════
        //  RUNTIME CONSTRUCTOR
        // ══════════════════════════════════════════════

        public CheckupViewModel(Inventor.Application app, UserSettings settings = null, CatalogStore catalogStore = null, CapabilityStore capabilityStore = null)
        {
            _app              = app;
            _catalogStore     = catalogStore;
            _capabilityStore  = capabilityStore;
            _docResolver    = new DocumentResolver(app);
            _catalogBuilder = new FieldCatalogBuilder(_app, _capabilityStore);
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
                    if (p is RowModel row && row.IsWritableField && !row.IsEditable)
                    {
                        // Logic rows: populate catalog items for Dropdown/Search cards.
                        // Formula-only Logic rows enter inline edit (never auto-apply on click).
                        bool isFormulaOnlyGroup = false;
                        if (row.FieldKey.StartsWith("SPECIAL:LOGIC:"))
                        {
                            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
                            var found = _capabilityStore?.FindGroup(groupId);
                            if (found != null)
                            {
                                var group   = found.Value.Group;
                                bool hasPrimary = CardEngine.HasCard(group, CardEngine.CardTypeDropdown)
                                              || CardEngine.HasCard(group, CardEngine.CardTypeSearch)
                                              || CardEngine.HasCard(group, CardEngine.CardTypeButton)
                                              || CardEngine.HasCard(group, CardEngine.CardTypeMultiPick);

                                if (!hasPrimary && (CardEngine.HasPairTransformCard(group) || CardEngine.HasBasicLogicCard(group) || CardEngine.HasPrefixSuffixCard(group)))
                                {
                                    isFormulaOnlyGroup = true; // Apply always visible: typed value feeds {INPUT} for BL / PairTransform companion mapping, or is inverse-transformed for PrefixSuffix
                                }
                                else
                                {
                                    string catId = CardEngine.GetPrimaryCatalogId(group);
                                    var catalog  = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
                                    if (CardEngine.HasCard(group, CardEngine.CardTypeSearch))
                                    {
                                        row.CatalogDropdownItems = CardEngine.GetSearchItemsForCard(group, catalog);
                                        row.IsLogicSearchMode    = true;
                                        PopulateLogicDropdownColumns(row, group, catalog);
                                    }
                                    else if (CardEngine.HasCard(group, CardEngine.CardTypeDropdown))
                                    {
                                        row.CatalogDropdownItems = CardEngine.GetDropdownItemsForCard(group, catalog);
                                        row.IsLogicSearchMode    = false;
                                        PopulateLogicDropdownColumns(row, group, catalog);
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

                        // Pre-fill with common value when all docs agree; blank when they differ (user must type fresh).
                        string startText = row.DisplayValue.IndexOf(" | ", StringComparison.Ordinal) >= 0 ? "" : row.DisplayValue;
                        // Set OriginalValue BEFORE EditText so TextChanged guards (EditText != OriginalValue) work correctly.
                        // Formula groups: OriginalValue="" (null→"") so Apply/Cancel always appear on entry.
                        row.OriginalValue   = isFormulaOnlyGroup ? null : startText;
                        // Use suppress-filter variant so Logic Search rows and AllowedValues rows do not
                        // instantly filter/auto-open their popup on edit-mode entry (TDD §5.13).
                        row.SetEditTextSuppressFilter(startText);
                        row.IsInlineEditing = true;

                        if (row.IsMultiTokenMode)
                        {
                            // Set separator from card config so code-behind can tokenize correctly.
                            string groupId2 = row.FieldKey["SPECIAL:LOGIC:".Length..];
                            var found2 = _capabilityStore?.FindGroup(groupId2);
                            if (found2 != null)
                            {
                                string catId2 = CardEngine.GetPrimaryCatalogId(found2.Value.Group);
                                var catalog2  = catId2 != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId2) : null;
                                var (sep, _, _) = CardEngine.GetMultiTokenAutoCompleteConfig(found2.Value.Group, catalog2);
                                row.MultiTokenSeparator = sep ?? "-";
                            }
                            _activeMultiTokenEditRow = row;
                            row.PropertyChanged += OnActiveMultiTokenRowPropertyChanged;
                        }
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

            ApplyExpertValueCommand      = new RelayCommand(p => { if (p is RowModel r) ApplyExpertValue(r); });

            ToggleFormulaEditCommand     = new RelayCommand(p => { if (p is RowModel r) ToggleFormulaEdit(r); });

            CycleFileNameViewModeCommand = new RelayCommand(_ =>
                ApplyFileNameViewMode(_fileNameViewMode + 1));

            _fileNameViewMode = UiStateStore.LoadFileNameViewMode();

            // Load pinned field keys from Registry
            string pinnedRaw = UiStateStore.LoadFieldSelPinnedFields();
            if (!string.IsNullOrEmpty(pinnedRaw))
                _pinnedFieldKeys.AddRange(pinnedRaw.Split(';')
                    .Select(k => k.Trim()).Where(k => k.Length > 0));

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
            StickyFieldItems = new List<FieldItem>
            {
                new FieldItem("__ADD_ROW__",    LanguageLoader.Get("Action_AddRow"),    "", "", false, true),
                new FieldItem("__REMOVE_ROW__", LanguageLoader.Get("Action_RemoveRow"), "", "", false, true),
                _fieldCatalog.FirstOrDefault(f => f.GroupName == FieldCatalogBuilder.GRP_NONE)
                    ?? new FieldItem("", LanguageLoader.Get("Field_None"), "", FieldCatalogBuilder.GRP_NONE),
            };

            // Favoriten zone — rebuild from pinned keys
            RebuildFavoritenZone();

            // Scrollable groups — build FieldSelectorGroupVm per group (GRP_NONE + GRP_SPECIAL in fixed zones)
            var groupedItems = _fieldCatalog
                .Where(f => f.GroupName != FieldCatalogBuilder.GRP_NONE)
                .GroupBy(f => f.GroupName)
                .OrderBy(g => GroupOrder(g.Key))
                .ToList();

            var groups = new List<FieldSelectorGroupVm>();
            foreach (var grp in groupedItems)
            {
                bool isSpecial = grp.Key == FieldCatalogBuilder.GRP_SPECIAL;
                var items = grp.ToList();

                // Sonderfunktionen: check if any active SPECIAL:LOGIC: entry is present
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

            // Wire ToggleCollapseCommand after list is built so closure captures the right instance
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

            // Apply current filter text immediately
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

        private static int GroupOrder(string g) => g switch
        {
            FieldCatalogBuilder.GRP_SPECIAL  => 0,
            "Grp_iPropertiesCustom"          => 1,
            "Grp_ParamUser"                  => 2,
            "Grp_iProperties"                => 3,
            "Grp_Document"                   => 4,
            "Grp_ParamModel"                 => 5,
            _                                => 6,
        };

        private void RebuildFavoritenZone()
        {
            if (_fieldCatalog == null) { FavoritenItems = System.Array.Empty<PinnedFieldEntry>(); return; }
            var available = new HashSet<string>(_fieldCatalog.Select(f => f.Key), StringComparer.Ordinal);
            var entries = new List<PinnedFieldEntry>();
            foreach (var key in _pinnedFieldKeys)
            {
                var item = _fieldCatalog.FirstOrDefault(f => f.Key == key);
                if (item == null)
                    item = new FieldItem(key, key, key, "");
                entries.Add(new PinnedFieldEntry { Item = item, IsAvailable = available.Contains(key) });
            }
            FavoritenItems = entries;
        }

        private void ApplyFieldSelectorFilter()
        {
            string filter = _fieldSelectorFilterText;
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var gvm in _scrollableGroups)
            {
                if (!hasFilter)
                {
                    gvm.FilteredItems = gvm.AllItems;
                    // Restore saved collapse state (re-check auto-collapse for Sonderfunktionen)
                    if (!gvm.IsChevronEnabled)
                        gvm.IsCollapsed = true;
                }
                else
                {
                    gvm.FilteredItems = gvm.AllItems
                        .Where(f => f.DropText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    // Auto-expand all groups when filter is active
                    if (gvm.IsChevronEnabled) gvm.IsCollapsed = false;
                    else if (gvm.FilteredItems.Count > 0) gvm.IsCollapsed = false;
                }
            }
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

        /// <summary>Called by code-behind when the Field Selector popup closes — resets filter text.</summary>
        public void OnFieldSelectorClosed()
        {
            if (_fieldSelectorFilterText.Length > 0)
                FieldSelectorFilterText = "";
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
                _userInputEvents = _app.CommandManager.UserInputEvents;
                _userInputEvents.OnSelect   += OnSelectionChanged;
                _userInputEvents.OnUnSelect += OnUnSelectionChanged;
            }
            catch { }

            _selectDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _selectDebounceTick = (_, _) => { _selectDebounce.Stop(); DoRefresh(); };
            _selectDebounce.Tick += _selectDebounceTick;

            _selectSetPoller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _selectSetPollerTick = (_, _) =>
            {
                // Mirror _autoRefresh idle-stop: stop polling when Inventor has been idle long enough.
                if (_autoRefresh != null && !_autoRefresh.IsEnabled)
                {
                    _selectSetPoller?.Stop();
                    return;
                }
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
            if (_eventsUnsubscribed) return;
            _eventsUnsubscribed = true;

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

            try
            {
                if (_userInputEvents != null)
                {
                    _userInputEvents.OnSelect   -= OnSelectionChanged;
                    _userInputEvents.OnUnSelect -= OnUnSelectionChanged;
                    _userInputEvents = null;
                }
            }
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
                InvalidateRefreshCache();
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
                            // after document updates and we cannot distinguish a write-triggered
                            // deselect from a genuine empty-click. Sticky is released only when
                            // the user makes a new explicit selection (count > 0) or switches document.
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
                {
                    _docEvents.OnChange       += OnInventorDocumentChanged;
                    _docEvents.OnChangeSelectSet += OnDocumentSelectSetChanged;
                }
            }
            catch { }
        }

        private void UnsubscribeDocEvents()
        {
            try
            {
                if (_docEvents != null)
                {
                    _docEvents.OnChange          -= OnInventorDocumentChanged;
                    _docEvents.OnChangeSelectSet -= OnDocumentSelectSetChanged;
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
                    InvalidateRefreshCache();
                    if (!Rows.Any(r => r.IsInlineEditing))
                        DoRefresh();
                }
                catch { }
            }));
        }

        private void OnDocumentSelectSetChanged(EventTimingEnum before, NameValueMap context,
                                               out HandlingCodeEnum handlingCode)
        {
            handlingCode = HandlingCodeEnum.kEventNotHandled;
            if (before != EventTimingEnum.kAfter) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ResetFallbackTimer();
                    _selectDebounce?.Stop();
                    _selectDebounce?.Start();
                }
                catch { }
            }));
        }

        private void ResetFallbackTimer()
        {
            _noChangeTicks = 0;
            if (_autoRefresh != null && !_autoRefresh.IsEnabled)
                _autoRefresh.Start();
            if (_selectSetPoller != null && !_selectSetPoller.IsEnabled)
                _selectSetPoller.Start();
        }

        private void InvalidateRefreshCache() => _refreshCacheInvalid = true;

        // ══════════════════════════════════════════════
        //  INITIALIZATION
        // ══════════════════════════════════════════════

        private void InitializeDefaultRows()
        {
            Rows.Clear();
            var keys = _presets?.Count > 0 ? _presets[0].FieldKeys : null;
            if (keys != null && keys.Count > 0)
            {
                foreach (var key in keys)
                    Rows.Add(new RowModel { FieldKey = key });
            }
            else
            {
                // No presets available — start with two empty, user-configurable rows.
                Rows.Add(new RowModel());
                Rows.Add(new RowModel());
            }
            EnforceButtonRules();
        }

        // ══════════════════════════════════════════════
        //  FILENAME VIEW MODES
        // ══════════════════════════════════════════════

        // Sets the filename view mode (wrapped to 0..2), persists it, rebuilds the filename
        // string, and notifies the S/C/D highlight + height-cap bindings.
        private void ApplyFileNameViewMode(int mode)
        {
            _fileNameViewMode = ((mode % 3) + 3) % 3;
            UiStateStore.SaveFileNameViewMode(_fileNameViewMode);
            FileName = BuildFileName();
            OnPropertyChanged(nameof(FileNameViewModeLabel));
            OnPropertyChanged(nameof(FileNameMaxHeight));
        }

        private string BuildFileName()
        {
            if (_selectedDocs == null || _selectedDocs.Count == 0) return "";
            return _fileNameViewMode switch
            {
                1 => BuildFileNameCompact(),
                2 => BuildFileNameDetailed(),
                _ => BuildFileNamePlain(),
            };
        }

        private string BuildFileNamePlain()
        {
            return string.Join(", ", _selectedDocs.Select(d =>
            {
                string key = "", name = "";
                try { key = d.FullFileName; name = System.IO.Path.GetFileName(key); }
                catch { name = d.DisplayName; key = name; }
                return _instanceCounts.TryGetValue(key, out int cnt) ? $"{name} ({cnt})" : name;
            }));
        }

        private string BuildFileNameCompact()
        {
            return string.Join(", ", _selectedDocs.Select(d =>
            {
                string key = "", name = "";
                try { key = d.FullFileName; name = System.IO.Path.GetFileName(key); }
                catch { name = d.DisplayName; key = name; }
                if (!_instanceCounts.TryGetValue(key, out int cnt)) return name;
                int iamCount = _subAsmGroups.Values.Count(g => g.ContainsKey(name));
                return iamCount > 1 ? $"{name} ({cnt}, {iamCount} IAM)" : $"{name} ({cnt})";
            }));
        }

        private string BuildFileNameDetailed()
        {
            if (_subAsmGroups == null || _subAsmGroups.Count == 0)
                return BuildFileNamePlain();

            var lines = new System.Collections.Generic.List<string>();
            foreach (var kvSubAsm in _subAsmGroups.OrderBy(x => x.Key))
            {
                string label = string.IsNullOrEmpty(kvSubAsm.Key) ? GetTopLevelName() : kvSubAsm.Key;
                var parts = kvSubAsm.Value
                    .OrderBy(p => p.Key)
                    .Select(p => p.Value > 1 ? $"{p.Key} ({p.Value})" : p.Key);
                lines.Add($"{label} > {string.Join(", ", parts)}");
            }
            return string.Join("\n", lines);
        }

        // Name of the top-level object (the active assembly) — used as the group label for
        // components placed directly in the top assembly instead of a literal "(Top Level)".
        private string GetTopLevelName()
        {
            try
            {
                string path = _app.ActiveDocument.FullFileName;
                string name = System.IO.Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            try
            {
                string dn = _app.ActiveDocument.DisplayName;
                if (!string.IsNullOrEmpty(dn)) return dn;
            }
            catch { }
            return "(Top Level)";
        }

        // ══════════════════════════════════════════════
        //  MAIN REFRESH
        // ══════════════════════════════════════════════

        /// <summary>
        /// Builds a stable, order-independent signature from the given document paths.
        /// Used by the refresh value cache to detect selection changes without COM calls.
        /// </summary>
        internal static string BuildDocSignature(IEnumerable<string> paths)
        {
            if (paths == null) return "";
            var sorted = new List<string>();
            foreach (var p in paths)
                if (p != null) sorted.Add(p);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("|", sorted);
        }

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

        /// <summary>
        /// Invalidates the field catalog cache and triggers a full refresh.
        /// Call after external changes to CapabilityStore (e.g. after Catalog Builder closes).
        /// </summary>
        public void InvalidateFieldCatalog()
        {
            _catalogBuilder.InvalidateCache();
            InvalidateRefreshCache();
            DoRefresh();
        }

        private void DoRefreshCore()
        {
            var _sw = Stopwatch.StartNew();

            RowModel.DisplayValueSetTotal   = 0;
            RowModel.DisplayValueSetChanged = 0;

            _selectedDocs = _docResolver.GetAllSelectedDocuments(out bool isMulti, out bool isAssemblyFallback, out _instanceCounts, out _subAsmGroups);
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

            long _tDocRes = _sw.ElapsedMilliseconds;

            var primaryDoc = _selectedDocs[0];

            FileName = BuildFileName();

            FieldCatalog = _catalogBuilder.GetCatalog(primaryDoc);

            long _tCatalog = _sw.ElapsedMilliseconds;

            // Doc-set signature cache (Kit #2): skip all COM reads when selection is unchanged.
            var _docPaths = new string[_selectedDocs.Count];
            for (int i = 0; i < _selectedDocs.Count; i++)
                try { _docPaths[i] = _selectedDocs[i].FullFileName; } catch { _docPaths[i] = ""; }
            string _docSig   = BuildDocSignature(_docPaths);
            bool   _cacheHit = !_refreshCacheInvalid && _docSig == _refreshCacheSig && _refreshValueCache.Count > 0;
            if (!_cacheHit) { _refreshValueCache.Clear(); _refreshHasFormulaCache.Clear(); _refreshFormulaCache.Clear(); }

            // Batch value reads (Kit #5): open each PropertySet once for all rows on a cache miss.
            Dictionary<string, string>[] _batchValues = null;
            if (!_cacheHit)
            {
                var _batchKeys = Rows
                    .Where(r => !r.IsInlineEditing && !string.IsNullOrEmpty(r.FieldKey))
                    .Select(r => r.FieldKey)
                    .Distinct()
                    .ToArray();
                _batchValues = new Dictionary<string, string>[_selectedDocs.Count];
                for (int i = 0; i < _selectedDocs.Count; i++)
                    _batchValues[i] = _catalogBuilder.BatchReadValues(_selectedDocs[i], _batchKeys);
            }

            foreach (var row in Rows)
            {
                // Don't disrupt an active inline edit
                if (row.IsInlineEditing) continue;

                // fx formula state is recomputed below for value-bearing rows; reset here so
                // SPECIAL / empty / reconfigured rows never retain a stale fx toggle.
                row.HasFormula  = false;
                row.FormulaText = "";

                row.SelectedField = FieldCatalog.FirstOrDefault(f => f.Key == row.FieldKey);
                if (row.SelectedField != null)
                {
                    row.FieldLabel      = row.SelectedField.RowLabel;
                    row.IsWritableField = row.SelectedField.IsWritable;
                    row.AllowedValues   = row.SelectedField.AllowedValues;
                }
                else if (string.IsNullOrEmpty(row.FieldLabel) && !string.IsNullOrEmpty(row.FieldKey))
                {
                    // Key not in catalog (language mismatch or unknown): derive readable label from key shape
                    // so the fallback TextBlock never shows the raw key string.
                    row.FieldLabel = DeriveFieldLabel(row.FieldKey);
                }
                // IsFieldMissing = key is known (in catalog) but not available on the current object.
                // A null SelectedField means the key itself is unresolvable here (language mismatch counts
                // as "missing" until the key is re-normalised); still attempt resolution so the language
                // fallback in ResolveFieldValue can show the value.
                row.IsFieldMissing = row.SelectedField == null && !string.IsNullOrEmpty(row.FieldKey);

                if (string.IsNullOrEmpty(row.FieldKey))
                {
                    row.IsEditable      = false;
                    row.IsWritableField = false;
                    row.DisplayValue    = "";
                    row.ValueForeground = Brushes.Black;
                }
                else
                {
                    row.IsEditable    = false;
                    // When IsFieldMissing the key is not in the current catalog, but still attempt
                    // resolution — the language fallback in ResolveFieldValue may succeed and show
                    // the value; the label shows with strikethrough to signal the mismatch.
                    if (row.IsFieldMissing)
                    {
                        row.IsWritableField = false;
                        row.HasPickerButton  = false;
                    }
                    List<string> vals;
                    if (_cacheHit)
                    {
                        vals = new List<string>(_selectedDocs.Count);
                        for (int i = 0; i < _selectedDocs.Count; i++)
                        {
                            _refreshValueCache.TryGetValue(_docPaths[i] + "||" + row.FieldKey, out string cv);
                            vals.Add(cv ?? "");
                        }
                        _refreshHasFormulaCache.TryGetValue(row.FieldKey, out bool hf);
                        row.HasFormula  = hf;
                        row.FormulaText = hf && _refreshFormulaCache.TryGetValue(row.FieldKey, out string ft) ? ft : "";
                    }
                    else
                    {
                        vals = new List<string>(_selectedDocs.Count);
                        for (int i = 0; i < _selectedDocs.Count; i++)
                        {
                            string v = (_batchValues != null && _batchValues[i] != null
                                        && _batchValues[i].TryGetValue(row.FieldKey, out string bv))
                                       ? bv
                                       : _catalogBuilder.ResolveFieldValue(row.FieldKey, _selectedDocs[i]);
                            _refreshValueCache[_docPaths[i] + "||" + row.FieldKey] = v;
                            vals.Add(v);
                        }
                        // fx: detect a formula behind the value (single-selection only).
                        UpdateFormulaState(row);
                        _refreshHasFormulaCache[row.FieldKey] = row.HasFormula;
                        _refreshFormulaCache[row.FieldKey]    = row.FormulaText;
                    }
                    SetAggregatedValue(row, vals, Brushes.Black);

                    // A formula-driven field provably exists and is writable (we just read its
                    // Expression), so it must never be shown as "missing" (strikethrough label) or
                    // blocked from inline editing — even when its stored key (e.g. the 2-part form
                    // IPROP|Description) doesn't match a 3-part catalog entry.
                    if (row.HasFormula)
                    {
                        row.IsFieldMissing  = false;
                        row.IsWritableField = true;
                    }

                    if (!row.IsFieldMissing)
                    {
                        if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
                        {
                            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
                            var found = _capabilityStore?.FindGroup(groupId);
                            row.HasPickerButton = found != null &&
                                CardEngine.HasCard(found.Value.Group, CardEngine.CardTypeButton);
                        }
                        else
                        {
                            row.HasPickerButton = false;
                        }
                    }
                }
            }

            long _tRows = _sw.ElapsedMilliseconds;

            if (!_cacheHit) { _refreshCacheSig = _docSig; _refreshCacheInvalid = false; }

            // Post-pass: SPECIAL:LOGIC: cycle sentinel — surface as user-visible warning.
            // ResolveFieldValue returns FieldCatalogBuilder.CycleSentinel when a closed TargetFieldKey
            // chain is detected (would otherwise stack-overflow + crash Inventor).
            var cycleGroupNames = new List<string>();
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing) continue;
                if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                if (row.DisplayValue != FieldCatalogBuilder.CycleSentinel) continue;
                row.DisplayValue    = LanguageLoader.Get("Cycle_DisplayLabel");
                row.ValueForeground = Brushes.Red;
                cycleGroupNames.Add(!string.IsNullOrEmpty(row.FieldLabel) ? row.FieldLabel : row.FieldKey);
            }
            if (cycleGroupNames.Count > 0)
            {
                string fmt = LanguageLoader.Get("Msg_LogicCycleDetected");
                StatusMessage = string.Format(fmt, cycleGroupNames.Count, string.Join(", ", cycleGroupNames));
            }

            // Post-pass: PrefixSuffix card — transform display value for Logic rows
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing) continue;
                if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                string psGroupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
                var psFound = _capabilityStore?.FindGroup(psGroupId);
                var psGroup = psFound?.Group;
                if (psGroup == null || !CardEngine.HasPrefixSuffixCard(psGroup)) continue;
                var (psPrefix, psSuffix, psRemove) = CardEngine.GetPrefixSuffixConfig(psGroup);
                row.DisplayValue = CardEngine.ApplyPrefixSuffix(row.DisplayValue, psPrefix, psSuffix, psRemove);
            }

            // Post-pass: V1 Expert Mode — auto-evaluate BL formulas for Expert groups whose
            // formulas contain $[FIELD_KEY] references. Evaluated AFTER Normal groups so live
            // values from Inventor-backed rows are already in row.DisplayValue.
            // Expert→Expert cross-dependencies are resolved via Kahn's topological sort;
            // circular dependencies produce ⚠ Zirkelschluss on the affected rows.
            //
            // Pre-reset: clear pending state on all SPECIAL:LOGIC: rows so non-qualifying
            // rows (disabled BL, no $[...]) don't retain a stale amber / Apply button.
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
                // Collect Expert SPECIAL:LOGIC: groups that have at least one BL card with $[...] refs.
                // Use a dict to guard against duplicate FieldKey rows (same gid twice → ToDictionary crash).
                var expertByGid = new Dictionary<string, (RowModel Row, CardGroup Group)>();
                foreach (var row in Rows)
                {
                    if (row.IsInlineEditing) continue;
                    if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                    string gid = row.FieldKey["SPECIAL:LOGIC:".Length..];
                    if (expertByGid.ContainsKey(gid)) continue; // skip duplicate rows
                    var found = _capabilityStore?.FindGroup(gid);
                    var grp   = found?.Group;
                    if (grp == null || !grp.IsExpert) continue;
                    bool hasExpertBl = false;
                    foreach (var card in grp.Cards)
                    {
                        if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                        if (card.Params.TryGetValue(CardEngine.ParamFormula, out string f) && FormulaEngine.HasExpertRef(f))
                        { hasExpertBl = true; break; }
                    }
                    if (!hasExpertBl) continue;
                    expertByGid[gid] = (row, grp);
                }

                DiagLogger.Log("expertmode", $"Expert post-pass: {expertByGid.Count} qualifying group(s): [{string.Join(", ", expertByGid.Keys)}]");

                if (expertByGid.Count > 0)
                {
                    // Build Expert→Expert dependency graph: A depends on B when A's BL formula has $[SPECIAL:LOGIC:B]
                    var expertGids = new HashSet<string>(expertByGid.Keys);
                    var inDegree   = new Dictionary<string, int>();
                    var outEdges   = new Dictionary<string, List<string>>();
                    foreach (string gid in expertByGid.Keys) { inDegree[gid] = 0; outEdges[gid] = new List<string>(); }

                    foreach (var kvp1308 in expertByGid)
                    {
                        var gid = kvp1308.Key; var (_, grp) = kvp1308.Value;
                        foreach (var card in grp.Cards)
                        {
                            if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                            if (!card.Params.TryGetValue(CardEngine.ParamFormula, out string formula)) continue;
                            foreach (string refKey in FormulaEngine.GetExpertRefs(formula))
                            {
                                if (!refKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)) continue;
                                string refGid = refKey["SPECIAL:LOGIC:".Length..];
                                if (!expertGids.Contains(refGid) || refGid == gid) continue;
                                inDegree[gid]++;
                                outEdges[refGid].Add(gid);
                                DiagLogger.Log("expertmode", $"  Edge: '{refGid}' → '{gid}'");
                            }
                        }
                    }

                    // Kahn's algorithm: topological sort
                    var queue     = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
                    var topoOrder = new List<string>();
                    while (queue.Count > 0)
                    {
                        string cur = queue.Dequeue();
                        topoOrder.Add(cur);
                        foreach (string next in outEdges[cur])
                            if (--inDegree[next] == 0) queue.Enqueue(next);
                    }
                    DiagLogger.Log("expertmode", $"  Topo order: [{string.Join(", ", topoOrder)}]");

                    // Any node still with inDegree > 0 is part of a cycle → ⚠ Zirkelschluss (same text as normal cycle guard)
                    foreach (var kvp1339 in expertByGid)
                    {
                        var gid = kvp1339.Key; var (row, group) = kvp1339.Value;
                        if (inDegree[gid] <= 0) continue;
                        row.DisplayValue        = LanguageLoader.Get("Cycle_DisplayLabel");
                        row.ValueForeground     = Brushes.Red;
                        row.IsExpertPendingApply = false;
                        row.ExpertComputedValue  = null;
                        DiagLogger.Log("expertmode", $"  Zirkelschluss cyclic group '{group.Name}' (id={gid})");
                    }

                    // Evaluate topo-sorted groups; show amber + Apply button when computed ≠ current doc value
                    foreach (string gid in topoOrder)
                    {
                        var (row, group) = expertByGid[gid];
                        string catId = CardEngine.GetPrimaryCatalogId(group);
                        var catalog  = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
                        var ctx      = BuildBasicLogicContext(catalog, "");
                        foreach (var card in group.Cards)
                        {
                            if (!card.Enabled || card.Type != CardEngine.CardTypeBasicLogic) continue;
                            if (!card.Params.TryGetValue(CardEngine.ParamFormula, out string formula) || string.IsNullOrWhiteSpace(formula)) continue;
                            if (!FormulaEngine.HasExpertRef(formula)) continue;
                            card.Params.TryGetValue(CardEngine.ParamFormulaTargetFieldKey, out string targetKey);
                            if (string.IsNullOrEmpty(targetKey)) targetKey = group.TargetFieldKey;
                            if (!string.Equals(targetKey, group.TargetFieldKey, StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                string result = FormulaEngine.Evaluate(formula, ctx);
                                DiagLogger.Log("expertmode", $"  Eval '{group.Name}': formula='{formula}' → '{DiagLogger.S(result)}'");
                                if (!result.StartsWith("#ERROR", StringComparison.Ordinal))
                                {
                                    row.DisplayValue = result;
                                    // Find what the current Inventor document value is for the target field
                                    string currentDocValue = "";
                                    foreach (var r in Rows)
                                    {
                                        if (string.Equals(r.FieldKey, group.TargetFieldKey, StringComparison.OrdinalIgnoreCase))
                                        { currentDocValue = r.DisplayValue; break; }
                                    }
                                    bool isPending = !string.Equals(result, currentDocValue, StringComparison.Ordinal);
                                    row.IsExpertPendingApply = isPending;
                                    row.ExpertComputedValue  = isPending ? result : null;
                                    row.ValueForeground      = isPending ? _expertAmberBrush : Brushes.Black;
                                    DiagLogger.Log("expertmode", $"    pending={isPending} (computed='{DiagLogger.S(result)}', doc='{DiagLogger.S(currentDocValue)}')");
                                }
                            }
                            catch (Exception ex)
                            {
                                DiagLogger.Log("expertmode", $"  Eval EXCEPTION '{group.Name}': {ex.GetType().Name}: {DiagLogger.S(ex.Message)}");
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLogger.Log("expertmode", $"Expert post-pass FATAL: {ex.GetType().Name}: {DiagLogger.S(ex.Message)} | {DiagLogger.S(ex.StackTrace)}");
            }

            // Post-pass: Logic mismatch detection + multi-token segment computation
            foreach (var row in Rows)
            {
                if (row.IsInlineEditing) continue;
                if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
                {
                    string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
                    var found = _capabilityStore?.FindGroup(groupId);
                    var group = found?.Group;
                    if (group != null)
                    {
                        // Multi-token display: MultiPick, PairTransform, or Sort card
                        if (CardEngine.HasMultiPickCard(group) || CardEngine.HasPairTransformCard(group) ||
                            CardEngine.HasSortCard(group))
                        {
                            row.IsMultiTokenMode   = true;
                            row.MultiTokenSegments = ComputeMultiTokenSegments(row.DisplayValue, group);
                            row.MatchedPart   = row.DisplayValue;
                            row.UnmatchedPart = "";
                            continue;
                        }
                        // Standard single-value mismatch (Dropdown / Search card rows)
                        if (CardEngine.HasCard(group, CardEngine.CardTypeDropdown) ||
                            CardEngine.HasCard(group, CardEngine.CardTypeSearch))
                        {
                            row.IsMultiTokenMode = false;
                            var (matched, unmatched) = ComputeLogicMismatch(row.DisplayValue, group);
                            row.MatchedPart   = matched;
                            row.UnmatchedPart = unmatched;
                            continue;
                        }
                    }
                }
                row.IsMultiTokenMode = false;
                row.MatchedPart   = row.DisplayValue;
                row.UnmatchedPart = "";
            }

            // Post-pass: Sync row indicators (capability store driven — rows that participate in Sync cards)
            UpdateSyncIndicators();

            StatusMessage = string.Format(LanguageLoader.Get("Msg_Updated"), FileName, DateTime.Now.ToString("HH:mm:ss"));
            EnforceButtonRules();

            long _tTotal = _sw.ElapsedMilliseconds;
            PerfLogger.LogRefresh(_tTotal, _tDocRes, _tCatalog - _tDocRes, _tRows - _tCatalog, _tTotal - _tRows, Rows.Count, FileName,
                true, RowModel.DisplayValueSetChanged, RowModel.DisplayValueSetTotal, _cacheHit);
        }

        private static string TryRead(Func<string> fn)
        {
            try { return fn(); } catch { return PropertyReader.NotAvailable; }
        }

        /// <summary>
        /// Splits <paramref name="displayValue"/> into per-token <see cref="Models.SpeziSegment"/> objects
        /// using the separator from the first MultiPick or PairTransform card in <paramref name="group"/>.
        /// Each token is marked valid when it exists as a PRI value in the catalog.
        /// </summary>
        private List<Models.SpeziSegment> ComputeMultiTokenSegments(string displayValue, CardGroup group)
        {
            var result = new List<Models.SpeziSegment>();
            if (string.IsNullOrEmpty(displayValue)) return result;

            string catId  = CardEngine.GetPrimaryCatalogId(group);
            var catalog   = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;

            string sep;
            var validSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (catalog != null)
            {
                // GetMultiTokenAutoCompleteConfig returns the correct separator AND the right lookup
                // column for each card type: PRI column for MultiPick, LookupRole column for
                // PairTransform (which may be SEC when the target field holds long-form tokens).
                var (autoSep, lookupColKey, _) = CardEngine.GetMultiTokenAutoCompleteConfig(group, catalog);
                sep = !string.IsNullOrEmpty(autoSep) ? autoSep : "-";
                if (lookupColKey != null)
                    foreach (var entry in catalog.Entries)
                        if (entry.Values.TryGetValue(lookupColKey, out string pv) && !string.IsNullOrEmpty(pv))
                            validSet.Add(pv);
            }
            else
            {
                // No catalog — derive separator from card params; all tokens are treated as valid.
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

        /// <summary>
        /// Splits <paramref name="displayValue"/> into a matched prefix (found in the catalog's PRI column)
        /// and an unmatched tail. Returns ("", "") for empty values. Returns (value, "") when an exact
        /// PRI match is found. Returns (prefix, tail) when a PRI is a prefix of the value. Returns
        /// ("", value) when no catalog PRI is a prefix — the whole value is unrecognised (shown in red).
        /// </summary>
        private (string matched, string unmatched) ComputeLogicMismatch(string displayValue, CardGroup group)
        {
            if (string.IsNullOrEmpty(displayValue)) return ("", "");
            string catId  = CardEngine.GetPrimaryCatalogId(group);
            var catalog   = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
            if (catalog == null) return (displayValue, "");
            string priKey = catalog.Columns.FirstOrDefault(
                c => c.Role == ColumnRole.PrimaryDisplay && c.RoleIndex == 1)?.Key;
            if (priKey == null) return (displayValue, "");

            string bestPrefix = null;
            foreach (var entry in catalog.Entries)
            {
                if (!entry.Values.TryGetValue(priKey, out string pri) || string.IsNullOrEmpty(pri)) continue;
                if (string.Equals(pri, displayValue, StringComparison.OrdinalIgnoreCase))
                    return (displayValue, "");
                if (displayValue.StartsWith(pri, StringComparison.OrdinalIgnoreCase))
                    if (bestPrefix == null || pri.Length > bestPrefix.Length)
                        bestPrefix = pri;
            }
            if (bestPrefix != null)
                return (displayValue[..bestPrefix.Length], displayValue[bestPrefix.Length..]);
            return ("", displayValue);
        }

        // Sets DisplayValue and ValueForeground from a list of per-doc values.
        // If all values are identical the singleColor is used; otherwise Red signals a mismatch.
        private static void SetAggregatedValue(RowModel row, List<string> values, Brush singleColor)
        {
            if (values.Count == 0) { row.DisplayValue = ""; row.ValueForeground = singleColor; return; }
            bool allSame = values.Count == 1 || values.All(v => v == values[0]);
            if (allSame) { row.DisplayValue = values[0]; row.ValueForeground = singleColor; }
            else { row.DisplayValue = string.Join(" | ", values); row.ValueForeground = Brushes.Red; }
        }

        // ══════════════════════════════════════════════
        //  ROW OPERATIONS
        // ══════════════════════════════════════════════

        private void InsertRowBelow(RowModel refRow)
        {
            if (Rows.Count >= MAX_ROWS) { StatusMessage = string.Format(LanguageLoader.Get("Msg_MaxRows"), MAX_ROWS); return; }

            int idx = refRow != null ? Rows.IndexOf(refRow) : Rows.Count - 1;
            if (idx < 0) idx = Rows.Count - 1;

            Rows.Insert(idx + 1, new RowModel());
            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnforceButtonRules();
            DoRefresh();
        }

        private void RemoveRow(RowModel row)
        {
            if (row == null || Rows.Count <= 1) return;

            // Linked row removal — remove all rows sharing the same LinkedGroupId (N-way: covers
            // 1:1 Link-card pairs AND multi-group CapabilitySet blocks).
            if (!string.IsNullOrEmpty(row.LinkedGroupId))
            {
                foreach (var linked in Rows.Where(r => r.LinkedGroupId == row.LinkedGroupId && r != row).ToList())
                    Rows.Remove(linked);
            }
            else if (row.FieldKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
            {
                // Fallback for Logic rows whose LinkedGroupId wasn't set yet (shouldn't normally happen)
                string logicId = row.FieldKey["SPECIAL:LOGIC:".Length..];
                var partner    = Rows.FirstOrDefault(r => r.LinkedGroupId == logicId && r != row);
                if (partner != null) Rows.Remove(partner);
            }

            Rows.Remove(row);
            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
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
            EnforceButtonRules();
            DoRefresh();
        }

        private void EnsureLogicLinkAdjacency()
        {
            if (_capabilityStore == null) return;

            // Track rows that have already been placed as a partner by an earlier group.
            // Prevents mutual-link fighting: when group A links to B and group B links to A,
            // processing B would try to re-order A (which was already placed correctly by A's pass),
            // creating a duplicate row or corrupting LinkedGroupId.
            var alreadyPlacedAsPartner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cs in _capabilityStore.CapabilitySets)
            {
                foreach (var group in cs.Groups)
                {
                    var partnerKeys = CardEngine.GetAllLinkPartnerFieldKeys(group);
                    if (partnerKeys.Count == 0) continue;

                    string logicKey  = $"SPECIAL:LOGIC:{group.Id}";
                    // Skip if this row was already placed as a partner by a prior group's pass.
                    if (alreadyPlacedAsPartner.Contains(logicKey)) continue;

                    RowModel primary = Rows.FirstOrDefault(r => r.FieldKey == logicKey);
                    if (primary == null) continue;

                    primary.LinkedGroupId = group.Id;

                    RowModel prevRow = primary;
                    for (int slot = 0; slot < partnerKeys.Count; slot++)
                    {
                        string partnerKey = partnerKeys[slot];
                        RowModel partner  = Rows.FirstOrDefault(r =>
                            r.FieldKey == partnerKey && r != primary &&
                            (string.IsNullOrEmpty(r.LinkedGroupId) || r.LinkedGroupId == group.Id));

                        if (partner == null)
                        {
                            int prevIdx = Rows.IndexOf(prevRow);
                            var catalog = _fieldCatalog?.FirstOrDefault(f => f.Key == partnerKey);
                            partner = new RowModel
                            {
                                FieldKey        = partnerKey,
                                FieldLabel      = catalog?.RowLabel ?? partnerKey,
                                IsWritableField = catalog?.IsWritable ?? false,
                                AllowedValues   = catalog?.AllowedValues,
                                LinkedGroupId   = group.Id,
                            };
                            Rows.Insert(Math.Min(prevIdx + 1, Rows.Count), partner);
                            return; // chain will be called again after collection change
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

        /// <summary>Marks rows that participate in a Sync card relationship (source Logic row or companion field).</summary>
        private void UpdateSyncIndicators()
        {
            if (_capabilityStore == null)
            {
                foreach (var row in Rows) row.IsConnected = false;
                return;
            }

            var syncSources  = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var syncTargets  = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cs in _capabilityStore.CapabilitySets)
            {
                foreach (var group in cs.Groups)
                {
                    if (!CardEngine.HasCard(group, CardEngine.CardTypeSync)) continue;
                    syncSources.Add($"SPECIAL:LOGIC:{group.Id}");
                    foreach (var card in group.Cards)
                    {
                        if (!card.Enabled || card.Type != CardEngine.CardTypeSync) continue;
                        if (card.Params.TryGetValue(CardEngine.ParamCompanionFieldKey, out string key) && !string.IsNullOrEmpty(key))
                            syncTargets.Add(key);
                    }
                }
            }

            foreach (var row in Rows)
                row.IsConnected = syncSources.Contains(row.FieldKey) || syncTargets.Contains(row.FieldKey);
        }

        // ── Field label helpers ──

        /// <summary>Derives a human-readable label from a raw field key when the key is not in the
        /// current catalog (e.g. language mismatch or object-type switch). Returns the last meaningful
        /// segment so IPROP|Design Tracking Properties|Description → "Description".</summary>
        private string DeriveFieldLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.StartsWith("IPROP|", StringComparison.Ordinal))
            {
                int last = key.LastIndexOf('|');
                return last >= 0 ? key[(last + 1)..] : key["IPROP|".Length..];
            }
            if (key.StartsWith("UDEF:", StringComparison.Ordinal))  return key["UDEF:".Length..];
            if (key.StartsWith("DOC:",  StringComparison.Ordinal))  return key["DOC:".Length..];
            if (key.StartsWith("PARAM:", StringComparison.Ordinal))
            {
                int last = key.LastIndexOf(':');
                return last >= 0 ? key[(last + 1)..] : key["PARAM:".Length..];
            }
            if (key.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
            {
                string groupId = key["SPECIAL:LOGIC:".Length..];
                var found = _capabilityStore?.FindGroup(groupId);
                return found?.Group.Name ?? groupId;
            }
            return key;
        }

        // ── Basic Logic (BL) ──

        /// <summary>Builds a FormulaContext that resolves field refs from live row state and {INPUT} from the typed value.</summary>
        private FormulaContext BuildBasicLogicContext(CatalogData catalog, string inputValue)
        {
            return new FormulaContext
            {
                InputValue        = inputValue ?? "",
                ResolveFieldValue = key =>
                {
                    if (string.IsNullOrEmpty(key)) return "";
                    var r = Rows.FirstOrDefault(row => row.FieldKey == key);
                    return r?.DisplayValue ?? "";
                },
                Lookup = (key, searchCol, returnCol, catName) =>
                {
                    var cat = string.IsNullOrEmpty(catName)
                        ? catalog
                        : _catalogStore?.Catalogs.FirstOrDefault(c =>
                              string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase));
                    return cat == null ? "" : (CardEngine.LookupByColumn(cat, key, searchCol, returnCol) ?? "");
                },
            };
        }

        // ── Multi-column Logic Dropdown popup (U1) ──

        private const double MinColumnWidth     = 40.0;
        private const double ColumnPaddingPx    = 20.0;

        private static readonly System.Windows.Media.Typeface _tfNormal = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        private static readonly System.Windows.Media.Typeface _tfBold = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);

        /// <summary>Measures rendered width of <paramref name="text"/> in DIPs at FontSize 11, Segoe UI.</summary>
        public static double MeasureLogicDropdownText(string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var ft = new System.Windows.Media.FormattedText(
                text, System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, bold ? _tfBold : _tfNormal, 11,
                System.Windows.Media.Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }

        /// <summary>Sets <see cref="RowModel.LogicDropdownFieldWidth"/> to <paramref name="fieldWidth"/> and
        /// proportionally scales columns down if their sum exceeds the popup width (TDD §5.13).</summary>
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
                    col.Width = System.Math.Max(MinColumnWidth, col.Width * scale);
            }
        }

        /// <summary>
        /// Builds the shared <see cref="LogicDropdownColumn"/> list + per-item <see cref="LogicDropdownItemRow"/>
        /// list for <paramref name="row"/>. Restores any saved column widths from the registry.
        /// </summary>
        private void PopulateLogicDropdownColumns(RowModel row, CardGroup group, CatalogData catalog)
        {
            if (row == null) { return; }
            if (catalog == null || row.CatalogDropdownItems == null || row.CatalogDropdownItems.Count == 0)
            {
                row.LogicDropdownColumns = Array.Empty<LogicDropdownColumn>();
                row.LogicDropdownRows    = Array.Empty<LogicDropdownItemRow>();
                return;
            }

            var specs = CardEngine.GetLogicDropdownColumnSpecs(group, catalog);
            if (specs.Count == 0)
            {
                row.LogicDropdownColumns = Array.Empty<LogicDropdownColumn>();
                row.LogicDropdownRows    = Array.Empty<LogicDropdownItemRow>();
                return;
            }

            string ctxKey = catalog.Id ?? "";
            row.LogicDropdownContextKey = ctxKey;

            if (UiStateStore.TryLoadLogicDropdownSize(ctxKey, out double _, out double savedHeight) && savedHeight >= 80)
                row.LogicDropdownPopupHeight = savedHeight;

            UiStateStore.TryLoadLogicDropdownColumnWidths(ctxKey, out double[] savedWidths);
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
                    // TDD §5.13: auto-size column to fit widest content on first open.
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

        /// <summary>Persists the current column widths for the row's context to the registry.</summary>
        public void SaveLogicDropdownColumnWidths(RowModel row)
        {
            if (row?.LogicDropdownColumns == null || row.LogicDropdownColumns.Count == 0) return;
            if (string.IsNullOrEmpty(row.LogicDropdownContextKey)) return;
            var widths = new double[row.LogicDropdownColumns.Count];
            for (int i = 0; i < widths.Length; i++) widths[i] = row.LogicDropdownColumns[i].Width;
            UiStateStore.SaveLogicDropdownColumnWidths(row.LogicDropdownContextKey, widths);
        }

        /// <summary>
        /// Keeps row-level UI constraints consistent after any row add/remove/reorder operation.
        /// Called after every mutation. Rule: CanRemove=false when only 1 row would remain.
        /// </summary>
        internal void EnforceButtonRules()
        {
            int n = Rows.Count;

            for (int i = 0; i < n; i++)
                Rows[i].CanRemove = n > 1;

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
            row.IsEditable      = false;
            row.IsInlineEditing = false;

            row.ValueForeground = Brushes.Black;

            EnsureLogicLinkAdjacency();
            UpdateSyncIndicators();
            EnforceButtonRules();
            DoRefresh();
        }

        // ══════════════════════════════════════════════
        //  FORMULA (fx) STATE + TOGGLE
        // ══════════════════════════════════════════════

        /// <summary>
        /// Refreshes the fx (formula) state for a row: detects whether the value is driven by an
        /// Inventor formula and, if so, stores the equation for the fx editor. Formulas are
        /// per-document, so this resolves only in single-selection; multi-select leaves fx hidden.
        /// </summary>
        private void UpdateFormulaState(RowModel row)
        {
            if (row.IsInlineEditing) return;

            string formula = "";
            if (_selectedDocs.Count == 1)
            {
                try { formula = _catalogBuilder.ResolveFieldFormula(row.FieldKey, _selectedDocs[0]); }
                catch { formula = ""; }
            }

            bool has = !string.IsNullOrEmpty(formula);
            row.FormulaText = formula;
            row.HasFormula  = has;
            if (has) DiagLogger.Log("fx", $"formula on '{DiagLogger.S(row.FieldKey)}': eq='{DiagLogger.S(formula)}' value='{DiagLogger.S(row.DisplayValue)}'");
        }

        /// <summary>
        /// fx toggle: enters formula-edit mode (revealing/editing the equation) or, when already
        /// engaged, returns to the value display. Mirrors Inventor's fx button.
        /// </summary>
        private void ToggleFormulaEdit(RowModel row)
        {
            if (row == null) return;

            if (row.IsFormulaEditing)
            {
                row.IsInlineEditing = false; // setter clears IsFormulaEditing
                return;
            }
            if (!row.HasFormula) return;

            // Close any other row that is already in edit mode.
            foreach (var r in Rows)
                if (r != row && r.IsInlineEditing) r.IsInlineEditing = false;

            // OriginalValue before EditText so HasValueChanged is false until the user edits.
            row.OriginalValue = row.FormulaText;
            row.SetEditTextSuppressFilter(row.FormulaText);
            row.IsFormulaEditing = true;
            row.IsInlineEditing  = true;
            DiagLogger.Log("fx", $"enter formula edit '{DiagLogger.S(row.FieldKey)}': '{DiagLogger.S(row.FormulaText)}'");
        }

        /// <summary>
        /// Writes an fx-edited equation. The equation is sent raw to the terminal real field
        /// (resolving through any SPECIAL:LOGIC alias chain, cycle-guarded), bypassing the logic-card
        /// transforms. If Inventor rejects it (decision B — invalid/cyclic parameter reference), the
        /// row stays in edit with the equation painted red instead of throwing.
        /// </summary>
        private void ApplyFormulaEdit(RowModel row)
        {
            if (!row.HasValueChanged) { row.IsInlineEditing = false; return; }

            string equation = row.EditText?.Trim() ?? "";

            if (_selectedDocs.Count == 0)
            {
                StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite");
                row.IsInlineEditing = false;
                return;
            }

            string targetKey = _catalogBuilder.ResolveTerminalFieldKey(row.FieldKey);
            if (string.IsNullOrEmpty(targetKey))
            {
                // Broken or cyclic logic chain — keep the user in edit, flag it.
                row.IsFormulaInvalid = true;
                StatusMessage = LanguageLoader.Get("Msg_FormulaTargetUnresolved");
                return;
            }

            _stickyDocs = new List<Document>(_selectedDocs);

            bool didWrite = false;
            using (_fieldWriter.BeginBatch())
            {
                var errors = new List<string>();
                foreach (var doc in _selectedDocs)
                {
                    string err = _fieldWriter.WriteFieldValue(doc, targetKey, equation);
                    if (err != null)
                    {
                        string name;
                        try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                        errors.Add($"{name}: {err}");
                    }
                }

                DiagLogger.Log("fx", $"apply formula edit row='{DiagLogger.S(row.FieldKey)}' target='{DiagLogger.S(targetKey)}' eq='{DiagLogger.S(equation)}' errors={errors.Count}");

                if (errors.Count > 0)
                {
                    // Decision B: Inventor rejected the equation → stay in edit, paint it red, show why.
                    row.IsFormulaInvalid = true;
                    StatusMessage = string.Join(" | ", errors);
                    return; // batch flushes any partial successes on scope exit
                }

                row.IsInlineEditing = false; // also clears IsFormulaEditing / IsFormulaInvalid
                EnforceButtonRules();
                didWrite = true;
            } // batch TryUpdate fires here; dependents propagate before DoRefresh re-reads
            if (didWrite) DoRefresh();
        }

        // ══════════════════════════════════════════════
        //  FIELD EDIT (APPLY)
        // ══════════════════════════════════════════════

        private void ApplyFieldEdit(RowModel row)
        {
            if (row == null) return;

            // fx formula write — write the raw equation to the terminal real field, bypassing the
            // logic-card transforms (PrefixSuffix/Sort/BasicLogic) that decorate a normal logic Apply.
            if (row.IsFormulaEditing)
            {
                ApplyFormulaEdit(row);
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
                string groupId  = row.FieldKey["SPECIAL:LOGIC:".Length..];
                var found = _capabilityStore?.FindGroup(groupId);
                logicGroup = found?.Group;
                if (logicGroup == null || string.IsNullOrEmpty(logicGroup.TargetFieldKey))
                {
                    StatusMessage = "Logic Set has no target field configured.";
                    row.IsInlineEditing = false;
                    return;
                }
                writeFieldKey = logicGroup.TargetFieldKey;
                string catId  = CardEngine.GetPrimaryCatalogId(logicGroup);
                logicCatalog  = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
                DiagLogger.Log("pairtransform", $"groupId={groupId} catId={catId ?? "(null)"} logicCatalog={(logicCatalog != null ? logicCatalog.Id : "(null)")} cards=[{string.Join(",", logicGroup.Cards.Select(c => c.Type))}]");

                // PrefixSuffix card: inverse-transform newValue before storing.
                // Add mode: user sees prefix+raw+suffix → strip to get raw.
                // Remove mode: user sees stripped value → add back to get stored form.
                if (CardEngine.HasPrefixSuffixCard(logicGroup))
                {
                    var (psPrefix, psSuffix, psRemove) = CardEngine.GetPrefixSuffixConfig(logicGroup);
                    newValue = CardEngine.ApplyPrefixSuffix(newValue, psPrefix, psSuffix, !psRemove);
                }

                // Sort card: sort the tokens by SRT column before storing.
                if (CardEngine.HasSortCard(logicGroup))
                {
                    var (srtSep, srtLookup, srtInvert) = CardEngine.GetSortConfig(logicGroup);
                    newValue = CardEngine.BuildSortedValue(newValue, logicCatalog, srtLookup, srtSep, srtInvert);
                }
            }

            // Snapshot the current selection before writing; Inventor may clear SelectSet
            // after a document update. DoRefreshCore will use this sticky list if SelectSet
            // is empty after the write completes.
            _stickyDocs = new List<Document>(_selectedDocs);

            // V1 safeguard: if a BasicLogic card writes to writeFieldKey, BL is authoritative —
            // skip the raw user-input write to avoid corrupting numeric parameters with non-numeric text.
            // The BL evaluation below will write the computed value to the same target.
            bool blOwnsWrite = logicGroup != null
                && CardEngine.HasBasicLogicWritingTo(logicGroup, writeFieldKey);

            bool didWrite = false;
            using (_fieldWriter.BeginBatch())
            {
                var errors = new List<(string fileName, string error)>();
                if (!blOwnsWrite)
                {
                    foreach (var doc in _selectedDocs)
                    {
                        string err = _fieldWriter.WriteFieldValue(doc, writeFieldKey, newValue);
                        if (err != null)
                        {
                            string name = "";
                            try { name = System.IO.Path.GetFileName(doc.FullFileName); }
                            catch { name = doc.DisplayName; }
                            errors.Add((name, err));
                        }
                    }
                }

                if (errors.Count > 0)
                {
                    _stickyDocs = null;

                    // Parameter equations are user-editable expressions — an invalid one is expected
                    // input, not an exceptional failure. Mirror the fx path: keep the row in edit, paint
                    // it red, status line only — no modal. (Other field kinds keep the explicit dialog.)
                    if (writeFieldKey.StartsWith("PARAM:", StringComparison.Ordinal))
                    {
                        row.IsFormulaInvalid = true;
                        StatusMessage = string.Join(" | ", errors.Select(e => $"{e.fileName}: {e.error}"));
                        return; // stay in edit; batch flushes any partial successes on scope exit
                    }

                    string details = string.Join("\n", errors.Select(e => $"  {e.fileName}: {e.error}"));
                    MessageBox.Show(
                        $"Write failed for {errors.Count} document(s):\n\n{details}",
                        "Checkup – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
                }
                else
                {
                    // Sync and PairTransform require a primary catalog.
                    if (logicGroup != null && logicCatalog != null)
                    {
                        foreach (var (syncKey, syncVal) in CardEngine.GetSyncWrites(logicGroup, logicCatalog, newValue))
                        {
                            foreach (var doc in _selectedDocs)
                                _fieldWriter.WriteFieldValue(doc, syncKey, syncVal);
                        }

                        if (CardEngine.HasPairTransformCard(logicGroup))
                        {
                            var (srcSep, lookupRole, outputRole, outSep, compField) =
                                CardEngine.GetPairTransformConfig(logicGroup);
                            DiagLogger.Log("pairtransform", $"PT config: srcSep='{srcSep}' lookupRole='{lookupRole}' outputRole='{outputRole}' outSep='{outSep}' compField='{compField}'");
                            DiagLogger.Log("pairtransform", $"newValue='{DiagLogger.S(newValue)}' catalog.Entries={logicCatalog.Entries.Count} cols=[{string.Join(",", logicCatalog.Columns.Select(c => $"{c.Key}:{c.Role}"))}]");
                            if (!string.IsNullOrEmpty(compField))
                            {
                                string transformed = CardEngine.BuildPairTransformValue(
                                    newValue, logicCatalog, srcSep, lookupRole, outputRole, outSep);
                                DiagLogger.Log("pairtransform", $"transformed='{DiagLogger.S(transformed)}' → writing to '{compField}'");
                                foreach (var doc in _selectedDocs)
                                    _fieldWriter.WriteFieldValue(doc, compField, transformed);
                            }
                        }
                    }
                    else if (logicGroup != null)
                    {
                        DiagLogger.Log("bl", $"no primary catalog for group '{logicGroup.Id}' — Sync/PairTransform skipped; BL runs below");
                    }

                    // Basic Logic cards run regardless of whether the group has a primary catalog.
                    // LOOKUP(key, col, col, "CatalogName") (4-arg form) searches CatalogStore by name
                    // and works even when logicCatalog is null.
                    if (logicGroup != null && CardEngine.HasBasicLogicCard(logicGroup))
                    {
                        var ctx = BuildBasicLogicContext(logicCatalog, newValue);
                        DiagLogger.Log("bl", $"group='{logicGroup.Id}' input='{newValue}' catalog={(logicCatalog != null ? logicCatalog.Id : "(none)")}");
                        foreach (var (blKey, blVal) in CardEngine.GetBasicLogicWrites(logicGroup, ctx))
                        {
                            DiagLogger.Log("bl", $"  write {blKey} = '{blVal}'");
                            foreach (var doc in _selectedDocs)
                            {
                                string blErr = _fieldWriter.WriteFieldValue(doc, blKey, blVal);
                                if (blErr != null) DiagLogger.Log("bl", $"  ERROR writing {blKey}: {blErr}");
                            }
                        }
                    }

                    row.IsInlineEditing = false;
                    _catalogBuilder.InvalidateCache();
                    InvalidateRefreshCache();
                    didWrite = true;
                }
            } // batch TryUpdate fires here; dependents propagate before DoRefresh re-reads
            if (didWrite) DoRefresh();
        }

        /// <summary>
        /// Called by <see cref="CheckupWindow"/> after the catalog picker closes with OK.
        /// Writes <paramref name="selectedPriValue"/> to the Logic Set's configured TargetFieldKey,
        /// then runs any enabled Sync cards to write companion fields.
        /// </summary>
        public void ApplyLogicPickerResult(RowModel row, string selectedPriValue)
        {
            if (row == null || string.IsNullOrEmpty(selectedPriValue)) return;
            if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
            var found = _capabilityStore?.FindGroup(groupId);
            var group = found?.Group;
            if (group == null || string.IsNullOrEmpty(group.TargetFieldKey)) return;

            string catId  = CardEngine.GetPrimaryCatalogId(group);
            var catalog   = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;

            if (_selectedDocs.Count == 0)
            {
                StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite");
                return;
            }

            _stickyDocs = new List<Document>(_selectedDocs);

            bool didWrite = false;
            using (_fieldWriter.BeginBatch())
            {
                var errors = new List<(string fileName, string error)>();
                foreach (var doc in _selectedDocs)
                {
                    string err = _fieldWriter.WriteFieldValue(doc, group.TargetFieldKey, selectedPriValue);
                    if (err != null)
                    {
                        string name = "";
                        try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                        errors.Add((name, err));
                    }
                }

                if (errors.Count > 0)
                {
                    _stickyDocs = null;
                    string details = string.Join("\n", errors.Select(e => $"  {e.fileName}: {e.error}"));
                    MessageBox.Show(
                        $"Write failed for {errors.Count} document(s):\n\n{details}",
                        "Checkup – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
                    return; // batch flushes any partial successes on scope exit
                }

                // Sync cards + role-binding writes
                if (catalog != null)
                {
                    foreach (var (syncKey, syncVal) in CardEngine.GetSyncWrites(group, catalog, selectedPriValue))
                    {
                        foreach (var doc in _selectedDocs)
                            _fieldWriter.WriteFieldValue(doc, syncKey, syncVal);
                    }
                }

                // Basic Logic cards on the picker path (treat picked PRI value as {INPUT})
                if (CardEngine.HasBasicLogicCard(group))
                {
                    var ctx = BuildBasicLogicContext(catalog, selectedPriValue);
                    foreach (var (blKey, blVal) in CardEngine.GetBasicLogicWrites(group, ctx))
                    {
                        foreach (var doc in _selectedDocs)
                            _fieldWriter.WriteFieldValue(doc, blKey, blVal);
                    }
                }

                row.IsInlineEditing = false;
                _catalogBuilder.InvalidateCache();
                didWrite = true;
            } // batch TryUpdate fires here; dependents propagate before DoRefresh re-reads
            if (didWrite) DoRefresh();
        }

        /// <summary>
        /// Writes a joined multi-pick selection to the Logic Set's TargetFieldKey,
        /// and optionally writes a companion field built from the mapped companion role.
        /// </summary>
        public void ApplyMultiPickResult(RowModel row, IReadOnlyList<string> selectedPriValues)
        {
            if (row == null || selectedPriValues == null) return;
            if (!row.FieldKey.StartsWith("SPECIAL:LOGIC:")) return;

            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
            var found = _capabilityStore?.FindGroup(groupId);
            var group = found?.Group;
            if (group == null || string.IsNullOrEmpty(group.TargetFieldKey)) return;

            string catId  = CardEngine.GetPrimaryCatalogId(group);
            var catalog   = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;

            var (priSep, companionFieldKey, companionRole, companionSep) = CardEngine.GetMultiPickConfig(group);

            string priValue = string.Join(priSep, selectedPriValues);

            if (_selectedDocs.Count == 0)
            {
                StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite");
                return;
            }

            _stickyDocs = new List<Document>(_selectedDocs);

            bool didWrite = false;
            using (_fieldWriter.BeginBatch())
            {
                var errors = new List<(string fileName, string error)>();
                foreach (var doc in _selectedDocs)
                {
                    string err = _fieldWriter.WriteFieldValue(doc, group.TargetFieldKey, priValue);
                    if (err != null)
                    {
                        string name = "";
                        try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                        errors.Add((name, err));
                    }
                }

                if (errors.Count > 0)
                {
                    _stickyDocs = null;
                    string details = string.Join("\n", errors.Select(e => $"  {e.fileName}: {e.error}"));
                    MessageBox.Show(
                        $"Write failed for {errors.Count} document(s):\n\n{details}",
                        "Checkup – Write Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = string.Format(LanguageLoader.Get("Msg_WriteErrors"), errors.Count);
                    return; // batch flushes any partial successes on scope exit
                }

                // Write companion field (e.g. long-form tokens joined with companionSep)
                if (!string.IsNullOrEmpty(companionFieldKey) && catalog != null)
                {
                    string companionValue = CardEngine.BuildMultiPickCompanionValue(selectedPriValues, catalog, companionRole, companionSep);
                    foreach (var doc in _selectedDocs)
                        _fieldWriter.WriteFieldValue(doc, companionFieldKey, companionValue);
                }

                row.IsInlineEditing = false;
                _catalogBuilder.InvalidateCache();
                didWrite = true;
            } // batch TryUpdate fires here; dependents propagate before DoRefresh re-reads
            if (didWrite) DoRefresh();
        }

        // ── Expert pending-apply write ──

        private void ApplyExpertValue(RowModel row)
        {
            if (row == null || !row.IsExpertPendingApply || row.ExpertComputedValue == null) return;
            if (_selectedDocs.Count == 0) { StatusMessage = LanguageLoader.Get("Msg_NoDocForWrite"); return; }

            string gid = row.FieldKey["SPECIAL:LOGIC:".Length..];
            var found = _capabilityStore?.FindGroup(gid);
            if (found == null) return;
            string targetKey = found.Value.Group.TargetFieldKey;

            _stickyDocs = new List<Document>(_selectedDocs);
            bool didWrite = false;
            using (_fieldWriter.BeginBatch())
            {
                var errors = new List<(string fileName, string error)>();
                foreach (var doc in _selectedDocs)
                {
                    string err = _fieldWriter.WriteFieldValue(doc, targetKey, row.ExpertComputedValue);
                    if (err != null)
                    {
                        string name = "";
                        try { name = System.IO.Path.GetFileName(doc.FullFileName); } catch { name = doc.DisplayName; }
                        errors.Add((name, err));
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
                    InvalidateRefreshCache();
                    didWrite = true;
                }
            } // batch TryUpdate fires here; dependents propagate before DoRefresh re-reads
            if (didWrite) DoRefresh();
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

            var newKeys = Rows.Select(r => r.FieldKey).ToList();
            bool nameChanged = !string.Equals(name, DemoPresetName, StringComparison.Ordinal);
            bool keysChanged = !newKeys.SequenceEqual(_demoDefaultFieldKeys);
            if (nameChanged && keysChanged)
                _presets[index].IsDemo = false;

            _presets[index].Name      = name;
            _presets[index].FieldKeys = newKeys;
            _presetsManager.Save(_presets);

            switch (index)
            {
                case 0: Preset1Name = name; break;
                case 1: Preset2Name = name; break;
                case 2: Preset3Name = name; break;
            }

            StatusMessage = string.Format(LanguageLoader.Get("Msg_PresetSaved"), name, DateTime.Now.ToString("HH:mm:ss"));
        }

        // ── Demo mode warning ─────────────────────────────────────────────────

        private bool IsDemoActive()
        {
            if (_presets == null || _presets.Count != 3) return false;
            // Primary: flag set by PresetsManager (new installations / after Reset).
            if (_presets.All(p => p.IsDemo)) return true;
            // Fallback: detect by content for Registry presets that predate the IsDemo field.
            return _presets.All(p =>
                string.Equals(p.Name, DemoPresetName, StringComparison.Ordinal) &&
                p.FieldKeys.SequenceEqual(_demoDefaultFieldKeys));
        }

        private bool ShouldShowDemoWarning()
        {
            if (!IsDemoActive()) return false;
            _demoWindowOpenCount++;
            if (!_demoShownThisSession) { _demoShownThisSession = true; return true; }
            return (_demoWindowOpenCount % 20) == 0;
        }

        public void CheckAndShowDemoWarning(Window owner)
        {
            if (!ShouldShowDemoWarning()) return;
            var dlg = new Views.InfoDialog(
                LanguageLoader.Get("Dlg_DemoWarning_Body"),
                "DemoWarning",
                "Dlg_DemoWarning_Title",
                440, 300);
            dlg.Owner = owner;
            dlg.ShowDialog();
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
            UiStateStore.ClearFieldSelUserPrefs();
            _presetsManager?.ResetToDefaults();
            _presets = _presetsManager?.GetDefaults() ?? new List<PresetData>();

            // Reset returns to factory demo state, so re-arm the demo-mode warning:
            // clear the session flags so the dialog shows again on the next window open
            // within this same Inventor session (TDD §5.3). Without this the static
            // "shown this session" flag would suppress it until the next 20th open.
            _demoShownThisSession = false;
            _demoWindowOpenCount  = 0;

            if (_presets.Count == 3)
            {
                Preset1Name = _presets[0].Name;
                Preset2Name = _presets[1].Name;
                Preset3Name = _presets[2].Name;
            }

            UiStateStore.ClearWindowSizes();
            RequestResetWindowSize?.Invoke();

            SetActivePreset(0);
            ApplyFileNameViewMode(0);   // back to Standard (S) view, persisted to HKCU
            _catalogBuilder?.InvalidateCache();
            InvalidateRefreshCache();
            InitializeDefaultRows();
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
        //  MULTI-TOKEN PER-TOKEN AUTOCOMPLETE
        // ══════════════════════════════════════════════

        /// <summary>
        /// Updates the autocomplete popup for a multi-token Logic row being edited.
        /// Tokenizes <paramref name="editText"/> by <see cref="RowModel.MultiTokenSeparator"/>,
        /// determines which token the caret sits in, and filters the catalog for prefix matches.
        /// </summary>
        public void UpdateMultiTokenAutoComplete(RowModel row, string editText, int caretIndex)
        {
            if (row == null || !row.IsMultiTokenMode) return;

            string groupId = row.FieldKey["SPECIAL:LOGIC:".Length..];
            var found = _capabilityStore?.FindGroup(groupId);
            if (found == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            string catId   = CardEngine.GetPrimaryCatalogId(found.Value.Group);
            var catalog    = catId != null ? _catalogStore?.Catalogs.FirstOrDefault(c => c.Id == catId) : null;
            if (catalog == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            var (_, lookupColKey, secColKey) = CardEngine.GetMultiTokenAutoCompleteConfig(found.Value.Group, catalog);
            if (lookupColKey == null) { row.IsMultiTokenAutoCompleteOpen = false; return; }

            string sep     = row.MultiTokenSeparator;
            string text    = editText ?? "";
            string partial = GetTokenAtCaret(text, sep, caretIndex);

            var items = new List<Models.SpeziAutoCompleteItem>();
            foreach (var entry in catalog.Entries)
            {
                if (!entry.Values.TryGetValue(lookupColKey, out string val) || string.IsNullOrEmpty(val)) continue;
                if (!val.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                entry.Values.TryGetValue(secColKey ?? "", out string sec);
                items.Add(new Models.SpeziAutoCompleteItem(val, sec ?? ""));
                if (items.Count >= 80) break;
            }

            row.AutoCompleteItems             = items;
            row.IsMultiTokenAutoCompleteOpen  = items.Count > 0;
        }

        /// <summary>
        /// Replaces the token at <paramref name="caretIndex"/> in <see cref="RowModel.EditText"/>
        /// with <paramref name="value"/>. Returns the new caret position (end of the inserted token).
        /// </summary>
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

            // Compute start position of the active token for caret calculation.
            int tokenStart = 0;
            for (int i = 0; i < activeIdx; i++) tokenStart += parts[i].Length + sep.Length;

            parts[activeIdx]                 = value;
            row.EditText                     = string.Join(sep, parts);
            row.IsMultiTokenAutoCompleteOpen = false;
            return tokenStart + value.Length;
        }

        private void OnActiveMultiTokenRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is not RowModel row) return;
            if (e.PropertyName == nameof(RowModel.IsInlineEditing) && !row.IsInlineEditing)
            {
                row.PropertyChanged              -= OnActiveMultiTokenRowPropertyChanged;
                row.IsMultiTokenAutoCompleteOpen  = false;
                if (_activeMultiTokenEditRow == row)
                    _activeMultiTokenEditRow = null;
            }
        }

        /// <summary>Extracts the partial token at <paramref name="caretIndex"/> in <paramref name="text"/>.</summary>
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

        // ══════════════════════════════════════════════
        //  INotifyPropertyChanged
        // ══════════════════════════════════════════════

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
