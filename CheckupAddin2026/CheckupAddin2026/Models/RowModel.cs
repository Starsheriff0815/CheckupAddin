using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// UI state for a single configurable row in the Checkup window.
    /// One instance per visible row; bound directly to the ItemsControl DataTemplate.
    /// </summary>
    /// <remarks>
    /// Edit-mode state matrix:
    ///
    ///   IsEditable  IsInlineEditing  → IsDisplayMode  IsEditMode  IsFieldSelectorVisible
    ///   false       false              true            false       true
    ///   false       true               false           true        false
    ///   true        any                false           true        false
    ///
    /// IsEditable is never set to true anymore (legacy path removed).
    /// IsInlineEditing is toggled by StartInlineEditCommand / CancelFieldEditCommand / ApplyFieldEditCommand.
    /// </remarks>
    public class RowModel : INotifyPropertyChanged
    {
        private string _fieldKey = "";
        private string _fieldLabel = "";
        private string _displayValue = "";
        private System.Windows.Media.Brush _valueForeground = System.Windows.Media.Brushes.Black;
        private FieldItem _selectedField;
        private bool _isEditable;
        private bool _isInlineEditing;
        private bool _isWritableField;
        private string _editText = "";
        private bool _canRemove = true;
        private bool _isDragOver;
        private IReadOnlyList<string>              _allowedValues        = Array.Empty<string>();
        private string                             _highlightedAllowedValue;
        private IReadOnlyList<CatalogDropdownItem> _catalogDropdownItems = Array.Empty<CatalogDropdownItem>();
        private ListCollectionView _catalogDropdownView;
        private string _originalValue = "";
        private string _matchedPart   = "";
        private string _unmatchedPart = "";
        private bool _isLogicSearchMode;
        private bool   _isAllowedValuesPopupOpen;
        private string _allowedValuesFilterText;    // null = show full list; set only when user actually types
        private bool _suppressFilterOnLoad;         // true during SetEditTextSuppressFilter — prevents auto-filter on edit entry
        private bool _isExpertPendingApply;
        private string _expertComputedValue;
        private bool _hasFormula;
        private string _formulaText = "";
        private bool _isFormulaEditing;
        private bool _isFormulaInvalid;

        /// <summary>Field key — determines which Inventor value this row reads/writes (see FieldItem key conventions).</summary>
        public string FieldKey
        {
            get => _fieldKey;
            set
            {
                _fieldKey = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSpecialRow));
                OnPropertyChanged(nameof(IsPlainTextEditMode));
                OnPropertyChanged(nameof(IsNormalDisplayMode));
                OnPropertyChanged(nameof(IsValueMismatchDisplayMode));
                OnPropertyChanged(nameof(IsFieldSelectorVisible));
                OnPropertyChanged(nameof(IsComboEditMode));
                if (_isExpertPendingApply)
                {
                    _isExpertPendingApply = false;
                    OnPropertyChanged(nameof(IsExpertPendingApply));
                }
            }
        }

        /// <summary>Label shown in the left column (set from FieldItem.RowLabel on selection change).</summary>
        public string FieldLabel
        {
            get => _fieldLabel;
            set { _fieldLabel = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>Current value read from Inventor, refreshed on every DoRefresh call.</summary>
        public string DisplayValue
        {
            get => _displayValue;
            set { _displayValue = value ?? ""; OnPropertyChanged(); }
        }

        public System.Windows.Media.Brush ValueForeground
        {
            get => _valueForeground;
            set { _valueForeground = value; OnPropertyChanged(); }
        }

        /// <summary>Currently selected item in the field-selector ComboBox (Col 2).</summary>
        public FieldItem SelectedField
        {
            get => _selectedField;
            set { _selectedField = value; OnPropertyChanged(); }
        }

        /// <summary>Reserved for future use; currently always false at runtime.</summary>
        public bool IsEditable
        {
            get => _isEditable;
            set { _isEditable = value; OnPropertyChanged(); NotifyEditModeChanged(); }
        }

        /// <summary>True while the user is actively editing this row's value (click-to-edit).</summary>
        public bool IsInlineEditing
        {
            get => _isInlineEditing;
            set { _isInlineEditing = value; OnPropertyChanged(); NotifyEditModeChanged(); if (!value) { IsSyncProposal = false; _isAllowedValuesPopupOpen = false; _isFormulaEditing = false; _isFormulaInvalid = false; OnPropertyChanged(nameof(IsFormulaEditing)); OnPropertyChanged(nameof(IsFormulaEditMode)); OnPropertyChanged(nameof(IsFormulaInvalid)); } OnPropertyChanged(nameof(ShowFormulaToggle)); }
        }

        /// <summary>Set by the sync logic when this edit was proposed automatically after the partner row was applied.
        /// Suppresses the back-sync cascade: accepting a proposal does not re-propose the partner.</summary>
        public bool IsSyncProposal { get; set; }

        /// <summary>True if the field bound to this row supports write-back to Inventor (controls cursor hint and click-to-edit).</summary>
        public bool IsWritableField
        {
            get => _isWritableField;
            set { _isWritableField = value; OnPropertyChanged(); }
        }

        // ── Computed visibility states ──

        /// <summary>Normal display: value TextBlock visible.</summary>
        public bool IsDisplayMode => !_isEditable && !_isInlineEditing;

        /// <summary>Any edit state active.</summary>
        public bool IsEditMode => _isEditable || _isInlineEditing;

        /// <summary>True when the field-selector ComboBox should be shown.</summary>
        public bool IsFieldSelectorVisible => !_isEditable && !_isInlineEditing;

        /// <summary>Edit mode for free-text fields (no AllowedValues) — shows plain TextBox.</summary>
        public bool IsTextEditMode => IsEditMode && !HasAllowedValues;

        /// <summary>Edit mode for list fields (has AllowedValues), excluding Logic-dropdown rows.</summary>
        public bool IsComboEditMode => IsEditMode && HasAllowedValues && !HasCatalogDropdownItems && !_isFormulaEditing;

        /// <summary>Edit mode for plain free-text fields (no AllowedValues, non-Logic-dropdown).</summary>
        public bool IsPlainTextEditMode => IsEditMode && !HasAllowedValues && !HasCatalogDropdownItems && !_isFormulaEditing;

        /// <summary>Edit mode for Logic rows — shows the unified Dropdown panel (arrow button always visible).</summary>
        public bool IsLogicComboEditMode => IsEditMode && HasCatalogDropdownItems && !_isFormulaEditing;

        /// <summary>Always false — Search card no longer replaces the Dropdown panel. Search behavior is embedded in the unified panel.</summary>
        public bool IsLogicSearchEditMode => false;

        // ── Edit value state ──

        /// <summary>Pre-populated from DisplayValue when entering edit mode; submitted to FieldWriter on Apply.</summary>
        public string EditText
        {
            get => _editText;
            set
            {
                _editText = value ?? "";
                // Editing the equation clears a prior "invalid" red state (user is fixing it).
                if (_isFormulaInvalid) { _isFormulaInvalid = false; OnPropertyChanged(nameof(IsFormulaInvalid)); }
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasValueChanged));
                OnPropertyChanged(nameof(IsEditValueValid));
                OnPropertyChanged(nameof(ShowValidationError));
                // Search card: apply live filter whenever the text changes (guarded during edit-mode entry)
                if (_isLogicSearchMode && !_suppressFilterOnLoad) ApplySearchFilter(_editText);
                // AllowedValues panel: if popup is open and user is actively filtering, refresh the list
                if (HasAllowedValues && _isAllowedValuesPopupOpen && _allowedValuesFilterText != null)
                    OnPropertyChanged(nameof(FilteredAllowedValues));
            }
        }

        /// <summary>Snapshot of DisplayValue captured when inline editing starts.</summary>
        public string OriginalValue
        {
            get => _originalValue;
            set
            {
                _originalValue = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasValueChanged));
                OnPropertyChanged(nameof(ShowValidationError));
            }
        }

        /// <summary>Apply/Cancel visible only when the user has actually changed the value.</summary>
        public bool HasValueChanged => _isInlineEditing && _editText != _originalValue;

        /// <summary>False only for AllowedValues fields when EditText is not in the list.</summary>
        public bool IsEditValueValid
        {
            get
            {
                if (!HasAllowedValues) return true;
                foreach (string v in _allowedValues)
                    if (string.Equals(v, _editText, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        /// <summary>Value changed AND not valid — drives the red-border DataTrigger on the value ComboBox.</summary>
        public bool ShowValidationError => HasValueChanged && !IsEditValueValid;

        // ── Allowed values ──

        /// <summary>Fixed set of valid values; empty for free-text fields.</summary>
        public IReadOnlyList<string> AllowedValues
        {
            get => _allowedValues;
            set
            {
                _allowedValues = value ?? Array.Empty<string>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAllowedValues));
                OnPropertyChanged(nameof(IsEditValueValid));
                OnPropertyChanged(nameof(ShowValidationError));
                OnPropertyChanged(nameof(IsTextEditMode));
                OnPropertyChanged(nameof(IsPlainTextEditMode));
                OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
                OnPropertyChanged(nameof(IsComboEditMode));
            }
        }

        /// <summary>True when this field has a fixed set of valid values.</summary>
        public bool HasAllowedValues => _allowedValues != null && _allowedValues.Count > 0;

        /// <summary>Filtered subset of <see cref="AllowedValues"/> matching the user's typed filter text.
        /// Returns the full list when no filter has been entered (popup just opened or not yet typed into).</summary>
        public IReadOnlyList<string> FilteredAllowedValues
        {
            get
            {
                if (!HasAllowedValues) return Array.Empty<string>();
                if (string.IsNullOrEmpty(_allowedValuesFilterText)) return _allowedValues;
                var result = new List<string>();
                foreach (var v in _allowedValues)
                    if (v.IndexOf(_allowedValuesFilterText, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(v);
                return result;
            }
        }

        /// <summary>Called by CheckupWindow to set or clear the AllowedValues filter text.
        /// Pass null or empty to show the full list.</summary>
        public void SetAllowedValuesFilter(string filter)
        {
            _allowedValuesFilterText = string.IsNullOrEmpty(filter) ? null : filter;
            _highlightedAllowedValue = null;
            OnPropertyChanged(nameof(FilteredAllowedValues));
            OnPropertyChanged(nameof(HighlightedAllowedValue));
        }

        /// <summary>Sets EditText without triggering ApplySearchFilter or AllowedValues auto-filter.
        /// Use during inline-edit initialization so the popup does not auto-open on edit entry.</summary>
        public void SetEditTextSuppressFilter(string text)
        {
            _suppressFilterOnLoad = true;
            EditText = text;
            _suppressFilterOnLoad = false;
        }

        /// <summary>True while the AllowedValues search popup is open.</summary>
        public bool IsAllowedValuesPopupOpen
        {
            get => _isAllowedValuesPopupOpen;
            set
            {
                if (_isAllowedValuesPopupOpen == value) return;
                _isAllowedValuesPopupOpen = value;
                if (!value) { _allowedValuesFilterText = null; _highlightedAllowedValue = null; OnPropertyChanged(nameof(FilteredAllowedValues)); OnPropertyChanged(nameof(HighlightedAllowedValue)); }
                OnPropertyChanged();
            }
        }

        /// <summary>The AllowedValues list item currently highlighted by arrow-key navigation. Null when no item is keyboard-selected.</summary>
        public string HighlightedAllowedValue
        {
            get => _highlightedAllowedValue;
            set
            {
                if (_highlightedAllowedValue == value) return;
                _highlightedAllowedValue = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Moves the keyboard highlight up (-1) or down (+1) through the visible AllowedValues popup items.</summary>
        public void MoveAllowedValuesHighlight(int delta)
        {
            var items = FilteredAllowedValues;
            if (items == null || items.Count == 0) return;
            int cur = -1;
            if (_highlightedAllowedValue != null)
                for (int i = 0; i < items.Count; i++)
                    if (items[i] == _highlightedAllowedValue) { cur = i; break; }
            int next = cur < 0
                ? (delta > 0 ? 0 : items.Count - 1)
                : Math.Max(0, Math.Min(items.Count - 1, cur + delta));
            HighlightedAllowedValue = items[next];
        }

        // ── Catalog Logic dropdown items (Dropdown card) ──

        /// <summary>Catalog entries (PRI/SEC/AUX/GroupName) for Logic rows with a Dropdown card.
        /// Set before entering edit mode; cleared by setting to null.
        /// Also rebuilds <see cref="CatalogDropdownView"/>.</summary>
        public IReadOnlyList<CatalogDropdownItem> CatalogDropdownItems
        {
            get => _catalogDropdownItems;
            set
            {
                _catalogDropdownItems = value ?? Array.Empty<CatalogDropdownItem>();

                // Rebuild grouped view; add GroupDescription only when at least one item has a group name.
                var list = _catalogDropdownItems as System.Collections.IList
                           ?? new List<CatalogDropdownItem>(_catalogDropdownItems);
                var view = new ListCollectionView(list);
                if (_catalogDropdownItems.Any(i => !string.IsNullOrEmpty(i.GroupName)))
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogDropdownItem.GroupName)));
                _catalogDropdownView = view;

                OnPropertyChanged();
                OnPropertyChanged(nameof(CatalogDropdownView));
                OnPropertyChanged(nameof(HasCatalogDropdownItems));
                OnPropertyChanged(nameof(IsLogicComboEditMode));
                OnPropertyChanged(nameof(IsComboEditMode));
                OnPropertyChanged(nameof(IsPlainTextEditMode));
                OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
            }
        }

        /// <summary>Grouped/sorted view over <see cref="CatalogDropdownItems"/> for the Logic ComboBox.
        /// Groups are added only when the catalog has a GRP column.</summary>
        public ListCollectionView CatalogDropdownView => _catalogDropdownView;

        /// <summary>True when this row has catalog dropdown items (Logic + Dropdown card).</summary>
        public bool HasCatalogDropdownItems => _catalogDropdownItems != null && _catalogDropdownItems.Count > 0;

        // ── Multi-column Logic dropdown popup (U1) ──

        private IReadOnlyList<LogicDropdownColumn> _logicDropdownColumns = Array.Empty<LogicDropdownColumn>();
        private IReadOnlyList<LogicDropdownItemRow> _logicDropdownRows  = Array.Empty<LogicDropdownItemRow>();
        private ListCollectionView _logicDropdownRowsView;
        private bool _isLogicPopupOpen;
        private string _logicDropdownContextKey;
        private double _logicDropdownFieldWidth = 280;

        /// <summary>Column specs for the multi-column popup (shared between header + every item row).</summary>
        public IReadOnlyList<LogicDropdownColumn> LogicDropdownColumns
        {
            get => _logicDropdownColumns;
            set { _logicDropdownColumns = value ?? Array.Empty<LogicDropdownColumn>(); OnPropertyChanged(); }
        }

        /// <summary>Per-item rows (wraps CatalogDropdownItem + per-column cells using the shared columns).</summary>
        public IReadOnlyList<LogicDropdownItemRow> LogicDropdownRows
        {
            get => _logicDropdownRows;
            set
            {
                _logicDropdownRows = value ?? Array.Empty<LogicDropdownItemRow>();
                var list = _logicDropdownRows as System.Collections.IList
                           ?? new List<LogicDropdownItemRow>(_logicDropdownRows);
                var view = new ListCollectionView(list);
                if (_logicDropdownRows.Any(r => !string.IsNullOrEmpty(r.GroupName)))
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LogicDropdownItemRow.GroupName)));
                _logicDropdownRowsView = view;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LogicDropdownRowsView));
            }
        }

        public ListCollectionView LogicDropdownRowsView => _logicDropdownRowsView;

        /// <summary>TwoWay-bound to <c>Popup.IsOpen</c> for the unified Logic dropdown popup.</summary>
        public bool IsLogicComboPopupOpen
        {
            get => _isLogicPopupOpen;
            set { IsLogicPopupOpen = value; }
        }

        /// <summary>Always false — unified panel uses <see cref="IsLogicPopupOpen"/> directly.</summary>
        public bool IsLogicSearchPopupOpen
        {
            get => false;
            set { }
        }

        /// <summary>Master open flag; drives both <see cref="IsLogicComboPopupOpen"/> and <see cref="IsLogicSearchPopupOpen"/>.</summary>
        public bool IsLogicPopupOpen
        {
            get => _isLogicPopupOpen;
            set
            {
                if (_isLogicPopupOpen == value) return;
                _isLogicPopupOpen = value;
                if (!value) HighlightedLogicItem = null; // clear keyboard highlight when popup closes
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLogicComboPopupOpen));
                OnPropertyChanged(nameof(IsLogicSearchPopupOpen));
            }
        }

        private LogicDropdownItemRow _highlightedLogicItem;
        /// <summary>The popup item currently highlighted by arrow-key navigation. Null when no item is selected.</summary>
        public LogicDropdownItemRow HighlightedLogicItem
        {
            get => _highlightedLogicItem;
            set
            {
                if (_highlightedLogicItem == value) return;
                if (_highlightedLogicItem != null) _highlightedLogicItem.IsHighlighted = false;
                _highlightedLogicItem = value;
                if (_highlightedLogicItem != null) _highlightedLogicItem.IsHighlighted = true;
            }
        }

        /// <summary>Moves the keyboard highlight up (-1) or down (+1) through the visible popup items.</summary>
        public void MoveLogicHighlight(int delta)
        {
            if (_logicDropdownRowsView == null || _logicDropdownRowsView.IsEmpty) return;
            var items = _logicDropdownRowsView.OfType<LogicDropdownItemRow>().ToList();
            if (items.Count == 0) return;
            int cur = _highlightedLogicItem != null ? items.IndexOf(_highlightedLogicItem) : -1;
            int next = cur < 0
                ? (delta > 0 ? 0 : items.Count - 1)
                : Math.Max(0, Math.Min(items.Count - 1, cur + delta));
            HighlightedLogicItem = items[next];
        }

        /// <summary>Registry context key (e.g. catalog ID) for persisting column widths + popup size.</summary>
        public string LogicDropdownContextKey
        {
            get => _logicDropdownContextKey;
            set { _logicDropdownContextKey = value; OnPropertyChanged(); }
        }

        private double _logicDropdownPopupHeight = 320;
        /// <summary>Popup scrollviewer MaxHeight (TDD §5.13 user-resizable).</summary>
        public double LogicDropdownPopupHeight
        {
            get => _logicDropdownPopupHeight;
            set
            {
                double clamped = value < 80 ? 80 : value;
                if (System.Math.Abs(_logicDropdownPopupHeight - clamped) < 0.5) return;
                _logicDropdownPopupHeight = clamped;
                OnPropertyChanged();
            }
        }

        /// <summary>Popup border Width — set to field column width at open time (TDD §5.13).</summary>
        public double LogicDropdownFieldWidth
        {
            get => _logicDropdownFieldWidth;
            set
            {
                double clamped = value < 200 ? 200 : value;
                if (System.Math.Abs(_logicDropdownFieldWidth - clamped) < 0.5) return;
                _logicDropdownFieldWidth = clamped;
                OnPropertyChanged();
            }
        }

        /// <summary>False when only one row remains (enforced by EnforceButtonRules).</summary>
        public bool CanRemove
        {
            get => _canRemove;
            set { _canRemove = value; OnPropertyChanged(); }
        }

        /// <summary>True while another row is being dragged over this row — triggers the blue top-border DataTrigger.</summary>
        public bool IsDragOver
        {
            get => _isDragOver;
            set { _isDragOver = value; OnPropertyChanged(); }
        }

        /// <summary>True while this row is being dragged — triggers 50% opacity DataTrigger.</summary>
        private bool _isDragging;
        public bool IsDragging
        {
            get => _isDragging;
            set { _isDragging = value; OnPropertyChanged(); }
        }

        private bool _hasPickerButton;
        /// <summary>True when this Logic row has an enabled Button card — shows the catalog picker button in display mode.</summary>
        public bool HasPickerButton
        {
            get => _hasPickerButton;
            set { _hasPickerButton = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLogicPickerDisplayMode)); }
        }

        /// <summary>True when the catalog picker button should be shown: display mode + Button card enabled on this row.</summary>
        public bool IsLogicPickerDisplayMode => IsDisplayMode && _hasPickerButton;

        /// <summary>Shared identifier for rows locked together by a Link card (= CapabilitySet.Id).
        /// Empty string when the row is not part of a linked pair.</summary>
        private string _linkedGroupId = "";
        public string LinkedGroupId
        {
            get => _linkedGroupId;
            set { _linkedGroupId = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(IsLinked)); OnPropertyChanged(nameof(IsLinkedAndConnected)); }
        }

        /// <summary>True when this row is part of a linked pair (Link card defined in its Logic Set).</summary>
        public bool IsLinked => !string.IsNullOrEmpty(_linkedGroupId);

        private bool _isConnected;
        /// <summary>True when this row participates in a Sync card relationship (source Logic row or companion field).</summary>
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLinkedAndConnected));
            }
        }

        /// <summary>True when the row is both part of a linked pair AND a sync relationship — shows dual stripe.</summary>
        public bool IsLinkedAndConnected => IsLinked && IsConnected;

        /// <summary>True when the row has a FieldKey but the key is not found in the current document's catalog.</summary>
        private bool _isFieldMissing;
        public bool IsFieldMissing
        {
            get => _isFieldMissing;
            set { _isFieldMissing = value; OnPropertyChanged(); }
        }

        /// <summary>True for any SPECIAL: row — shows "S:" prefix in the field-selector ComboBox header.</summary>
        public bool IsSpecialRow => _fieldKey.StartsWith("SPECIAL:", StringComparison.Ordinal);

        // ── Multi-token display mode (Logic rows with MultiPick / PairTransform card) ──

        private bool _isMultiTokenMode;
        /// <summary>True for Logic rows whose group has a MultiPick or PairTransform card.
        /// Enables per-token colored display in the main window.</summary>
        public bool IsMultiTokenMode
        {
            get => _isMultiTokenMode;
            set
            {
                if (_isMultiTokenMode == value) return;
                _isMultiTokenMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNormalDisplayMode));
                OnPropertyChanged(nameof(IsMultiTokenDisplayMode));
                OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
            }
        }

        private List<SpeziSegment> _multiTokenSegments = new();
        /// <summary>Parsed tokens with per-token catalog validity — computed by CheckupViewModel on each refresh
        /// for Logic rows in multi-token mode.</summary>
        public List<SpeziSegment> MultiTokenSegments
        {
            get => _multiTokenSegments;
            set
            {
                _multiTokenSegments = value ?? new();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMultiTokenMismatch));
            }
        }

        /// <summary>True when at least one token in MultiTokenSegments is not in the catalog.</summary>
        public bool IsMultiTokenMismatch => _multiTokenSegments.Any(s => !s.IsValid);

        /// <summary>Display variant: multi-token colored display (Logic row with MultiPick/PairTransform card, display mode).</summary>
        public bool IsMultiTokenDisplayMode => IsDisplayMode && _isMultiTokenMode;

        /// <summary>Edit variant: plain TextBox with per-token autocomplete popup (Logic row in multi-token mode).</summary>
        public bool IsMultiTokenTextEditMode => IsPlainTextEditMode && _isMultiTokenMode;

        /// <summary>Controls the per-token autocomplete popup for multi-token rows.</summary>
        private bool _isMultiTokenAutoCompleteOpen;
        public bool IsMultiTokenAutoCompleteOpen
        {
            get => _isMultiTokenAutoCompleteOpen;
            set { if (_isMultiTokenAutoCompleteOpen == value) return; _isMultiTokenAutoCompleteOpen = value; OnPropertyChanged(); }
        }

        private IReadOnlyList<SpeziAutoCompleteItem> _autoCompleteItems = Array.Empty<SpeziAutoCompleteItem>();
        public IReadOnlyList<SpeziAutoCompleteItem> AutoCompleteItems
        {
            get => _autoCompleteItems;
            set { _autoCompleteItems = value ?? Array.Empty<SpeziAutoCompleteItem>(); OnPropertyChanged(); }
        }

        private bool _isFieldSelectorOpen;
        /// <summary>Controls whether the custom field-selector popup is open for this row.</summary>
        public bool IsFieldSelectorOpen
        {
            get => _isFieldSelectorOpen;
            set { if (_isFieldSelectorOpen == value) return; _isFieldSelectorOpen = value; OnPropertyChanged(); }
        }

        private string _multiTokenSeparator = "-";
        /// <summary>Separator for splitting/joining tokens. Set by CheckupViewModel when entering edit mode.</summary>
        public string MultiTokenSeparator
        {
            get => _multiTokenSeparator;
            set { _multiTokenSeparator = value ?? "-"; OnPropertyChanged(); }
        }

        // ── Value mismatch (Logic Dropdown / Search rows) ──

        /// <summary>The part of DisplayValue that matched a catalog PRI entry (may be the whole value or empty).</summary>
        public string MatchedPart
        {
            get => _matchedPart;
            set
            {
                _matchedPart = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>The trailing part of DisplayValue that did not match any catalog PRI entry (empty when no mismatch).</summary>
        public string UnmatchedPart
        {
            get => _unmatchedPart;
            set
            {
                string nv = value ?? "";
                bool wasChanged = nv != _unmatchedPart;
                _unmatchedPart = nv;
                OnPropertyChanged();
                if (wasChanged)
                {
                    OnPropertyChanged(nameof(HasValueMismatch));
                    OnPropertyChanged(nameof(IsNormalDisplayMode));
                    OnPropertyChanged(nameof(IsValueMismatchDisplayMode));
                }
            }
        }

        /// <summary>True when the current DisplayValue does not exactly match a catalog PRI (tail is red).</summary>
        public bool HasValueMismatch => !string.IsNullOrEmpty(_unmatchedPart);

        // ── Expert Mode pending-apply state ──

        /// <summary>True when this Expert row has a BL-computed value that differs from the current Inventor document value.
        /// Drives amber ValueForeground and the Expert Apply button in Col-2.</summary>
        public bool IsExpertPendingApply
        {
            get => _isExpertPendingApply;
            set { _isExpertPendingApply = value; OnPropertyChanged(); }
        }

        /// <summary>The BL-computed value waiting to be written to Inventor. Valid when IsExpertPendingApply is true.</summary>
        public string ExpertComputedValue
        {
            get => _expertComputedValue;
            set { _expertComputedValue = value; OnPropertyChanged(); }
        }

        // ── Formula (fx) state ──
        // Mirrors Inventor's iProperties / Fx-parameter behaviour: the row shows the evaluated
        // value, and an fx toggle reveals/edits the formula behind it. Set on every refresh from
        // FieldCatalogBuilder.ResolveFieldFormula.

        /// <summary>True when this row's value is driven by an Inventor formula (iProperty expression
        /// or parameter equation). Enables the fx toggle button.</summary>
        public bool HasFormula
        {
            get => _hasFormula;
            set
            {
                if (_hasFormula == value) return;
                _hasFormula = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowFormulaToggle));
            }
        }

        /// <summary>The formula/equation behind the value (e.g. "=&lt;NUP_BENENNUNG&gt;" or "d3 + 10 mm").
        /// Shown and edited only in the fx (formula) state.</summary>
        public string FormulaText
        {
            get => _formulaText;
            set { _formulaText = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>True while the fx toggle is engaged and the user is editing the formula.
        /// Suppresses the normal value editors and shows the formula TextBox instead.</summary>
        public bool IsFormulaEditing
        {
            get => _isFormulaEditing;
            set
            {
                if (_isFormulaEditing == value) return;
                _isFormulaEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFormulaEditMode));
                OnPropertyChanged(nameof(ShowFormulaToggle));
                NotifyEditModeChanged();
            }
        }

        /// <summary>True after a formula Apply that Inventor rejected (e.g. invalid/cyclic parameter
        /// reference). Paints the equation editor red and keeps the row in edit; cleared as soon as
        /// the user edits the text or a valid equation applies. (Parameters only — Inventor validates
        /// those; unknown iProperty refs are accepted-but-empty, matching Inventor.)</summary>
        public bool IsFormulaInvalid
        {
            get => _isFormulaInvalid;
            set { if (_isFormulaInvalid == value) return; _isFormulaInvalid = value; OnPropertyChanged(); }
        }

        /// <summary>Value-field shows the formula editor TextBox (fx pressed + inline editing active).</summary>
        public bool IsFormulaEditMode => _isInlineEditing && _isFormulaEditing;

        /// <summary>fx toggle button is shown: a formula is present and we are either displaying the
        /// value or already editing the formula (never during a plain literal edit).</summary>
        public bool ShowFormulaToggle => _hasFormula && (IsDisplayMode || _isFormulaEditing);

        /// <summary>True for Logic rows (Search card) — the filter text input is the edit control.</summary>
        public bool IsLogicSearchMode
        {
            get => _isLogicSearchMode;
            set
            {
                if (_isLogicSearchMode == value) return;
                _isLogicSearchMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Display variant: normal TextBlock (not multi-token, no value mismatch, or in edit mode).</summary>
        public bool IsNormalDisplayMode => IsDisplayMode && !_isMultiTokenMode && !HasValueMismatch;

        /// <summary>Display variant: mismatch split — matched part normal, unmatched tail red (Logic Dropdown/Search rows).</summary>
        public bool IsValueMismatchDisplayMode => IsDisplayMode && !_isMultiTokenMode && HasValueMismatch;


        /// <summary>Applies a live filter to CatalogDropdownView for Search card mode.
        /// Empty text clears the filter. Items match when any SearchValue contains the text;
        /// falls back to checking PriValue and SecValue when SearchValues is empty.</summary>
        public void ApplySearchFilter(string text)
        {
            if (_catalogDropdownView != null)
            {
                if (string.IsNullOrEmpty(text))
                {
                    _catalogDropdownView.Filter = null;
                }
                else
                {
                    _catalogDropdownView.Filter = obj =>
                    {
                        if (obj is not CatalogDropdownItem item) return false;
                        if (item.SearchValues.Count > 0)
                            return item.SearchValues.Any(v => v.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                        return item.PriValue.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0
                            || item.SecValue.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0;
                    };
                }
            }
            // Mirror filter onto the multi-column popup view (U1).
            if (_logicDropdownRowsView != null)
            {
                if (string.IsNullOrEmpty(text))
                {
                    _logicDropdownRowsView.Filter = null;
                }
                else
                {
                    _logicDropdownRowsView.Filter = obj =>
                        obj is LogicDropdownItemRow r &&
                        r.Filterable.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            // Do NOT call Refresh() here — the Filter setter already calls RefreshOrDefer() internally.
            HighlightedLogicItem = null; // filter changed → reset arrow-key highlight
        }

        private void NotifyEditModeChanged()
        {
            OnPropertyChanged(nameof(IsDisplayMode));
            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(IsFieldSelectorVisible));
            OnPropertyChanged(nameof(IsSpecialRow));
            OnPropertyChanged(nameof(IsTextEditMode));
            OnPropertyChanged(nameof(IsPlainTextEditMode));
            OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
            OnPropertyChanged(nameof(IsComboEditMode));
            OnPropertyChanged(nameof(IsLogicComboEditMode));
            OnPropertyChanged(nameof(IsLogicSearchEditMode));
            OnPropertyChanged(nameof(IsLogicPickerDisplayMode));
            OnPropertyChanged(nameof(HasValueChanged));
            OnPropertyChanged(nameof(IsEditValueValid));
            OnPropertyChanged(nameof(ShowValidationError));
            OnPropertyChanged(nameof(IsNormalDisplayMode));
            OnPropertyChanged(nameof(IsValueMismatchDisplayMode));
            OnPropertyChanged(nameof(IsFormulaEditMode));
            OnPropertyChanged(nameof(ShowFormulaToggle));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
