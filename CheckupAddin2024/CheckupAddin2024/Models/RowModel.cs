using System.Collections.Generic;
using System.ComponentModel;
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
    ///   IsWritableField  IsInlineEditing  HasAllowedValues  → IsTextEditMode  IsComboEditMode
    ///   true             true             false               true             false
    ///   true             true             true                false            true
    ///   any              false            any                 false            false
    ///
    /// Apply/Cancel buttons are visible only when HasValueChanged (EditText ≠ OriginalValue).
    /// Apply is enabled only when IsEditValueValid.
    /// ShowValidationError (HasValueChanged && !IsEditValueValid) drives the red-border DataTrigger.
    /// </remarks>
    public class RowModel : INotifyPropertyChanged
    {
        private string _fieldKey         = "";
        private string _fieldLabel       = "";
        private string _displayValue     = "";
        private System.Windows.Media.Brush _valueForeground = System.Windows.Media.Brushes.Black;
        private FieldItem _selectedField;
        private bool   _isEditable;
        private bool   _isInlineEditing;
        private bool   _isWritableField;
        private string _editText         = "";
        private bool   _canRemove        = true;
        private bool   _isDragOver;
        private bool   _isFieldMissing;
        private bool   _isDragging;
        private IReadOnlyList<string> _allowedValues = System.Array.Empty<string>();
        private string _highlightedAllowedValue;
        private string _originalValue    = "";
        private IReadOnlyList<CatalogDropdownItem> _catalogDropdownItems = System.Array.Empty<CatalogDropdownItem>();
        private ListCollectionView _catalogDropdownView;
        private bool _isLogicSearchMode;
        private bool   _isAllowedValuesPopupOpen;
        private string _allowedValuesFilterText;    // null = show full list; set only when user actually types
        private bool _suppressFilterOnLoad;         // true during SetEditTextSuppressFilter — prevents auto-filter on edit entry
        private bool _isLogicPopupOpen;
        private bool _isFieldSelectorOpen;
        private bool _isExpertPendingApply;
        private string _expertComputedValue;
        private bool _hasFormula;
        private string _formulaText = "";
        private bool _isFormulaEditing;
        private bool _isFormulaInvalid;

        // ── Multi-column Logic dropdown popup (U1) ──
        private IReadOnlyList<LogicDropdownColumn> _logicDropdownColumns = System.Array.Empty<LogicDropdownColumn>();
        private IReadOnlyList<LogicDropdownItemRow> _logicDropdownRows   = System.Array.Empty<LogicDropdownItemRow>();
        private ListCollectionView _logicDropdownRowsView;
        private double _logicDropdownFieldWidth  = 280;
        private double _logicDropdownPopupHeight = 320;
        private string _logicDropdownContextKey  = "";

        /// <summary>Field key — determines which Inventor value this row reads/writes (see FieldItem key conventions).</summary>
        public string FieldKey
        {
            get => _fieldKey;
            set
            {
                _fieldKey = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSpecialRow));
                OnPropertyChanged(nameof(IsNormalDisplayMode));
                OnPropertyChanged(nameof(IsFieldSelectorVisible));
                OnPropertyChanged(nameof(IsPlainTextEditMode));
                OnPropertyChanged(nameof(IsComboEditMode));
                if (_isExpertPendingApply)
                {
                    _isExpertPendingApply = false;
                    OnPropertyChanged(nameof(IsExpertPendingApply));
                }
            }
        }

        /// <summary>Label shown in the left column.</summary>
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

        /// <summary>Currently selected item in the field-selector ComboBox.</summary>
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

        /// <summary>True while the user is actively editing this row's value.</summary>
        public bool IsInlineEditing
        {
            get => _isInlineEditing;
            set { _isInlineEditing = value; OnPropertyChanged(); NotifyEditModeChanged(); if (!value) { IsSyncProposal = false; IsLogicPopupOpen = false; _isAllowedValuesPopupOpen = false; _isFormulaEditing = false; _isFormulaInvalid = false; OnPropertyChanged(nameof(IsFormulaEditing)); OnPropertyChanged(nameof(IsFormulaEditMode)); OnPropertyChanged(nameof(IsFormulaInvalid)); } OnPropertyChanged(nameof(ShowFormulaToggle)); }
        }

        /// <summary>Set by the sync logic when this edit was proposed automatically after the partner row was applied.
        /// Suppresses the back-sync cascade: accepting a proposal does not re-propose the partner.</summary>
        public bool IsSyncProposal { get; set; }

        /// <summary>True if the field bound to this row supports write-back to Inventor.</summary>
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

        /// <summary>Edit mode for plain free-text non-Logic-dropdown fields — shows the standard TextBox.</summary>
        public bool IsPlainTextEditMode => IsEditMode && !HasAllowedValues && !HasCatalogDropdownItems && !_isFormulaEditing;

        /// <summary>Edit mode for list fields (has AllowedValues), excluding Logic-dropdown rows.</summary>
        public bool IsComboEditMode => IsEditMode && HasAllowedValues && !HasCatalogDropdownItems && !_isFormulaEditing;

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

        /// <summary>
        /// False only for AllowedValues fields when EditText is not in the list.
        /// Always true for free-text fields.
        /// </summary>
        public bool IsEditValueValid
        {
            get
            {
                if (!HasAllowedValues) return true;
                foreach (string v in _allowedValues)
                    if (string.Equals(v, _editText, System.StringComparison.OrdinalIgnoreCase))
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
                _allowedValues = value ?? System.Array.Empty<string>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAllowedValues));
                OnPropertyChanged(nameof(IsEditValueValid));
                OnPropertyChanged(nameof(ShowValidationError));
                OnPropertyChanged(nameof(IsTextEditMode));
                OnPropertyChanged(nameof(IsComboEditMode));
                OnPropertyChanged(nameof(IsPlainTextEditMode));
                OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
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
                if (!HasAllowedValues) return System.Array.Empty<string>();
                if (string.IsNullOrEmpty(_allowedValuesFilterText)) return _allowedValues;
                var result = new System.Collections.Generic.List<string>();
                foreach (var v in _allowedValues)
                    if (v.IndexOf(_allowedValuesFilterText, System.StringComparison.OrdinalIgnoreCase) >= 0) result.Add(v);
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
            get { return _highlightedAllowedValue; }
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
            int next;
            if (cur < 0)
                next = delta > 0 ? 0 : items.Count - 1;
            else
                next = System.Math.Max(0, System.Math.Min(items.Count - 1, cur + delta));
            HighlightedAllowedValue = items[next];
        }

        // ── Catalog Logic dropdown items (Dropdown / Search card) ──

        /// <summary>Catalog entries for Logic rows with a Dropdown or Search card.
        /// Setting this rebuilds CatalogDropdownView.</summary>
        public IReadOnlyList<CatalogDropdownItem> CatalogDropdownItems
        {
            get => _catalogDropdownItems;
            set
            {
                _catalogDropdownItems = value ?? System.Array.Empty<CatalogDropdownItem>();

                var list = _catalogDropdownItems as System.Collections.IList
                           ?? new List<CatalogDropdownItem>(_catalogDropdownItems);
                var view = new ListCollectionView(list);
                bool hasGroups = false;
                foreach (var item in _catalogDropdownItems)
                    if (!string.IsNullOrEmpty(item.GroupName)) { hasGroups = true; break; }
                if (hasGroups)
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

        /// <summary>Grouped view over CatalogDropdownItems for the Logic ComboBox.</summary>
        public ListCollectionView CatalogDropdownView => _catalogDropdownView;

        /// <summary>True when this row has catalog dropdown items.</summary>
        public bool HasCatalogDropdownItems => _catalogDropdownItems != null && _catalogDropdownItems.Count > 0;

        // ── Multi-column Logic dropdown popup (U1) ──

        /// <summary>Column specs for the multi-column popup (shared between header + every item row).</summary>
        public IReadOnlyList<LogicDropdownColumn> LogicDropdownColumns
        {
            get => _logicDropdownColumns;
            set { _logicDropdownColumns = value ?? System.Array.Empty<LogicDropdownColumn>(); OnPropertyChanged(); }
        }

        /// <summary>Per-item rows (wraps CatalogDropdownItem + per-column cells using the shared columns).</summary>
        public IReadOnlyList<LogicDropdownItemRow> LogicDropdownRows
        {
            get => _logicDropdownRows;
            set
            {
                _logicDropdownRows = value ?? System.Array.Empty<LogicDropdownItemRow>();
                var list = _logicDropdownRows as System.Collections.IList
                           ?? new List<LogicDropdownItemRow>(_logicDropdownRows);
                var view = new ListCollectionView(list);
                bool hasGroups = false;
                foreach (var r in _logicDropdownRows)
                    if (!string.IsNullOrEmpty(r.GroupName)) { hasGroups = true; break; }
                if (hasGroups)
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LogicDropdownItemRow.GroupName)));
                _logicDropdownRowsView = view;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LogicDropdownRowsView));
            }
        }

        public ListCollectionView LogicDropdownRowsView => _logicDropdownRowsView;

        /// <summary>Popup border MinWidth — set to field column width at open time (TDD §5.13).</summary>
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

        /// <summary>ScrollViewer MaxHeight for popup rows — user-resizable via height Thumb.</summary>
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

        /// <summary>Registry key suffix for persisting column widths (= catalog ID).</summary>
        public string LogicDropdownContextKey
        {
            get => _logicDropdownContextKey;
            set { _logicDropdownContextKey = value ?? ""; OnPropertyChanged(); }
        }

        // ── Row management ──

        /// <summary>False when only one row remains (enforced by EnforceButtonRules).</summary>
        public bool CanRemove
        {
            get => _canRemove;
            set { _canRemove = value; OnPropertyChanged(); }
        }

        /// <summary>True while another row is being dragged over this row.</summary>
        public bool IsDragOver
        {
            get => _isDragOver;
            set { _isDragOver = value; OnPropertyChanged(); }
        }

        /// <summary>True when FieldKey is set but the current document's catalog does not contain it.</summary>
        public bool IsFieldMissing
        {
            get => _isFieldMissing;
            set { _isFieldMissing = value; OnPropertyChanged(); }
        }

        /// <summary>True while this row is being dragged — triggers 50% opacity DataTrigger.</summary>
        public bool IsDragging
        {
            get => _isDragging;
            set { _isDragging = value; OnPropertyChanged(); }
        }

        // ── Logic Set (SPECIAL:LOGIC:) rows ──

        private bool _hasPickerButton;
        public bool HasPickerButton
        {
            get => _hasPickerButton;
            set { _hasPickerButton = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLogicPickerDisplayMode)); }
        }

        public bool IsLogicPickerDisplayMode => IsDisplayMode && _hasPickerButton;

        private string _linkedGroupId = "";
        public string LinkedGroupId
        {
            get => _linkedGroupId;
            set { _linkedGroupId = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(IsLinked)); OnPropertyChanged(nameof(IsLinkedAndConnected)); }
        }

        public bool IsLinked => !string.IsNullOrEmpty(_linkedGroupId);

        private bool _isConnected;
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

        public bool IsLinkedAndConnected => IsLinked && IsConnected;

        // ── Multi-token display mode (Logic rows with MultiPick / PairTransform card) ──

        private bool _isMultiTokenMode;
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

        private List<SpeziSegment> _multiTokenSegments = new List<SpeziSegment>();
        public List<SpeziSegment> MultiTokenSegments
        {
            get => _multiTokenSegments;
            set
            {
                _multiTokenSegments = value ?? new List<SpeziSegment>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMultiTokenMismatch));
            }
        }

        public bool IsMultiTokenMismatch
        {
            get
            {
                foreach (var s in _multiTokenSegments) if (!s.IsValid) return true;
                return false;
            }
        }

        public bool IsMultiTokenDisplayMode => IsDisplayMode && _isMultiTokenMode;

        public bool IsMultiTokenTextEditMode => IsPlainTextEditMode && _isMultiTokenMode;

        private bool _isMultiTokenAutoCompleteOpen;
        public bool IsMultiTokenAutoCompleteOpen
        {
            get => _isMultiTokenAutoCompleteOpen;
            set { if (_isMultiTokenAutoCompleteOpen == value) return; _isMultiTokenAutoCompleteOpen = value; OnPropertyChanged(); }
        }

        private IReadOnlyList<SpeziAutoCompleteItem> _autoCompleteItems = new SpeziAutoCompleteItem[0];
        public IReadOnlyList<SpeziAutoCompleteItem> AutoCompleteItems
        {
            get => _autoCompleteItems;
            set { _autoCompleteItems = value ?? new SpeziAutoCompleteItem[0]; OnPropertyChanged(); }
        }

        private string _multiTokenSeparator = "-";
        public string MultiTokenSeparator
        {
            get => _multiTokenSeparator;
            set { _multiTokenSeparator = value ?? "-"; OnPropertyChanged(); }
        }

        // ── Value mismatch (Logic Dropdown / Search rows) ──

        private string _matchedPart = "";
        public string MatchedPart
        {
            get => _matchedPart;
            set { _matchedPart = value ?? ""; OnPropertyChanged(); }
        }

        private string _unmatchedPart = "";
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

        public bool HasValueMismatch => !string.IsNullOrEmpty(_unmatchedPart);

        public bool IsValueMismatchDisplayMode => IsDisplayMode && !_isMultiTokenMode && HasValueMismatch;

        /// <summary>True for Logic rows with a Search card — live-filter text input is the edit control.</summary>
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

        /// <summary>True for any SPECIAL: field key — drives the red "S:" prefix in the field-selector ComboBox label.</summary>
        public bool IsSpecialRow => _fieldKey.StartsWith("SPECIAL:", System.StringComparison.Ordinal);

        /// <summary>Display variant: normal TextBlock (not multi-token, no value mismatch).</summary>
        public bool IsNormalDisplayMode => IsDisplayMode && !_isMultiTokenMode && !HasValueMismatch;

        public bool IsLogicPopupOpen
        {
            get => _isLogicPopupOpen;
            set
            {
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
            get { return _highlightedLogicItem; }
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
            var items = new System.Collections.Generic.List<LogicDropdownItemRow>();
            foreach (var obj in _logicDropdownRowsView)
            {
                var item = obj as LogicDropdownItemRow;
                if (item != null) items.Add(item);
            }
            if (items.Count == 0) return;
            int cur = _highlightedLogicItem != null ? items.IndexOf(_highlightedLogicItem) : -1;
            int next;
            if (cur < 0)
                next = delta > 0 ? 0 : items.Count - 1;
            else
                next = System.Math.Max(0, System.Math.Min(items.Count - 1, cur + delta));
            HighlightedLogicItem = items[next];
        }

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

        // ── Field Selector popup open state (F1) ──

        /// <summary>True while the field-selector popup is open for this row.</summary>
        public bool IsFieldSelectorOpen
        {
            get => _isFieldSelectorOpen;
            set { _isFieldSelectorOpen = value; OnPropertyChanged(); }
        }

        // ── Expert Mode pending-apply state (V1) ──

        /// <summary>True when a $[...] Expert BL formula produced a value that differs from the current doc value.
        /// Drives the amber foreground and pending-apply button.</summary>
        public bool IsExpertPendingApply
        {
            get => _isExpertPendingApply;
            set { _isExpertPendingApply = value; OnPropertyChanged(); }
        }

        /// <summary>The value computed by the Expert BL formula. Non-null only when IsExpertPendingApply is true.</summary>
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

        /// <summary>True while the fx toggle is engaged and the user is editing the formula.</summary>
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

        /// <summary>Applies a live filter to LogicDropdownRowsView for Search card mode.
        /// Empty text clears the filter.</summary>
        public void ApplySearchFilter(string text)
        {
            if (_logicDropdownRowsView == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _logicDropdownRowsView.Filter = null;
            }
            else
            {
                string filterText = text;
                _logicDropdownRowsView.Filter = obj =>
                {
                    var ir = obj as LogicDropdownItemRow;
                    if (ir == null) return false;
                    return ir.Filterable.IndexOf(filterText, System.StringComparison.OrdinalIgnoreCase) >= 0;
                };
            }
            HighlightedLogicItem = null; // filter changed → reset arrow-key highlight
        }

        private void NotifyEditModeChanged()
        {
            OnPropertyChanged(nameof(IsDisplayMode));
            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(IsFieldSelectorVisible));
            OnPropertyChanged(nameof(IsTextEditMode));
            OnPropertyChanged(nameof(IsComboEditMode));
            OnPropertyChanged(nameof(IsPlainTextEditMode));
            OnPropertyChanged(nameof(IsLogicComboEditMode));
            OnPropertyChanged(nameof(IsLogicSearchEditMode));
            OnPropertyChanged(nameof(HasValueChanged));
            OnPropertyChanged(nameof(IsEditValueValid));
            OnPropertyChanged(nameof(ShowValidationError));
            OnPropertyChanged(nameof(IsNormalDisplayMode));
            OnPropertyChanged(nameof(IsLogicPickerDisplayMode));
            OnPropertyChanged(nameof(IsMultiTokenDisplayMode));
            OnPropertyChanged(nameof(IsMultiTokenTextEditMode));
            OnPropertyChanged(nameof(IsValueMismatchDisplayMode));
            OnPropertyChanged(nameof(IsFormulaEditMode));
            OnPropertyChanged(nameof(ShowFormulaToggle));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
