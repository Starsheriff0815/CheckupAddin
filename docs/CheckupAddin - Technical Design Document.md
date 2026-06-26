# CheckupAddin — Technical Design Document

> **Scope:** All four variants — CheckupAddin2024/2025 (.NET 4.8) and CheckupAddin2026/2027 (.NET 8.0).
> **Author of this doc:** Starsheriff.
> **Last updated:** 2026-06-17.

---

## 1. Project Overview

**CheckupAddin** is a WPF MVVM add-in for Autodesk Inventor that provides a dynamic, user-configurable property panel for parts and assemblies. It replaces manual iProperty browsing with a fast, editable grid that shows exactly the fields the user cares about — and lets them write values back without opening Inventor's own dialogs.

**Core value:**

- See all relevant properties of the active or selected part at a glance
- Write iProperties and parameters directly from the add-in
- Saveable preset layouts — load a named preset to show the relevant fields for that document type
- Logics-Constructor: configure derived/computed fields without coding
- ~~Spezi Baukasten: catalog-backed specialty designations for IZ-specific parts~~ **⚠ Legacy** — replaced by Logics-Constructor capability set
- Style Purger: one-click cleanup of unused styles in IDW/IPT/IAM

**Target user:** Engineering teams. German and English UI (language detected automatically from Inventor; additional languages can be added).

---

## 2. Four Variants

| Property         | CheckupAddin2024                      | CheckupAddin2025                      | CheckupAddin2026                       | CheckupAddin2027                       |
|------------------|---------------------------------------|---------------------------------------|----------------------------------------|----------------------------------------|
| Target Inventor  | 2024 (API v28.x)                      | 2025 (API v29.x)                      | 2026 (API v30.x)                       | 2027 (API v31.x)                       |
| Framework        | .NET 4.8, net48                       | .NET 4.8, net48                       | .NET 8.0, net8.0-windows               | .NET 8.0, net8.0-windows               |
| Language version | C# latest (via LangVersion)           | C# latest (via LangVersion)           | C# 12 (latest)                         | C# 12 (latest)                         |
| JSON library     | Newtonsoft.Json                       | Newtonsoft.Json                       | System.Text.Json                       | System.Text.Json                       |
| COM GUID         | 4E7A2B9C-...                          | (unique)                              | D72E8C3A-5B1F-4E3A-9C6D-A1B2C3D4E5F6  | (unique)                               |
| AssemblyName     | CheckupAddIn                          | CheckupAddIn                          | CheckupAddIn                           | CheckupAddIn                           |
| ProgId           | CheckupAddIn.StandardAddInServer      | CheckupAddIn.StandardAddInServer      | CheckupAddIn.StandardAddInServer       | CheckupAddIn.StandardAddInServer       |
| Addin manifest   | Autodesk.CheckupAddIn2024.addin       | Autodesk.CheckupAddIn2025.addin       | Autodesk.CheckupAddIn2026.addin        | Autodesk.CheckupAddIn2027.addin        |
| AppData folder   | %APPDATA%\\Checkup 2024\              | %APPDATA%\\Checkup 2025\              | %APPDATA%\\Checkup 2026\               | %APPDATA%\\Checkup 2027\               |
| Registry key     | Software\\Checkup 2024                | Software\\Checkup 2025                | Software\\Checkup 2026                 | Software\\Checkup 2027                 |
| Pack URI         | /CheckupAddIn;component/...           | /CheckupAddIn;component/...           | /CheckupAddIn;component/...            | /CheckupAddIn;component/...            |

**Policy:** Every feature is implemented in all four variants simultaneously unless the user explicitly says otherwise. All common code lives in `CheckupAddin.Shared/` (VS Shared Project) — see §7.8. Net48 porting rules apply to shared code compiled for 2024/2025 (see §7.1). New variants are cloned with `clone_variant.sh` (copies the thin head project; shared project import path is the same from any sibling folder). The interop DLL for each variant is fetched from the local Inventor install by `fetch_interop.ps1` (gitignored, never committed).

**Canonical resource files:** `DarkTheme.xaml`, `LightTheme.xaml`, `Addin_Language_File_DE.json`, `Addin_Language_File_EN.json`, and (for net8) `Addin_Language_File_DE.xaml` live in the **2026 project only**. The other three variants reference these files via MSBuild `<Link>` items — no physical copies (see §7.6).

---

## 3. Architecture & Data Flow

### Entry point and lifecycle

```
Inventor startup
  └─ loads Autodesk.CheckupAddIn2026.addin (ProgramData)
       └─ StandardAddInServer.Activate()
            ├─ loads Checkup_Settings.json  →  UserSettings
            ├─ creates CheckupViewModel (MVVM)
            ├─ creates CheckupWindow (WPF)
            ├─ adds ribbon buttons (Sheet Metal + 3D Model + Assemble + Drawing tabs)
            └─ subscribes to Inventor events
```

On Inventor shutdown or add-in deactivation:

- All events unsubscribed
- Window disposed
- `GC.Collect()` + `GC.WaitForPendingFinalizers()` to release COM references

### MVVM layers

```
StandardAddInServer  →  CheckupWindow (View)
                              ↓  DataContext
                        CheckupViewModel  →  Services
                              ↓  ObservableCollection
                           RowModel (per row)
```

- **CheckupWindow.xaml** — pure XAML binding; code-behind handles only drag-and-drop and right-click copy-to-clipboard.
- **CheckupViewModel** — all state (`Rows`, `FieldCatalog`, `FileName`, `StatusMessage`) and all `ICommand` via `RelayCommand`.
- **RowModel** — `INotifyPropertyChanged` model for one row: field key, display value, edit state, drag state, logic state, segment data.

### Event flow

| Event                                  | Handler               | Effect                                                        |
|----------------------------------------|-----------------------|---------------------------------------------------------------|
| `ApplicationEvents.OnActivateDocument`   | `OnDocumentActivated`   | Refreshes all row values                                      |
| `ApplicationEvents.OnDeactivateDocument` | `OnDocumentDeactivated` | Clears display                                                |
| `UserInputEvents.OnSelect`               | `OnSelectionChanged`    | Refreshes for new selection (80 ms debounce)                  |
| `UserInputEvents.OnUnSelect`             | `OnUnSelectionChanged`  | Clears multi-select if nothing left                           |
| `DocumentEvents.OnChange`                | per-doc subscription  | Reactive refresh on parameter / iProperty change              |
| `_selectSetPoller` (200 ms timer)        | —                     | Catches async ModelBrowser deselect not covered by OnUnSelect |
| `_autoRefresh` (15 s timer)              | —                     | Fallback catch-all; self-stops after 4 idle ticks (60 s)      |

> Full refresh mechanism with all triggers and guard conditions is specified in Section 5.1 (Refresh mechanism table).

### Services

| Service               | Responsibility                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|-----------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `DocumentResolver`      | Resolves the "best" active document: active IPT, selected component(s) in IAM, or IAM itself                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `FieldCatalogBuilder`   | Discovers all available fields at runtime; resolves field key → display value                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `PropertyReader`        | Reads iProperties (standard + user-defined) and model/user parameters. Also owns `UnitAbbreviation()` (mm/cm/m/in/ft) for `DOC:UnitsLength` — relocated here when `SheetMetalReader` was deleted (Task #29).                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `StylePurger`           | Calls `StylesManager.UpdateStyles()` + `PurgeUnusedStyles()` per doc type (IDW/IPT/IAM)                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `FieldWriter`           | Writes values back to iProperties, parameters, and document-level values. Entry point: `WriteFieldValue(doc, fieldKey, newValue)` → returns `null` on success or an error string on failure. Dispatch: UDEF: → user-defined property set; IPROP\| → standard property set (DisplayName match); PARAM:User:/Model: → parameter by name; DOC: → document-level value. Non-writable keys (read-only system fields, SPECIAL:LOGIC:) return a "not writable" error string. After every confirmed successful write, calls `TryUpdate(doc)` once so dependents (formula iProperties, iLogic, geometry) propagate. Cascade Apply methods wrap their write bursts in `BeginBatch()` → `IDisposable` that flushes one `TryUpdate` per touched doc on dispose, preventing a recalc storm on 27-doc multi-select Logic Apply. |
| `LanguageLoader`        | Loads DE/EN JSON string files; applies to WPF DynamicResource system. Key prefixes: `Btn_` (buttons), `Field_` (field labels), `Tip_` (tooltips), `Lbl_` / `Msg_` (labels/messages), `CatBuilder_` (Catalog Editor UI), `CardType_` (card type names), `Cap_` (capability set UI), `Info_` (info dialog content), `Cycle_` (cycle/error display). Sources: (1) XAML resource dict — base fallback, designer-visible; (2) `Addin_Language_File_DE/EN.json` — overrides + long texts. JSON wins when a key exists in both. See §5.5 for full flow. |
| `ThemeLoader`           | Detects Inventor light/dark theme; swaps XAML resource dictionaries; sets DWM caption color                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| `PresetsManager`        | Manages named row-layout presets (load, save, reset, export from Checkup_Settings.json)                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `UiStateStore`          | Registry (`HKCU\Software\Checkup 2026\`) persistence for: window dimensions (all windows), active tab (Catalogs/Capabilities), Cards panel + Basic Logics panel open/close state, all dropdown popup sizes (autocomplete, field selector, Spezi picker), catalog column widths per catalog, last selected CatalogId + CapabilitySetId, CatalogPicker last-used tab per catalog, InfoDialog sizes per context key, Spezi expander state per group, Spezi view mode + last group. **Field Selector sticky zone (P3, done):** `PinnedFields` = semicolon-separated ordered FieldKey list; `FieldSelGroupCollapsed_<GroupName>` = "1"/"0" per group collapse state. Reset button clears both. **Document Name Field view mode:** `FileNameViewMode` = DWORD `0`/`1`/`2` (Plain/Compact/Detailed). **`DesignMode` flag:** `public static bool DesignMode { get; set; }` — when `true`, all five `TryLoad*` size methods return `false` immediately so every window opens at its factory/XAML size instead of the developer's persisted values. Set by the Design Harness at startup (see §7.7); never set during normal Inventor operation. |
| `CatalogStore`          | Loads/saves catalog data (columns + entries) to AppData per-file JSON                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `CapabilityStore`       | Loads/saves capability sets (card definitions) to AppData per-file JSON                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `CardEngine`            | Evaluates card type logic — Dropdown, Button, Search, Link, Sync, MultiPick, PairTransform, PrefixSuffix, Sort, BasicLogic.                                                                                                                                                                                                                                                                                                                                                                                                              |
| `FormulaEngine`         | Evaluates formula expressions in Basic Logic cards. **Both projects — identical function set (31):** `CONCATENATE`, `IF`, `LOOKUP`, `FORMAT`, `ROUND`, `VALUE` / `NUM`, `STR`, `EQ`, `NE`, `LT`, `GT`, `LTE`, `GTE`, `AND`, `OR`, `NOT`, `JOIN`, `LEFT`, `RIGHT`, `MID`, `TRIM`, `UPPER`, `LOWER`, `REPLACE`, `ABS`, `LEN`, `CONTAINS`, `STARTSWITH`, `ENDSWITH`, `ISEMPTY`, `DEFAULT`. `LOOKUP(key, searchCol, returnCol [, catalogName])` performs catalog entry lookup. `VALUE(text)` / `NUM(text)` strips trailing unit suffixes (`"1.5 mm"` → `1.5`). See §5.10 for syntax details. |
| `DiagLogger`            | Developer diagnostic logging. **Disabled by default** (`Enabled = false`). Configure before use: set `DiagLogger.Enabled = true` and optionally override `DiagLogger.LogFile` (default: `%LOCALAPPDATA%\CheckupAddin\Logs\diag.txt`). Write-on-event; never blocks the UI. API: `Log(area, msg)` / `Section(area, title)` / `Clear()`. The `area` parameter tags every line for filtering (e.g. `"catalog"`, `"expertmode"`). The log folder is created automatically on first write.                                                                                                                                                                                                                                                                 |
| `SpeziBaukastenCatalog` | **⚠ REMOVED.** All legacy Spezi/Halbzeug special-function code removed from both projects. `SpeziAutoCompleteItem.cs` and `SpeziSegment.cs` are **retained** — they serve the MultiToken system (MultiPick card). No other legacy Spezi code remains.                                                                                                                                                                                                                                                 |

---

## 4. Field Key System

Field keys are stable string identifiers for every property the add-in can read or write.

| Prefix                | Example                 | Source                                                                                                                                                          |
|-----------------------|-------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `DOC:`                  | `DOC:Material`            | Document-level values (material, appearance, units, precision)                                                                                                  |
| `IPROP                | `                       | `IPROP                                                                                                                                                          |
| `IPROP                | set                     | prop`                                                                                                                                                           |
| `UDEF:`                 | `UDEF:MyProp`             | User-defined iProperties                                                                                                                                        |
| `PARAM:Model:`          | `PARAM:Model:d25`         | Model parameters                                                                                                                                                |
| `PARAM:User:`           | `PARAM:User:Breite`       | User parameters                                                                                                                                                 |
| `SPECIAL:`              | —                         | **⚠ All hardcoded SPECIAL: entries removed.** The only valid SPECIAL: key is `LOGIC:` (below). The legacy hardcoded keys (`MiterGap`, `FlangeDistance`, `Halbzeug`/`HalbzeugName`/`HalbzeugIdent`, `Spezi1`/`Spezi2`) were fully removed from both projects (Task #29) — no catalog entries and **no resolver code remains**. An old preset still carrying one of these keys degrades to a greyed/strikethrough "missing field" row (see §5.1). No new hardcoded Special Functions shall be added — users build all derived fields via Logics-Constructor cards. |
| `SPECIAL:LOGIC:`        | `SPECIAL:LOGIC:myGroupId` | Logics-Constructor group row                                                                                                                                     |

**Key rules:**

- 2-part and 3-part IPROP keys both handled in `ResolveFieldValue` — short form used in capability files/formulas, long form generated internally.
- The legacy hardcoded `SPECIAL:` keys (`MiterGap`, `FlangeDistance`, `Halbzeug*`, `Spezi1/2`) are **fully removed** (Task #29) — no Field Selector entry and no resolver code. A saved preset created before the Logics-Constructor replaced them may still reference one of these keys; such a row resolves to nothing and renders as a greyed/strikethrough "missing field" (red "S:" prefix retained because the key is still `SPECIAL:`-prefixed). It cannot be created anew.
- `SPECIAL:LOGIC:` rows are the only rows that can have formula/card logic applied. Normal rows (PARAM:, UDEF:, etc.) are never intercepted — this is a hard design rule.
- ⚠ **`UDEF:X` ≠ `PARAM:User:X`** — these are completely different objects. `UDEF:Breite` refers to a user-defined iProperty named "Breite" in the document's property sets. `PARAM:User:Breite` refers to a UserParameter named "Breite" in the Parameters collection. A parameter is **not** an iProperty and vice versa. Writing to `UDEF:X` when no such iProperty exists returns `"User Defined property 'X' not found in any user-defined property set."` → MessageBox appears → row stays in `IsInlineEditing = true` → auto-refresh timer is permanently blocked (`Rows.Any(r => r.IsInlineEditing)` = true) → all Inventor-side changes stop appearing in the addin until the stuck row is cancelled. This is a common preset configuration error when the user adds rows by name rather than through the Field Selector.
- ⚠ **Inventor "Export Parameter" creates a read-only UDEF iProperty** — toggling the Export flag on a `UserParameter` in Inventor's Parameters dialog publishes it into the Custom iProperties tab under the same name. That Custom entry is read-only: `doc.Update()` always reverts a direct write to it. `WriteUserDefinedProperty` detects this case by checking whether a `UserParameter` with the same name exists; if so, it redirects the write to `WriteParameter` instead (the iProperty syncs automatically after `TryUpdate`). The Field Selector shows both `UDEF:X` and `PARAM:User:X` entries for such a parameter; writing to either succeeds.
- ⚠ **`PARAM:Model:d25` displays its expression, not its value** — if d25 is a driven parameter (e.g., `d25.Expression = "Breite"`), `ReadParameterExpression` returns the string `"Breite"`, not the numeric result. This is correct behaviour. Writing to the d25 row redirects through `ResolveParamReference` to the referenced parameter (Breite) and sets it there.

---

## 5. Feature Inventory

### 5.1 Main Window — Checkup Grid

**Header bar (topmost row — above all data rows):**

Four elements from left to right:

| Position      | Element                      | Detail                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
|---------------|------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Far left      | **View Mode Cycle button**     | Button whose **label is the current mode letter + `⇄` symbol** (e.g. `S ⇄`, `C ⇄`, `D ⇄`) — both letter and symbol are fully catalog-managed via the `Lbl_ViewMode_Plain` / `_Compact` / `_Detailed` keys, surfaced by the `FileNameViewModeLabel` VM property. Left-clicking it cycles the Document Name Field through three display modes: **Plain (S) → Compact (C) → Detailed (D) → Plain**. Left-clicking on the Document Name Field itself also cycles. Active mode persisted via `UiStateStore.FileNameViewMode`; **Reset returns it to Plain (S)**. The button's left edge is indented 7 px so it lines up with the Row Drag Handles below (handle Grid is 10 px wide, centered in the rows' 24 px Col 0). Tooltip from `Tip_ViewMode_Cycle`. |
| Second left   | **"File:" label**              | Static text label; vertically aligned with the Row Drag Handles below                                                                                                                                                                                                                                                                                                                                                                                                  |
| Center        | **Document Name Field**        | Shows the filename(s) of the active/selected Source Object; auto-wraps and trims with ellipsis at a **maximum of 2 lines** (Plain/Compact) or **5 lines** (Detailed) — the cap is driven by the `FileNameMaxHeight` VM property; full text visible on mouse-over tooltip. See **View modes** below.                                                                                                                                                                      |
| Far right     | **Logics-Constructor button**  | Opens the Logics-Constructor (CatalogBuilder) window; vertically aligned with the Field Selectors below in both position and width                                                                                                                                                                                                                                                                                                                                       |

**Document Name Field — view modes:**

| Mode         | Format                                                                 | Example                                                                     |
|--------------|------------------------------------------------------------------------|-----------------------------------------------------------------------------|
| **Plain** (default) | Filenames comma-separated; `(N)` suffix per filename when N > 1 in SelectSet; N = 1 suppressed | `Schraube_M8.ipt (6), Mutter_M8.ipt (2)` |
| **Compact**  | Plain format + `, X IAM` appended when the same file spans more than one sub-assembly | `Schraube_M8.ipt (6, 2 IAM), Mutter_M8.ipt (2)` |
| **Detailed** | Grouped by immediate parent sub-assembly (one line per IAM); format: `SubAsm.iam > Part.ipt (N), …`; empty-string group key = components placed directly in the top-level assembly, labelled with the **top-level assembly's own filename** (via `GetTopLevelName()`, which reads `_app.ActiveDocument.FullFileName`; falls back to `DisplayName`, then to the literal `(Top Level)` only if neither can be read) | `Baugruppe.iam > Schraube_M8.ipt (3), Mutter_M8.ipt (2)` ↵ `Traeger_Rechts.iam > Schraube_M8.ipt (3)` |

Rules common to all modes:
- Single opened IPT/IDW → N = 1 always → no suffix in any mode.
- Assembly fallback (nothing selected in IAM) → assembly filename only, no suffix.
- Red text when ≥ 2 distinct documents are selected (all modes).
- Mouse-over tooltip always shows the full current text (handles wrap/ellipsis overflow).
- The field caps at 2 lines (Plain/Compact) or 5 lines (Detailed) — text beyond the cap is trimmed with an ellipsis and only fully visible via tooltip or by widening the window. See `FileNameMaxHeight`.

- **Row:** Field Selector (ComboBox) + Value Field. Up to 30 rows (MAX_ROWS = 30).
- **Field Selector:** the ComboBox column on the right side of every Row. Full behaviour:

  **Width:**
  - Auto-sizes to the longest label text visible in the column — the entire column moves together, not per-row.
  - User can drag the column border (between Value Field and Field Selector) to manually set a wider or narrower width.
  - Column resets to auto-width (longest label) via: Reset action OR mouse double left-click on the border between Value Field and Field Selector.
  - Dropdown popup follows Field Selector dropdown rules (Section 5.13).

  **Closed state — label display:**
  - Shows the label for the currently selected field, pre-populated from the active Preset.
  - **Field missing from current selection** (preset entry not available on the selected object): label still shown, but greyed out + strikethrough. Dropdown stays closed; no error.
  - **Non-editable field**: label shown greyed out (no strikethrough).
  - **Special Function entry**: label prefixed with `S:` — the `S:` prefix is rendered in red; the rest of the label in normal color.
  - **Right-click does nothing here** — `FieldSelectorBtn` (the closed-state header button) has no right-click handler, so it does **not** copy its label to clipboard. Right-click-to-copy is a **Value Field-only** gesture (see below). Right-click inside the *opened* popup is a different, unrelated gesture — pin/unpin a favorite (Zone 3/4, see below) — never copy.

  **Opened state — list contents:**

  The popup is a **custom WPF popup** (not a standard WPF ComboBox dropdown). Top-to-bottom layout:

  **Zone 1 — Search box (always visible):**
  - TextBox at the very top of the popup; receives keyboard focus when popup opens.
  - Filters all zones below **except** Zone 2 (Add Row / Remove Row always visible).
  - Filter = case-insensitive contains-match on the entry's display name.
  - While filter is active: all groups auto-expand (saved collapse state ignored); groups with zero matches hidden entirely.
  - When filter cleared (text deleted or ESC): groups revert to their saved collapse state. Filter text clears automatically when popup closes.

  **Zone 2 — Fixed actions (always visible, never filtered):**
  - `"+ Zeile hinzufügen"` (Add Row) (`__ADD_ROW__`)
  - `"- Zeile entfernen"` (Remove Row) (`__REMOVE_ROW__`)

  **Zone 3 — Favorites / Sticky zone** (German label: "Favoriten"):
  - Contains entries the user has explicitly pinned (any field type, from any group).
  - Pinned entries not available on the current Source Object: shown with **strikethrough** text (still visible, can be unpinned).
  - Subject to search filter; non-matching pinned entries hidden during active filter.
  - Each entry has a **drag handle** (left edge) for reordering within the zone.
  - **Pin / unpin gesture:** single **right-click** on any entry anywhere in the popup toggles its pinned state. Right-click in Zone 3 unpins; right-click in Zone 4 pins. Hover tooltip on all pinnable entries: *"Rechtsklick: Als Favorit anheften / lösen"* (Right-click to pin / unpin as favorite). Add Row / Remove Row are immune — right-click has no effect on them.
  - On unpin (from Zone 3): scroll position in Zone 4 does **not** jump.
  - On unpin: if the entry's group in Zone 4 is collapsed → group **auto-expands** and scrolls to the entry.
  - Persistence: ordered list of FieldKeys in Registry key `PinnedFields` (semicolon-separated, managed by `UiStateStore`).
  - **Reset button** (accessible from main window Reset action): clears `PinnedFields` + all `FieldSelGroupCollapsed_*` keys.

  **Zone 4 — Scrollable grouped entries:**
  Groups are collapsible via chevron (▶/▼) on the group header (click header to toggle). Collapse state persisted per group in Registry (`FieldSelGroupCollapsed_<GroupName>` = "1"/"0"). All groups expand while search filter is active; saved state restored when filter clears.

  **Group order (fixed):**

  **1. Special Functions** (German label: "Sonderfunktionen") — always the first group:
  - Contains: all Logics-Constructor groups (`SPECIAL:LOGIC:`) where at least one Card or Basic Logic is **active** (toggled on). Label: `S: <GroupLabel>` with `S:` in red.
  - **Auto-collapse rule:** when all Logics-Constructor groups have every Card and Basic Logic deactivated, the group has no entries → group **auto-collapses** and the collapse chevron is **disabled**. When at least one LC group becomes active: group auto-expands and chevron re-enables.
  - ⚠ **Legacy note (Task #29):** `SPECIAL:MiterGap` ("Gehrungslücke") and `SPECIAL:FlangeDistance` were once hardcoded entries in this group. Both — together with `SPECIAL:Halbzeug*` and `SPECIAL:Spezi1/2` — are **fully removed** from both projects: no catalog entry, no resolver, no `SheetMetalReader`. An old preset still referencing one of these keys renders as a greyed/strikethrough **missing-field** row (red "S:" prefix retained), never a value.

  **2. User-Defined iProperties** (German: "Benutzerdefinierte iProperties", Grp\_iPropertiesCustom)
  **3. User Parameters** (German: "Benutzerparameter", Grp\_ParamUser)
  **4. iProperties** (Grp\_iProperties)
  **5. Document** (German: "Dokument", Grp\_Document)
  **6. Model Parameters** (German: "Modellparameter", Grp\_ParamModel)

  > Note: the former Sheet Metal Parts group (German: "Blechteile", Grp\_SheetMetal) and its `SPECIAL:MiterGap` / `SPECIAL:FlangeDistance` entries are gone (Task #29) — fully removed, not merely hidden.

  Within each group: entries sorted **alphabetically in natural order** (numbers treated numerically, e.g. `d2` before `d10`).
- **Value Field:** spans the full available width between the Row Drag Handle (left) and the Field Selector (right) — width is independent of the displayed text length. Modes and sub-elements:
  - **Display mode** (default): shows the read value; right-click copies to clipboard.
  - **Inline edit mode**: activated by single left-click; edit frame stretches the full width between Drag Handle and Field Selector.
  - **Dropdown**: a dropdown menu within the Value Field (e.g. Dropdown card rows).
  - **Action Button**: optional button at the far right of the Value Field frame; opens `CatalogPickerWindow` (Section 5.12) as a modal dialog. Present on Button card rows; absent on all other card types.
- **2-line auto-flow cap (all display modes):** the value text auto-wraps and is capped at a **maximum of 2 lines** (`MaxHeight="36"`), then trimmed with an ellipsis; the full value is available on the mouse-over tooltip or by widening the window. This applies uniformly to the three display variants:
  - **Plain value** (`DisplayValue` TextBlock) — `TextWrapping=Wrap` + `MaxHeight` + `CharacterEllipsis`.
  - **Logic value-mismatch** (matched prefix + red unmatched tail) — rendered as a single wrapping `TextBlock` with two `Run`s (the red `Run` carries `CheckupErrorText`) so both parts flow and wrap together.
  - **Multi-token** (MultiPick / PairTransform rows) — the token `WrapPanel` auto-flows; the host `Border` uses `MaxHeight="36"` + `ClipToBounds` to hold it to 2 lines.

**Bottom bar (bottommost row — below all data rows):**

Three groups from left to right. Buttons auto-size to their label text; groups never intersect or overlap when the window is resized.

| Group  | Position              | Buttons                      | Notes                                                                                                                            |
|--------|-----------------------|------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| Left   | Always furthest left  | **Style Purger**                 | Custom background: `CheckupSpecialButtonBackground` (amber tint — see theme palette)                                               |
| Center | Always centered       | **Preset 1**, **Preset 2**, **Preset 3** | Standard button background                                                                                                       |
| Right  | Always furthest right | **Info**, **Reset**, **Close**           | Reset: `CheckupDestructiveButtonBackground` (red tint); Close: `CheckupCancelButtonBackground` (red tint); Info: standard background |

**Row Drag Handle:**

- 2×3 dot grid, far left of every row — leftmost element, before the Value Field.
- Vertically aligned with the "File:" label in the header bar.
- Fades to 50% opacity while dragging (`IsDragging` DataTrigger).
- Drag-and-drop row reorder is initiated exclusively from this handle (not from the rest of the row).

**Scrollbar:** The row area is wrapped in a `ScrollViewer`. A visible scrollbar appears when rows exceed the window height. Mouse wheel scrolling is supported.

**Window title:** `"Checkup"` — same for all windows in both the 2026 and 2024 variants.

**Status message:**

- Displayed above the button row, inside the bottom bar area.
- Italic text, `CheckupSecondaryText` color, FontSize 11.
- Bound to `StatusMessage` on CheckupViewModel — updated after writes, style purge, refresh errors, etc.
- A `Separator` line sits between the status message and the button row.
- **Fixed single-line height** (`Height="16"`, `TextWrapping="NoWrap"`, `TextTrimming="CharacterEllipsis"`, `ToolTip` shows the full text). `StatusMessage` can embed a multi-line `FileName` (Detailed/D view mode joins sub-assembly groups with `\n`) when multiple objects are selected; without a height cap the bar grows to fit every line and pushes row content out of view. The bar must never grow past one row — long/multi-line messages are truncated with an ellipsis, full text available via tooltip.

**Refresh mechanism (hybrid):**

| Trigger                      | Mechanism                                                                    | Notes                                                          |
|------------------------------|------------------------------------------------------------------------------|----------------------------------------------------------------|
| Document switch              | `OnActivateDocument` / `OnDeactivateDocument` events                             | Immediate                                                      |
| Selection change             | `OnSelect` (80 ms debounce) + `OnUnSelect`                                       | Debounce settles rapid multi-fire                              |
| Selection drop to zero       | `_selectSetPoller` (200 ms poll)                                               | Handles async ModelBrowser deselect not covered by events      |
| Parameter / iProperty change | `DocumentEvents.OnChange` (per-document subscription)                          | Reactive, per active document                                  |
| Fallback                     | `_autoRefresh` timer — fires every **15 s**                                        | Catches iProperty changes not fired by above events            |
| Fallback idle-stop           | After **4 consecutive ticks** (60 s) with no Inventor activity, timer self-stops | Any Inventor event resets the counter via `ResetFallbackTimer()` |
| Inline edit guard            | Fallback and poller skip refresh while any row is in inline edit mode        | Prevents clobbering a value being typed                        |

**Preset buttons — additional detail:**

- Labels (`Preset1Name`, `Preset2Name`, `Preset3Name`) are bound to ViewModel properties — names come from `Checkup_Settings.json`, not hardcoded in XAML.
- Each button shows a small dot indicator (4×4 `Ellipse`) below the name — indicates the active preset (see code for fill/trigger details).
- Right-click context menu on each preset button (5 items):

  | Item | Behavior |
  |---|---|
  | **Preset speichern** | Saves the current row layout into the right-clicked slot |
  | *(separator)* | |
  | **Preset exportieren** | Exports the right-clicked slot into a preset library file (see below) |
  | **Alle Presets exportieren** | Exports all 3 slots into a preset library file in one pass |
  | *(separator)* | |
  | **Preset importieren…** | Opens a library file, shows a picker dialog listing all presets in the file; imports the chosen one into the right-clicked slot |

**Preset library file format:**

A plain JSON file containing a `List<PresetData>` (any number of entries — not limited to 3). Each entry has `Name` (string) and `FieldKeys` (string list). The same file format is used for all export and import operations. The file can grow into a personal library over time and can be synced between machines.

**Export behavior (single and all):**

- A `SaveFileDialog` is shown (filter: `*.json`).
- If the chosen file already exists: the file is read first; each preset being exported is matched by `Name` — overwritten if a match is found, appended as a new entry if not. Existing entries with non-matching names are preserved.
- If the file does not exist: it is created with the exported presets as the initial entries.

**Import behavior:**

- An `OpenFileDialog` is shown (filter: `*.json`).
- The chosen file is read and all `Name` values are extracted.
- A **`PresetPickerDialog`** opens, listing the preset names in a `ListBox`. OK is disabled until the user selects one entry. ESC or Cancel closes without importing.
- The selected preset is loaded into the right-clicked slot only. All other slots are untouched.
- The slot's name and field keys are replaced; the active preset indicator updates if the modified slot is currently active.

**PresetPickerDialog:**

- Small themed window (same visual style as `InputDialog`): title, prompt label, `ListBox`, OK / Cancel buttons.
- Default size: 320 × 260 px; `MinWidth="280" MinHeight="180"`; `ResizeMode="CanResize"`; `WindowStartupLocation="CenterOwner"`. Size persisted via `UiStateStore.SaveInfoDialogSize("PresetPicker", ...)` / `TryLoadInfoDialogSize("PresetPicker", ...)` — follows §5.11.
- `ListBox` uses `ScrollViewer.CanContentScroll="False"` for pixel-smooth scrolling.
- OK enabled only when `ListBox.SelectedItem != null`; double-click also confirms; ESC cancels.
- Language keys: `Dlg_PresetPicker_Title`, `Dlg_PresetPicker_Label`.

**Multi-select visual indicator:**

- Document Name Field text rendered in **red** when ≥ 2 documents are selected (in addition to the comma-separated filename list with instance counters).

**`IsDemo` flag and demo-mode warning:**

- `PresetData` has an `IsDemo` (bool, default `false`) property. The shipped `Checkup_Settings.json` sets `IsDemo: true` on all three demo presets.
- `SavePreset()` clears `IsDemo` on the saved slot **only when both** the new name AND the new field keys differ from the demo defaults (`DemoPresetName = "Demo"` / `_demoDefaultFieldKeys`). Rename-only or field-change-only does not clear the flag.
- `PresetsManager.GetDefaults()` **must copy `IsDemo`** from the factory defaults into the returned copies. Omitting this causes `IsDemo=false` after Reset (since `new PresetData()` defaults to `false`), breaking the primary detection path.
- The session counter fields (`_demoWindowOpenCount`, `_demoShownThisSession`) are **`static`** — they persist across `CheckupViewModel` instances within the same Inventor session (AppDomain). `StandardAddInServer` creates a new `CheckupViewModel` on each window open; instance fields would reset on every open.
- **Warning dialog trigger:** `CheckupViewModel.CheckAndShowDemoWarning(Window owner)` is called from `CheckupWindow.OnContentRendered`. It fires a warning `InfoDialog` when `IsDemoActive()` returns true (all 3 presets still have `IsDemo == true`) AND the session frequency rule is met: first window open in the Inventor session always shows the dialog; subsequently every 20th open of the add-in window. The session counter (`_demoWindowOpenCount`) is in-memory only — never persisted.
- **Reset re-arms the warning:** `ResetToDefaults()` restores the factory demo presets, so it also clears the session flags (`_demoShownThisSession = false`, `_demoWindowOpenCount = 0`). The warning then reappears on the **next window open within the same Inventor session**; without this clear, the static "shown this session" flag would suppress it until the 20th open.
- **InfoDialog spec:** `contextKey = "DemoWarning"`, `titleKey = "Dlg_DemoWarning_Title"`, default size 440 × 300, no Cancel button. `Owner = CheckupWindow`. Opened via `ShowDialog()` — modal to CheckupWindow. Z-order: stays above Inventor via the Owner chain (CheckupWindow's Owner is Inventor's HWND). `Topmost = false` per §5.11.
- **Dismissal condition:** The dialog permanently stops appearing once at least one preset has `IsDemo == false` (i.e., the user has saved a preset with both a new name and new field keys).
- **CAD admin workflow:** An admin who configures company presets and copies them into `Checkup_Settings.json` should set `"IsDemo": false` on each preset entry so end users never see the demo warning.

**⚠ Removed — Miter Gap / Flange Distance pair (Task #29):**

The `SPECIAL:MiterGap` / `SPECIAL:FlangeDistance` pair and all of its special row behavior were **removed** from both projects: the adjacency rule (kept-together / moved-together), the "cannot remove when only 2 rows remain" exception, the always-red FlangeDistance, and the editable MiterGap inline path. `EnforceButtonRules()` is retained but now enforces only the general invariant: a row cannot be removed if it would leave fewer than one row. An old preset still referencing these keys shows each as an independent greyed/strikethrough missing-field row.

### 5.2 Source Object (Document resolution)

- Active IPT → use it directly.
- Nothing active / IAM active → use selected component(s).
- `DocumentResolver.GetAllSelectedDocuments(out bool isMulti, out bool isAssemblyFallback, out Dictionary<string,int> instanceCounts, out Dictionary<string,Dictionary<string,int>> subAsmGroups)` deduplicates by `FullFileName`. `instanceCounts` maps `FullFileName → total SelectSet occurrence count` (entries with count = 1 omitted). `subAsmGroups` maps `immediateParentIamFilename → (partFilename → count)` for Detailed view mode; empty-string key = directly in top-level assembly.
- Multi-select (`isMulti=true`): `_selectedDocs` list; aggregated display per row.

**Selection event coverage:**

| Selection method                  | `UserInputEvents.OnSelect` | `DocumentEvents.OnChangeSelectSet` |
|-----------------------------------|----------------------------|------------------------------------|
| Manual click / Shift-click        | ✓                          | ✓                                  |
| Box selection                     | ✓                          | ✓                                  |
| **"Select All Occurrences"** (RMB) | ✗                          | ✓                                  |
| **Browser "Select All"**          | ✗                          | ✓                                  |

Both events are subscribed simultaneously; both route through the existing 80 ms debounce (`_selectDebounce`) so double-firings collapse to a single refresh. `OnChangeSelectSet` is a `DocumentEvents` per-document subscription managed in `SubscribeDocEvents` / `UnsubscribeDocEvents`.

**Multi-select display:**

- All values identical → show once, normal color.
- Any difference → all values joined by `|`, shown in red.
- Document Name Field text rendered in red when ≥ 2 docs selected (in addition to comma-separated list with instance counters; see §5.1 view modes).
- Field Selector disabled (`IsSingleSelection` binding) in multi-select.

**Multi-select write:**

- Edit box opens **empty** in multi-select — forces the user to type an explicit new value (no pre-filling from first selected doc).
- **Apply:** loops over `_selectedDocs`; writes the same value to each; collects any per-doc exceptions; shows one consolidated `MessageBox` with all errors after all writes are attempted.
- **Scope:** IPT parts only. No batch write across assemblies (IAM) or drawing sheets (IDW).
- **Style Purge:** remains single-doc only — does not batch across `_selectedDocs`.

### 5.3 Presets

Three named preset buttons stored in `Checkup_Settings.json`. Default names: Part (German: "Bauteil"), Assembly (German: "Baugruppe"), and a third user-configurable preset. Names and row layouts are fully user-configurable.

- Button order left→right: Preset 1 (sheet metal part fields by default), Preset 2 (assembly fields by default), Preset 3 (user-defined).
- Exact field lists are maintained in `Checkup_Settings.json` and change over time — do not hardcode field counts here.
- Fresh window / Reset loads Preset 1; falls back to an empty row layout if presets are missing.
- `PresetsManager` handles load/save/reset; `UiStateStore` remembers active preset index.

**Active preset indicator — Option C (border + background tint):**

- **Active:** 1 px border in `CheckupPresetActiveBorder` (`#0696D7` accent blue) + `CheckupPresetActiveBackground` (10% alpha blue tint) as button background.
- **Inactive:** no border (transparent), button background = `CheckupBackground` — the button surface blends into the window panel. Button shape still visible from the 1 px border frame present at all times (color switches, thickness stays).
- Text/label unchanged in both states.
- Implemented via `DataTrigger` on `IsPreset1/2/3Active` in the button `ControlTemplate` — inline `Border` wrapping a `TextBlock`.
- The old `Ellipse` dot indicator is removed.
- **Theme tokens:** `CheckupPresetActiveBackground` (Dark `#1A0696D7`, Light `#140696D7`) and `CheckupPresetActiveBorder` (both themes `#0696D7`) defined in `DarkTheme.xaml` / `LightTheme.xaml`.

### 5.4 Theme System

- Detects Inventor scheme via `app.ThemeManager.ActiveTheme.Name` → `"LightTheme"` / `"DarkTheme"`.
- Fallback: `UserInterfaceManager.Theme` via late binding.
- Never reads Windows OS dark mode / registry — Inventor theme only.
- Swaps `DarkTheme.xaml` / `LightTheme.xaml` resource dictionaries at runtime.
- DWM caption color set via `DWMWA_CAPTION_COLOR=35` P/Invoke: dark `#2E3440` (COLORREF `0x40342E`), light resets to `0xFFFFFFFF`.
**Color resource key palette (both theme files must define all keys):**

| Resource Key | Dark value | Light value | Usage |
|---|---|---|---|
| `CheckupWindowBackground` | `#3B4453` | `#F0F0F0` | Main window + client area background |
| `CheckupRowBackground0` | `#3B4453` | `#F0F0F0` | Alternating row background (even) |
| `CheckupRowBackground1` | `#353D4C` | `#EFF2F7` | Alternating row background (odd) |
| `CheckupPrimaryText` | `#F5F5F5` | `#000000` | Primary label/value text |
| `CheckupSecondaryText` | `#8090A8` | `#5C5C5C` | Dimmed / secondary text |
| `CheckupLabelText` | `#9AAABB` | `#323232` | Field label text |
| `CheckupErrorText` | `#FF6B6B` | `#CC0000` | Error / invalid value highlight |
| `CheckupSeparator` | `#4A5365` | `#D0D0D0` | Row and column separator lines |
| `CheckupButtonBackground` | `#2E3645` | `#E8E8E8` | Standard button surface |
| `CheckupButtonBorder` | `#4A5570` | `#C0C0C0` | Standard button border |
| `CheckupButtonForeground` | `#F5F5F5` | `#000000` | Button label text |
| `CheckupSpecialButtonBackground` | `#3D2A14` | `#FFEBD2` | Style Purger button (amber tint) |
| `CheckupDestructiveButtonBackground` | `#3D1820` | `#FFD2D2` | Reset button (red tint) |
| `CheckupApplyButtonBackground` | `#1A3A5C` | `#DCEBFF` | Apply/OK button (blue tint) |
| `CheckupCancelButtonBackground` | `#3D1820` | `#FFD2D2` | Cancel/Close button (red tint) |
| `CheckupActionItemForeground` | `#4CC2FF` | `#1A6FBF` | Dropdown action items (Add/Remove Row) |
| `CheckupGroupHeaderBackground` | `#2E3440` | `#EEEEEE` | Group header row background |
| `CheckupGroupHeaderForeground` | `#6A7A90` | `#505050` | Group header label text |
| `CheckupDragHandleFill` | `#5A6880` | `#AAAAAA` | Drag handle dot grid |
| `CheckupDragHighlight` | `#0696D7` | `#66BCE3` | Drag-over row border / preset dot |
| `CheckupPresetActiveBorder` | `#0696D7` | `#0696D7` | Active preset button border |
| `CheckupPresetActiveBackground` | `#1A0696D7` | `#140696D7` | Active preset button tint |
| `CheckupSelectedCardBackground` | `#1A3660` | `#66BCE3` | Selected card / highlighted background |
| `CheckupSelectedRowBackground` | `#0A3D6E` | `#C0DCF5` | Drag-over row background |
| `CheckupLinkStripe` | `#5BA3DE` | `#4E9BD6` | Linked field indicator stripe |
| `CheckupSyncStripe` | `#C8985A` | `#A06020` | Sync indicator stripe |
| `CheckupComboItemBackground` | `#3B4453` | `#F0F0F0` | ComboBox / dropdown item background |
| `CheckupComboItemHoverBackground` | `#0878B8` | `#D8E8FB` | ComboBox / dropdown item hover |
| `CheckupTabActiveBackground` | `#3B4453` | `#F3F3F3` | Active tab button background |
| `CheckupTabInactiveBackground` | `#222933` | `#E0E0E0` | Inactive tab button background |
| `CheckupScrollBarThumb` | `#5A6880` | `#AAAAAA` | Scrollbar thumb |
| `CheckupScrollBarThumbHover` | `#8090A8` | `#888888` | Scrollbar thumb hover |
| `CheckupComboBoxBackground` | `#3B4453` | `#F0F0F0` | Logic dropdown popup border background |
| `CheckupEditableBackground` | `#2E3645` | `#FFFFFF` | Editable TextBox inside custom panels |

**Adding a new theme:** Create `Addin_Language_File_{CODE}.json` (not applicable — this is for language). For themes: copy `DarkTheme.xaml` or `LightTheme.xaml`, rename, change colors. `ThemeLoader.DetectDark()` returns bool; custom theme detection would require updating `ThemeLoader`.

### 5.5 Language System

#### Architecture overview

The language system is file-based and requires no recompile to add a new language. All UI strings that are not read directly from Inventor are stored as key-value pairs in JSON files outside the DLL.

#### File structure — 2026 (dual-file)

| Location | File | Purpose |
|---|---|---|
| `Resources/Languages/` (source) | `Addin_Language_File_DE.json` | German runtime strings (~230 keys: long texts + runtime overrides) |
| `Resources/Languages/` (source) | `Addin_Language_File_EN.json` | English runtime strings |
| `Resources/Languages/` (source) | `Addin_Language_File_DE.xaml` | Short UI strings — merged at XAML parse time (design + runtime) |
| `bin/Languages/` (runtime) | `Addin_Language_File_DE.json` | Copied by build; merged on top by LanguageLoader at runtime |
| `bin/Languages/` (runtime) | `Addin_Language_File_EN.json` | Copied by build; merged on top by LanguageLoader at runtime |

**2026 dual-file resolution order:**
1. XAML lang file merged at XAML parse time → provides short UI strings (~186 keys: buttons, labels, tooltips, card type names, context menu items).
2. JSON merged on top by `LanguageLoader.ApplyTo()` → overrides any key also in XAML (e.g. `Btn_Apply` = "OK" in JSON, "Anwenden" in XAML designer). Adds long texts (help dialogs, multi-line prompts) not in the XAML.
- Keys only in XAML → XAML value at runtime.
- Keys only in JSON → JSON value at runtime.
- Keys in both → JSON value wins.

The comment in the XAML file ("no effect at runtime") refers to shared keys being overridden by JSON — for XAML-only keys, the XAML value IS the runtime value.

#### File structure — 2024 (single JSON)

| Location | File | Purpose |
|---|---|---|
| `Resources/Languages/` (source) | `Addin_Language_File_DE.json` | ALL German strings (~250 keys — superset of both 2026 files) |
| `Resources/Languages/` (source) | `Addin_Language_File_EN.json` | ALL English strings |
| `Resources/Languages/` (source) | `Addin_Language_File_DE.xaml` | **Minimal fallback — only 2 keys** (`Win_Title_Checkup`, `Win_Title_LogicConstructor`) |
| `bin/Languages/` (runtime) | `Addin_Language_File_DE.json` | Copied by build; loaded at runtime |
| `bin/Languages/` (runtime) | `Addin_Language_File_EN.json` | Copied by build; loaded at runtime |

The 2024 XAML fallback has only 2 keys. Any key missing from the 2024 JSON silently shows as empty at runtime — no error, no warning. The 2024 JSON must always be a **superset** of all `{DynamicResource}` keys used in 2024 XAML files.

The `.csproj` files map each JSON to `Languages\Addin_Language_File_*.json` via `<TargetPath>`. JSON files must live in `Resources\Languages\` (not `Resources\`) — the csproj copy rule uses that path.

#### `LanguageLoader` flow

1. **`LanguageLoader.Detect(app)`** — called once from `StandardAddInServer.Activate()`, before any ViewModel or Window is created.
   - Detects language from `Inventor.Application.Locale` (LCID → two-letter ISO code). Falls back to `CultureInfo.CurrentUICulture` if Inventor is not available.
   - Resolves `Languages/` subfolder next to the DLL (`Assembly.Location`).
   - **Fallback chain:** detected language JSON → `Addin_Language_File_EN.json` → `Addin_Language_File_DE.json` → empty `ResourceDictionary`.
   - Parses the JSON with a custom regex parser (no external JSON dependency required); loads all key-value pairs into a `ResourceDictionary`.

2. **`LanguageLoader.ApplyTo(window)`** — called from each window's code-behind after `InitializeComponent()` (and after `ThemeLoader.ApplyTo`).
   - Merges the loaded `ResourceDictionary` into the window's `MergedDictionaries`.
   - Replaces any previously merged language dict (identified by `_LanguageMarker` sentinel key) so re-application is idempotent.

3. **`LanguageLoader.Get(key)`** — for C# code where `DynamicResource` is not available (e.g. message-box text, status messages). Returns the translated string or the key itself as fallback.

#### XAML binding

Every UI string uses `{DynamicResource KeyName}` — never a literal string. Switching language at runtime (by calling `Detect` + `ApplyTo` again) updates all bindings immediately.

#### Adding a new language

1. Copy `Addin_Language_File_EN.json` → `Addin_Language_File_FR.json` (use the two-letter ISO code).
2. Translate the values (not the keys).
3. Place the new file in the `Languages/` folder next to the DLL.
4. No recompile required. The fallback chain ensures EN is used for any missing keys.

#### Rules for new UI strings

- **2026:** Short UI strings → add to the DE XAML lang file (and EN JSON). Long texts / runtime overrides → add to DE and EN JSON only.
- **2024:** ALL strings → add to both DE and EN JSON. Never rely on the 2024 XAML fallback for new keys.
- **Sync rule:** When adding a `{DynamicResource KeyName}` to 2024 XAML, always add the key to both 2024 JSON files (DE + EN) in the same pass.
- Bind in XAML via `{DynamicResource KeyName}`.
- In C# code use `LanguageLoader.Get("KeyName")`.
- Never hardcode a display string in XAML or C#.

### 5.6 Style Purger

Triggered by the "Stile Bereinigen" (Clean Styles) button.

- Config in `Checkup_Settings.json` → `StylePurgeSection`.
- **IDW:** update → copy template resources → delete obsolete symbols → fix dimension alignment → loop purge until stable.
- **IPT/IAM:** capped 8-pass early-exit loop across all style collections.
- **Never auto-saves** the document — user saves manually after review.
- Template path: `V:\CAD\INV\Templates\Standard.idw` (deploy value).
- Matching iLogic VB file: `Bereinigen IDW+IPT+IAM.iLogicVb` — must be kept in sync with `StylePurger.cs`.

### 5.7 IZ Spezis Baukasten — ⚠ Legacy (Replaced by Logics-Constructor)

> **⚠ Removed — historical only.** All Spezi/Halbzeug hardcoded code was removed from both projects (Task #29) — including the backward-compat resolver paths that earlier revisions kept. **No legacy resolver code remains.** Old presets referencing these keys degrade to greyed/strikethrough missing-field rows. New development uses Logics-Constructor groups (`SPECIAL:LOGIC:`) exclusively. No new hardcoded `SPECIAL:` functions shall be added — the correct path is always composable cards.
>
> Full historical documentation (former field keys, picker window, CSV catalog, Halbzeug pair, sync behavior) is in **Appendix B**.

### 5.8 Logics-Constructor Window

**Window properties:** Title language key `Win_Title_LogicConstructor` (DE: "Logik-Baukasten", EN: "Logics-Constructor"). Default size 1500×1100px; minimum 600×400px. Centers on owner (CheckupWindow). Follows all unified window rules (Section 5.11).

**Top-level layout:** Three columns — Left panel (260px default, min 180px) | GridSplitter (5px, draggable) | Right panel (fills remaining width).

---

#### Left panel — Catalog / Capability list

**Tab strip (top):** Two tab buttons: `Catalogs` and `Capabilities`. Uses **Option C active indicator** (same as preset buttons — see §5.3):
- **Active tab:** 1 px `CheckupPresetActiveBorder` border with `BorderThickness="1,1,1,0"` (no bottom border — visually connects the active tab to the list content below) + `CheckupPresetActiveBackground` tint.
- **Inactive tab:** no border (transparent), background = left panel background (`CheckupBackground`) — button dissolves into the panel.
- Effect: tab strip and list below flow together visually; active tab feels like a labelled header for the content, not a detached tab control.
- `CheckupTabActiveBackground` / `CheckupTabInactiveBackground` tokens are **not used** for these buttons (retained in theme files for Spezi picker window tabs which use a different layout).

**List (both tabs — identical structure):**

- Scrollable ListBox; items not horizontally scrollable.
- Each item: **location icon** + name text (ellipsis trim) + tooltip showing last-updated date/time.

**Location icons:**

| Icon | Meaning                                                                    | Source property      |
|------|----------------------------------------------------------------------------|----------------------|
| 💾   | File is treated as local — AppData copy OR mapped drive OR true local path | `IsOnUncPath == false` |
| 🌐   | File path starts with `\\` (literal UNC path)                                | `IsOnUncPath == true`  |

`IsOnUncPath` is runtime-only (`[JsonIgnore]`) — derived from the physical file path at load time, never stored in JSON.

**Detection logic (**`IsUncPath`**) — confirmed approach:**

```csharp
private static bool IsUncPath(string path)
{
    if (string.IsNullOrEmpty(path)) return false;
    if (path.StartsWith(@"\\")) return true;          // literal UNC — \\server\share\...
    try
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root))
            return new DriveInfo(root).DriveType == DriveType.Network;
    }
    catch (IOException) { return false; }   // disconnected drive → treat as local (safe fallback)
    return false;
}
```

`DriveInfo.DriveType` reads the Windows drive table — no I/O, no network call. `DriveType.Network` is set by Windows for all mapped drives regardless of how they were mapped (policy, login script, manual).

| Path                | Detected as | Reason                            |
|---------------------|-------------|-----------------------------------|
| `\\server\share\file` | Network     | `StartsWith(@"\\")`                 |
| `V:\CAD\Inventor\...` | Network     | `DriveType.Network`                 |
| `C:\Users\...`        | Local       | `DriveType.Fixed`                   |
| `V:\` (disconnected)  | Local       | `DriveInfo` throws → caught → false |

**Accepted limitation:** a distribution file manually copied to a local drive is treated as local — correct behavior (it is the user's local copy).

---

**Lock system:**

**What makes an item locked — two independent conditions (OR):**

| Condition           | Source                        | Persisted?        | Notes                                                                                                   |
|---------------------|-------------------------------|-------------------|---------------------------------------------------------------------------------------------------------|
| `IsOnUncPath == true` | Runtime, set by store on load | No (`[JsonIgnore]`) | UNC files are **always** forced locked, regardless of the `IsLocked` JSON value — "never trust the JSON flag" |
| `IsLocked == true`    | Persisted bool in JSON        | Yes               | User-set toggle for local AppData files                                                                 |

`IsSelectedCatalogLocked = IsOnUncPath OR IsLocked` — both conditions result in identical locked behavior.

**Lock strip** (shown below the list whenever an item is selected):

| State    | Label shown           | Button shown | Button action                          |
|----------|-----------------------|--------------|----------------------------------------|
| Locked   | "Locked — read-only"  | **Unlock**       | See unlock behavior below              |
| Unlocked | "Unlocked — editable" | **Lock**         | Sets `IsLocked = true`, saves to AppData |

**Locked-state label color:** the "Locked" status label is rendered in the **error/red color** (`CheckupErrorText`, `FontWeight="SemiBold"`) so the locked state is highly visible; the "Unlocked" label uses the normal secondary text color. Applies to both the Catalog and Capabilities lock strips.

**Unlock behavior — two scenarios depending on source:**

1. **Item is on a UNC path (**`IsOnUncPath == true`**):**
   - Clicking Unlock calls `UnlockToLocal()` → **copies the file to AppData**, sets `IsLocked = false`, `IsOnUncPath = false`.
   - The item now shows 💾 and is editable. The UNC original is untouched.
   - This is a **migration** — the user gets their own local editable copy; the shared distribution file is never modified.
2. **Item is in AppData with** `IsLocked == true`**:**
   - Clicking Unlock toggles `IsLocked = false` and saves in place.
   - No file is copied — the AppData file is simply unlocked.

**Sync gap — AppData copy vs. updated UNC original:**

After a user unlocks a UNC file (scenario 1), a sync gap can develop:

- Load order on every startup: UNC / distribution loaded first → then AppData files loaded for the same ID, **overwriting** the UNC entry in memory.
- If the admin later updates the UNC file (same ID, newer content), the user's AppData copy silently wins on every subsequent load. The user sees their old migrated copy; the admin's update is invisible.
- There is no in-app notification that the shared original is newer.

**Current workaround — Delete the AppData copy:**

- The user selects the 💾 item (their migrated AppData copy) and clicks **Delete**.
- `Delete()` removes the physical AppData file and the in-memory entry.
- On next Inventor start: no AppData copy exists for that ID → the UNC version loads unchallenged → user sees the admin's updated version.
- This works but is unintuitive: "Delete" carries a destructive connotation; the user does not know why they should delete a file to get an update.

**Implemented solution:**

- The store loads both versions (UNC + AppData for the same ID) and compares `LastUpdated` timestamps.
- If the UNC/distribution version is **newer** than the AppData copy: 
  - The 💾 list item gains an **⚠ badge** (or similar visual indicator).
  - The **Lock/Unlock button is hidden** and replaced in the same position by an **"Update"** (German: "Aktualisieren") button. The location icon (💾 / 🌐) is incorporated directly into this button.
  - Clicking Update shows a **popup message (InfoDialog with Cancel)** (InfoDialog or equivalent) that explains exactly what will happen: *"Your local copy will be removed. The updated shared version will be active the next time you start the add-in. Restart Inventor to apply."* — OK / Cancel buttons.
  - On OK: the AppData copy is deleted. The UNC version is now the live copy for this session; it becomes the only copy on next startup.
- **Inventor restart required:** the CatalogStore and CapabilityStore are loaded once at `StandardAddInServer.Activate()`. There is no dynamic in-session reload. The popup must make this clear.
- Comparison uses `LastUpdated` (DateTime, UTC) — bumped on every `Save()` call. No Version counter. Manual JSON edits outside the add-in are not tracked; accepted limitation.

**Effect of being locked on the right panel:**

| Tab          | Mechanism                                                                                                                                           | Visual effect                                                                                                                                                                                                                                                          |
|--------------|-----------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Catalogs     | DataGrid `IsReadOnly={IsSelectedCatalogLocked}` only — `IsEnabled` **is NOT used for lock state**                                                           | Cells not editable; DataGrid remains scrollable. Bottom action bar buttons (Add/Remove Row, Add/Remove Column) stay disabled via `IsSelectedCatalogEditable`.                                                                                                            |
| Capabilities | Transparent hit-test overlay Grid (`Panel.ZIndex=100`) scoped to the **groups editing ScrollViewer (Row 1) only** — it covers the TwoWay-bound card-parameter editors, which are not command-gated. The bottom toolbar, Cards palette and Basic Logics buttons are `RelayCommand`s gated by `IsSelectedCapSetEditable` / `HasEditableActiveGroup`, so they disable themselves when locked (the overlay does not need to cover them). | Card editors appear normal but absorb clicks silently; scroll re-routed to `CapabilitiesScrollViewer` via `PreviewMouseWheel`. The **single ℹ Card-Help button in the bottom toolbar stays clickable while locked** (help is read-only — there is no separate lock-strip ℹ button). Lock/Unlock strip in the left panel is always accessible. |

All editing command `CanExecute` predicates check `IsSelectedCatalogEditable` / `IsSelectedCapSetEditable`. **Export** is always available regardless of lock state.

**Filename sanitization (**`SanitizeName`**):**

The file name written to disk is derived from the user-visible catalog / capability set name. Rules:

| Rule                                                                        | State                                 |
|-----------------------------------------------------------------------------|---------------------------------------|
| Strip Windows-illegal chars (`\ / : * ? " < > |`)                           | ✓ via `Path.GetInvalidFileNameChars()` |
| Replace spaces with `_`                                                     | ✓ implemented (U5)                    |
| Strip non-ASCII / special symbols (umlauts, accents, `@#&+~()[]{}` etc.)    | ✓ implemented (U5)                    |
| Max 60 chars                                                                | ✓ truncated, trimmed                  |
| Fallback when result is empty                                               | ✓ uses `Id`                           |

The user-visible **name** (shown in the list) is unaffected — only the derived filename is sanitized. A catalog called `"IZ Spezis (2026)"` might be saved as `IZ_Spezis_2026.catalog.json`.

**Delete behavior vs. lock:**

- Local AppData files: `Delete()` removes the physical file and the in-memory entry.
- UNC / distribution files: `Delete()` removes the in-memory entry only — the physical file is never deleted by the add-in.

**Import behavior:**

- Always imports as local + unlocked: `IsLocked = false`, `IsOnUncPath = false`. ID collision → new ID assigned. File saved to AppData.

**Property flags summary:**

| Property    | Persisted                        | Meaning                                                                      |
|-------------|----------------------------------|------------------------------------------------------------------------------|
| `IsLocked`    | Yes (JSON)                       | User-set edit gate; toggleable via Lock/Unlock button for local files        |
| `IsOnUncPath` | No (`[JsonIgnore]`)                | Runtime: file path starts with `\\`; forces locked; Unlock migrates to AppData |
| `IsShared`    | Legacy only (`WhenWritingDefault`) | Read from old JSON files; no UI meaning; never written by current code       |

---

**Action buttons** (WrapPanel, very bottom of left panel — same for both tabs):
`New` | `✎ Rename` | `× Delete` | `Import` | `Export`

---

#### Right panel — Catalog view

Visible when **Catalogs** tab is active. The Catalog Editor is designed to mimic standard spreadsheet behavior — see **Section 6.10** for the governing design principle.

**Spreadsheet grid (DataGrid):**

- Columns built dynamically by code-behind (not auto-generated).
- Cell-level selection (`SelectionUnit="Cell"`); extended multi-cell selection supported.
- User can drag columns to reorder their display order (`CanUserReorderColumns="True"`).
- Sorting managed by custom header-click handler — not WPF default sort.
- Alternating row colors (`AlternationCount="2"` — `CheckupRowBackground0/1`).
- Horizontal grid lines only (`GridLinesVisibility="Horizontal"`).
- Row headers: 1-based row numbers (36px wide, `CheckupGroupHeaderBackground`).
- Horizontal scrollbar always visible; vertical scrollbar auto.
- Grid is read-only (`IsReadOnly=true`) when catalog is locked; remains scrollable — `IsEnabled` is not used for lock state.

**Column headers:**

- Top line: letter label (small, secondary color) + sort arrow (accent color) + optional role badge (colored pill, bold white text, hidden when role = None).
- Bottom line: user-editable column label (SemiBold, ellipsis trim, tooltip = full label).
- Single-click = sort ascending/descending (toggled); double-click = edit label; right-click = context menu.

**Cell right-click context menu** (shown on data cell right-click — built programmatically, not in XAML, due to Inventor WPF host popup isolation):

**When catalog is unlocked (editable):**

| Item                                   | Shortcut | Language key |
|----------------------------------------|----------|-------------|
| Copy                                   | Ctrl+C   | `CtxMenu_Copy` |
| Cut                                    | Ctrl+X   | `CtxMenu_Cut` |
| Paste                                  | Ctrl+V   | `CtxMenu_Paste` |
| Fill Down → Same Value                 | Ctrl+D   | `CtxMenu_FillSameValue` |
| Fill Down → Series (auto-detect step)  | —        | `CtxMenu_FillSeries` |
| Fill Right → Same Value                | Ctrl+R   | `CtxMenu_FillSameValue` |
| Fill Right → Series (auto-detect step) | —        | `CtxMenu_FillSeries` |
| Clear Contents                         | Del      | `CtxMenu_ClearContents` |
| *(separator)*                          |          |             |
| Insert Row Above                       | —        | `CtxMenu_InsertAbove` |
| Insert Row Below                       | —        | `CtxMenu_InsertBelow` |
| Delete Row                             | —        | `CtxMenu_DeleteRow` |
| ▲ Move Row Up                          | —        | `CtxMenu_MoveRowUp` |
| ▼ Move Row Down                        | —        | `CtxMenu_MoveRowDown` |
| *(separator)*                          |          |             |
| Insert Column Left                     | —        | `CtxMenu_InsertColLeft` |
| Insert Column Right                    | —        | `CtxMenu_InsertColRight` |
| Delete Column                          | —        | `CtxMenu_DeleteCol` |
| *(separator)*                          |          |             |
| Sort A → Z                             | —        | `CtxMenu_SortAZ` |
| Sort Z → A                             | —        | `CtxMenu_SortZA` |

Move Row Up enabled only when right-clicked row is not the first; Move Row Down enabled only when not the last. After move: active sort arrows cleared (manual reorder invalidates sort state); working copy marked dirty; `UnselectAllCells()` called.

**When catalog is locked (read-only):**

No context menu is shown. Single right-click immediately copies the right-clicked cell's value to clipboard silently (same behavior as right-click on a Value Field in the main window). No menu, no confirmation.

**Column header right-click context menu** (separate from cell context menu):

- **Edit Label…** — always shown; opens inline label edit.
- *(separator — only if column has a role AND ≥2 columns share the same role type)*
- **⬆ Move Role Up** — shifts this column's role index down (enabled only if a lower-index sibling exists).
- **⬇ Move Role Down** — shifts this column's role index up (enabled only if a higher-index sibling exists).

**Keyboard shortcuts:**

| Key          | Action                                                            | Locked mode |
|--------------|-------------------------------------------------------------------|-------------|
| Ctrl+C       | Copy selected cells (tab-separated, Excel-compatible TSV)         | ✅ allowed  |
| Ctrl+X       | Cut (copy + clear contents)                                       | ❌ blocked  |
| Ctrl+V       | Paste from clipboard — expands multi-row TSV into grid            | ❌ blocked  |
| Del          | Clear contents of selected cells (does not delete rows)           | ❌ blocked  |
| Ctrl+D       | Fill Down — same value (top cell of each column copied down)      | ❌ blocked  |
| Ctrl+R       | Fill Right — same value (leftmost cell of each row copied right)  | ❌ blocked  |
| Ctrl+A       | Select all cells                                                  | ✅ allowed  |
| Ctrl+Home    | Jump to first cell (row 0, col 0)                                 | ✅ allowed  |
| Ctrl+End     | Jump to last cell (last row, last col)                            | ✅ allowed  |
| Home         | Jump to first cell in current row                                 | ✅ allowed  |
| End          | Jump to last cell in current row                                  | ✅ allowed  |
| Ctrl+F       | Open / close Find bar                                             | ✅ allowed  |
| Escape       | Close Find bar (if open); otherwise no effect at DataGrid level   | ✅ allowed  |
| Arrow keys   | Navigate between cells (WPF DataGrid native)                      | ✅ allowed  |
| F2           | Enter edit mode (WPF DataGrid native)                             | ❌ blocked by IsReadOnly |
| Enter / Tab  | Confirm edit + move (WPF DataGrid native)                         | navigation only in locked |

**Find bar (F-A inline style):**

A collapsible bar that slides in directly above the DataGrid column headers (within the right-panel Grid as an `Auto`-height row). Opened by Ctrl+F; closed by Escape, Ctrl+F again, or the `×` button.

- **Layout (left to right):** 🔍 label · search `TextBox` (expands to fill) · match counter `TextBlock` (`"3 / 12"` or `"Kein Treffer"`) · `▲` Prev button · `▼` Next button · `×` Close button.
- **Height:** 32 px. Background `CheckupGroupHeaderBackground`; 1 px bottom border `CheckupSeparator`.
- **Live search:** on every `TextChanged`, scans all entries across all columns (case-insensitive contains); builds `_findMatches` list of `(rowIndex, colIndex)` pairs; resets `_findIndex = 0`; scrolls DataGrid to first match and selects it; updates counter.
- **Navigation:** Enter / `▼` = next match; Shift+Enter / `▲` = previous match. Both wrap around.
- **Match display:** DataGrid scrolls to the matching row/cell; `CurrentCell` set to the match. Counter shows position ("X / Y"). When no match: counter shows "Kein Treffer" / "No match" in `CheckupErrorText` color.
- **Escape priority:** Escape closes the Find bar first (before the window-level Escape handler that closes the window). Second Escape then closes the window normally.
- **Language keys:** `FindBar_Counter` (format `"{0} / {1}"`), `FindBar_NoMatch` — in DE.json + EN.json + DE.xaml, both projects.

**Fill series behaviour:** Series fill (auto-detect) infers step from the first two selected cells; falls back to +1 if detection is impossible. Only available via context menu.

**Sort behaviour:**

- **Header single-click:** sorts ALL rows ascending/descending (toggled). Does **not** mark the catalog as dirty — treated as a temporary view operation.
- **Context menu Sort A→Z / Z→A:** if the current selection does not cover all rows, a dialog offers "sort selected rows only" or "expand to full table". Always marks dirty.

**Working-copy edit model:**

- All edits (cell values, column changes, row add/remove) are applied to a deep-copy `_workingCopy` — the original catalog object is untouched.
- Original is overwritten only when the user clicks **Save**.
- Switching to a different catalog or closing the window while dirty shows a **Save / Discard / Cancel** prompt.
- Export also uses the working copy — unsaved in-progress changes are included in the exported file.

**Catalog bottom action bar:**

| Position  | Controls                                                                                                                |
|-----------|-------------------------------------------------------------------------------------------------------------------------|
| Far left  | **Add Row** · **Remove Row** ∣ **Add Column** · **Remove Column** ∣ Role picker ComboBox (assigns role to the selected column) · ℹ info |
| Far right | Unsaved indicator (red text, visible when dirty) · **Save** button                                                          |

**Column role system** — each catalog column can be assigned one role via the Role picker. Multiple columns of the same role type get auto-indexed (e.g. SRT1, SRT2…). Role badge shown in the column header as a colored pill.

| Badge | Role             | Description                                                                        |
|-------|------------------|------------------------------------------------------------------------------------|
| —     | None             | No role; helper column or internal identifier                                      |
| PRI   | PrimaryDisplay   | Short form (field 1) — the value written to the target field on selection          |
| SEC   | SecondaryDisplay | Long form (field 2) — shown as secondary text in picker; written by Sync card      |
| TAB   | TabId            | Tab identifier — each unique value becomes a tab. A comma-separated cell = **multi-tab membership** (Task #40, planned): a row listing `A, B` appears under both tabs. |
| GRP   | GroupId          | **Sub-group header within a tab** — entries sharing a GRP value are grouped under a header inside the tab's item list. (NOT the tab title — corrected 2026-06-23.) |
| SRT   | SortKey          | Sorts entries within a group; multiple columns → SRT1, SRT2…                       |
| GST   | GroupSortKey     | Sorts groups within a tab; multiple → GST1, GST2…                                  |
| TST   | TabSortKey       | Sorts tabs in the picker; multiple → TST1, TST2…                                   |
| AUX   | Auxiliary        | Auxiliary data (e.g. tooltip text) — visible in picker but never written to fields |

**Placeholder state:** No catalog selected → right panel shows centered italic hint text.

**Scrolling — Catalog view:**

- Mouse-wheel scrollable.
- Supports keyboard/cursor navigation (arrow keys, Tab, Enter) matching spreadsheet conventions.
- Horizontal scrollbar always visible; vertical scrollbar auto.

---

#### Right panel — Capabilities view

Visible when **Capabilities** tab is active. Two-column Grid: center content area + Basic Logics panel on the right.

**Placeholder states:**

- No Capability Set selected → centered italic hint text shown in the right panel.
- Capability Set selected but has no Groups → centered italic hint text shown in the groups area.

**Groups area (scrollable, center):**

Each Capability Set contains zero or more **Groups**. Each Group represents one Logic Row (`SPECIAL:LOGIC:`) that will appear in the main window's Field Selector as an `S:` entry.

**Creating and managing groups:**

- **Add Group** button creates an empty group (no Cards or Basic Logics).
- User gives the Group a label in the **Group Name** field — this same label appears as the `S:` entry in the Field Selector and as the Row label in the main window.
- Groups can be reordered by drag-and-drop (drag handle in group header) or via the bottom bar ▲ / ▼ buttons.
- Groups can be moved between positions freely; cards inside can be dragged between groups if multiple groups exist.

**Group color:**

- Each Group has an accent color derived **deterministically from its** `Id` (`_palette[Math.Abs((group.Id ?? "").GetHashCode()) % _palette.Length]`) — used purely for user visibility. The 8-color palette (defined in `CatalogBuilderViewModel.BuildPalette()`):

  | \# | Hex     | Name   |
  |---|---------|--------|
  | 0 | `#5BA3DE` | blue   |
  | 1 | `#6CB87A` | green  |
  | 2 | `#C8985A` | amber  |
  | 3 | `#BF6FC8` | purple |
  | 4 | `#DE6A6A` | red    |
  | 5 | `#5BC8C8` | teal   |
  | 6 | `#DEB85A` | gold   |
  | 7 | `#9ADE5A` | lime   |
- The color appears as the group's frame border (2px when active, 1px separator color when inactive).
- Cards and Basic Logics inside the group share the same accent color as a 4px left-edge strip.
- When a Group or any item within it is selected (active), the accent color highlights the selection visually — until the user clicks elsewhere. Follows the overall UX design of the add-in.
- When another group is active, inactive groups render at 45% opacity.
- The accent color is **NOT persisted in the capability JSON** — it is recomputed from `group.Id` at runtime every time the capability is loaded. Two devices with the same capability file will always see the same color for each group.

**Group numbering and ordering:**

Groups are displayed in a single ordered list. Numbers are sequential 1…N top to bottom; number = visual position = evaluation order. Groups are draggable to reorder.

> Expert Mode groups form a separate lower section, visually divided from Normal groups.

**Group container:**

- **Group header row:** order number · drag handle · **Group Name** TextBox (red border when empty; italic placeholder hint when empty) · **⚡ Expert toggle** · **× delete** button · **"Target Field:"** label · **Target Field** ComboBox — uses the **same enhanced popup design as the main window Field Selector**, with these differences: (1) Zone 2 (Add Row / Remove Row) is **not shown**; (2) Sticky zone is **shared** — the same `PinnedFields` Registry key is used in both windows (pinning a field in one window pins it in the other); (3) **Circle safety:** evaluation-time cycle detection only (`ExpertTopoSort` / `#CIRC!`). No pre-selection greying out or blocking — confirmed decision.

  **Group header row — column layout (8-column Grid, expanded state):**

  | Col | Content | Width spec | Notes |
  |-----|---------|-----------|-------|
  | 0 | Chevron collapse button | `Auto` | 20×20 px |
  | 1 | Order number `#` | `Auto` | min 18 px |
  | 2 | Drag handle | `Auto` | 10 px wide tile |
  | 3 | Group Name TextBox (expanded) / Name+Pills (collapsed) | `*` `MinWidth="100"` | **Stretches to fill all remaining space** — pushes right-side controls to the far right |
  | 4 | ⚡ Expert toggle | `Auto` | Hidden when collapsed |
  | 5 | × Delete button | `Auto` | Hidden when collapsed |
  | 6 | Target Field section (label + dropdown) | `Auto` `MinWidth="0"` | Auto-sizes to content — **never steals space from col 3**. Container DockPanel: `MinWidth="160"`, `HorizontalAlignment="Left"` (prevents .NET 4.8 auto-column stretch bug). Dropdown Grid has no fixed `MinWidth` — sizes to label text + selected value. Popup: `MinWidth="150"` floor. |
  | 7 | ▲▼⧉× horizontal bar | `Auto` | Collapsed state only |

  **Invariant:** Drag handle and Group Name are always leftmost; Expert/Remove/Target Field are always rightmost. The `Width="*"` on col 3 is the sole mechanism that achieves this — do not change it to `Auto` or a fixed `2*` ratio.
- **Cards / Basic Logics list** below the header: one item per row. The list has no *vertical* scrollbar — the outer Capabilities `ScrollViewer` scrolls all groups vertically. **(T44)** each card editor area does have its own *horizontal* scrollbar, which appears only when the card is narrower than its field-area `MinWidth` floor; see the Card row layout + **Scrolling** notes below.
- Items can be dragged within the group or to another group.

> Groups, Cards, and Basic Logics are each individually collapsible.

**Card / Basic Logic row layout:**

*Expanded state:*

- Far left: drag handle — reorders within or between groups.
- Far right: **▲** · **▼** · **⧉** · **×** buttons (move up, move down, duplicate, remove) — **vertically stacked**.
- Left edge: 4px colored accent strip (group's accent color).
- Bottom-right: colored **type badge pill** (rounded corners, bold white text) — identifies the card/function type. Color per type via `CardTypeToBrushConverter`.
- Center: **Enabled** checkbox (`Dock=Left`, top-aligned) + the card's field area. **(T44)** the field area — all card-type-specific controls *and* the companion/partner field picker — is a single responsive `WrapPanel` that fills the space right of the checkbox: fields wrap to the available width, and each `label+field` pair is wrapped in its own non-breaking `StackPanel` so a label never separates from its control. Because the picker shares this one `WrapPanel`, the companion field wraps *below* the type fields when narrow instead of overflowing to the right. Horizontal-scroll / height behavior: see **Scrolling** below.

**Card-type-specific controls:**

| Card type         | Controls shown                                                                           |
|-------------------|------------------------------------------------------------------------------------------|
| Dropdown / Button | Catalog picker · SecRole · TooltipRole                                                   |
| Search            | Catalog picker · Search Roles (text) · SecRole · TooltipRole                             |
| Link              | Partner Field ComboBox †                                                                 |
| Sync              | CompanionRole · Companion Field ComboBox †                                               |
| MultiPick         | PrimaryTokenSeparator · Companion Field † · CompanionRole · CompanionTokenSeparator      |
| PairTransform     | SourceTokenSep · LookupRole · OutputRole · OutputTokenSep · Companion Field †            |
| Compose           | Catalog picker · LookupRole · OutputRole · ItemPrefixes · ItemSeparator · CollapseWhenEqual · CollapsedPrefix · DropEmptyOutputs · MaxItems · OnUnknownToken · Companion Field † · *(Split Mode)* SourceSeparator · TokenOutputSeparator · ComposeFallbackCatalogId · OnDirectMatch · OutputMode · AppendSeparator |
| PrefixSuffix      | Prefix text box · Suffix text box · Mode toggle (Add / Remove)                           |
| Sort              | Catalog picker · LookupRole (default PRI) · TokenSeparator (default `"-"`) · Invert toggle |

† All field-picker ComboBoxes marked † use the same enhanced popup design as the main window Field Selector — Zone 2 (Add/Remove Row) not shown; shared `PinnedFields` sticky zone; search; collapsible groups. Circle safety preserved (evaluation-time `#CIRC!` detection).

> **Note — CatalogBuilder card-editor configuration dropdowns** (CatalogId picker, SecRole, TooltipRole, CompanionRole, SearchRoles in the card editor panel) are **not** the same as the field-picker ComboBoxes above. They use theme-styled WPF ComboBoxes with `AllowsTransparency="True"` on the popup. SearchRoles is a plain TextBox (comma-separated roles). These are editor-time config controls; they are NOT subject to the §5.13 runtime value-entry spec.

**Compose card behaviour (`CardEngine.BuildComposeValue` / `GetComposeWrites`).** The no-separator sibling of PairTransform: it expands a single field value that concatenates several catalog codes **with no delimiter** into framed text written to the Companion Field, on inline-edit Apply. PairTransform splits on a literal separator; Compose splits by **longest match** against the `LookupRole` column, so it handles mixed-length codes a fixed split cannot.

Algorithm: (1) build the candidate set = distinct non-empty `LookupRole` values of the card's catalog; (2) walk the source left→right, at each position taking the **longest** candidate that prefixes the remainder (case-insensitive), bounded by `MaxItems` (0 = unlimited); (3) look up each code's `OutputRole`; (4) if `DropEmptyOutputs` (default true), drop codes whose output is empty; (5) frame — if `CollapseWhenEqual` and all codes are identical → `CollapsedPrefix + output`, else per item `ItemPrefixes[i] + output` joined by `ItemSeparator`; (6) write to `CompanionFieldKey`.

`OnUnknownToken` governs input that does not fully tokenise: `skip` (default — use the codes parsed so far), `keepRaw` (write the source unchanged), `passthrough` (write nothing — leave the companion for manual entry). A code is bounded to the card's configured catalog/column, so cross-domain collisions cannot occur. All framing is parameterised, so domain-specific rules (e.g. a two-sided `D1/D2` material) are just one configuration — the card itself carries no domain knowledge.

**Compose card — Split Mode (Task #42).** An optional extension to the Compose card that enables it to process separator-delimited source fields (e.g. SPEZIFIK1 `60-pur-g2g2-fngg`) where some tokens are direct catalog entries and others are packed sub-codes requiring sub-tokenization. Without Split Mode the base Compose card cannot process such fields because the separator character (`-`) is not a catalog code and halts `LongestMatchTokenize` at the first occurrence.

Six new optional params (all absent = base Compose behaviour unchanged):

| Param | Default | Meaning |
|---|---|---|
| `SourceSeparator` | (absent) | Enables Split Mode. Source field is split on this separator; each outer token is processed independently. |
| `TokenOutputSeparator` | `, ` | Separator used to join per-token outputs in the result string. |
| `ComposeFallbackCatalogId` | (same as card) | Catalog used for sub-tokenization. Use a domain-restricted sub-catalog to avoid longer-code collisions (e.g. Merkmal codes that share PRI values with feature stacks). |
| `OnDirectMatch` | `include` | `include` — direct-match tokens contribute their SEC output (replaces PairTransform). `skip` — direct-match tokens are omitted; only sub-expanded tokens appear in the output. Use `skip` when PairTransform already handles direct matches and this card adds only sub-expansions. |
| `OutputMode` | `replace` | `replace` — write result to CompanionFieldKey (normal). `append` — read the current CompanionFieldKey value; if non-empty prepend it with AppendSeparator; write the combined result. Allows multiple Compose cards to contribute to one companion field in sequence. |
| `AppendSeparator` | `, ` | Separator inserted between the existing companion value and the new output when `OutputMode=append` and the companion is non-empty. |

Split Mode algorithm for each outer token: (1) try a direct PRI→SEC lookup in the primary catalog; (2a) if found: `OnDirectMatch=include` → emit SEC, `skip` → omit; (2b) if not found: run `LongestMatchTokenize` against `ComposeFallbackCatalogId` (or primary catalog if absent); (3) apply existing Compose framing (ItemPrefixes, CollapseWhenEqual, DropEmptyOutputs, etc.) to the sub-codes; (4) `OnUnknownToken` governs tokens that fail sub-tokenization entirely. Collect all non-empty per-token outputs → join with `TokenOutputSeparator` → apply `OutputMode` before writing.

**Typical two-card setup for SPEZIFIK1 → SPEZIFIK2 expansion (with material doubles and feature stacks):**

Three cards in the SPECIAL:LOGIC: group, executed top-to-bottom:

1. **PairTransform** (unchanged): splits SPEZIFIK1 on `-`; direct PRI→SEC lookup; writes SPEZIFIK2 = `ISO 100, PUR-Schaum, gelocht, …` (all direct-match tokens).
2. **Material Compose** (`SourceSeparator="-"`, `OnDirectMatch=skip`, `OutputMode=append`, `ComposeFallbackCatalogId` = material-only sub-catalog, D1/D2 framing): for each SPEZIFIK1 token — skips direct matches (already in SPEZIFIK2); sub-tokenizes packed material codes (`g2g2`→`g2`+`g2`, `l5a1`→`l5`+`a1`, `hlzg2`→`hlz`+`g2`) against the material sub-catalog; applies D1/D2 framing; appends result to SPEZIFIK2.
3. **Feature Compose** (`SourceSeparator="-"`, `OnDirectMatch=skip`, `OutputMode=append`, `ComposeFallbackCatalogId` = feature-combinations sub-catalog, comma-join framing): sub-tokenizes packed feature stacks against the feature-combinations sub-catalog (see below); appends result to SPEZIFIK2.

**Feature-combinations sub-catalog.** The ILKAsys L-R and O-U feature axes share PRI codes (`fn`, `nf`, `ff`, `gg`, `nn`, …) with different SEC meanings per axis. A flat `LongestMatchTokenize` against a mixed L-R+O-U catalog always returns the first-matching axis for each 2-char code — producing wrong SEC values for the second positional slot. The correct approach is a **pre-computed feature-combinations catalog**: each used 4-char feature stack (`fnnn`, `fngg`, `ggnn`, `ffnn`, …) appears as a direct 4-char PRI entry whose SEC value is the correctly ordered combination of its L-R and O-U long forms (with empty O-U axis dropped). `LongestMatchTokenize` finds the 4-char entry as a whole-token match, bypassing any axis-ordering ambiguity. The catalog is a pure **adjustment** (no code change): derive entries from the Cartesian product of the 10 L-R × 10 O-U codes; skip combinations whose combined SEC is empty (`nnnn`); 100 entries maximum, typically far fewer in practice.

**Capability sub-catalogs (adjustments, no rebuild):**
- `001-test-spezifik-material-katalog` — Material (124) + Holz_Material (23) entries only; excludes all Merkmal/Blechkante categories to prevent longer-code sub-tokenization collisions.
- `001-test-spezifik-feature-combos-katalog` — pre-computed 4-char feature stacks as direct PRI entries; SEC = L-R long + `, ` + O-U long (empty axis dropped); covers all combinations used in the real data.

**Compose card editor — multi-row layout.** With Split Mode params the Compose card has more params than fit comfortably in a single horizontal row. The card editor panel therefore renders the Compose params as up to **3 explicit horizontal rows** inside a `StackPanel Orientation="Vertical"` (replacing the single flat `WrapPanel` from T41):

| Row | Visibility | Controls |
|---|---|---|
| 1 | Always (Compose) | LookupRole · OutputRole · Companion Field picker |
| 2 | Always (Compose) | ItemPrefixes · ItemSeparator · CollapseWhenEqual · CollapsedPrefix · DropEmptyOutputs · MaxItems · OnUnknownToken |
| 3 | `IsComposeSplitMode` only | SourceSeparator · TokenOutputSeparator · ComposeFallbackCatalogId · OnDirectMatch · OutputMode · AppendSeparator |

`IsComposeSplitMode` is a computed read-only property on the card's `CardItemViewModel`: `!string.IsNullOrEmpty(SourceSeparator)`. Row 3 is hidden when SourceSeparator is empty, so a base Compose card (no Split Mode) stays at 2 rows. These 3 rows are the explicit *logical* grouping of the Compose params. **(T44 update)** the original claim that "no other card types are affected" no longer holds: as of T44 *every* card type wraps its fields responsively (see Card row layout + Scrolling), and within every card — including each of these Compose rows — each `label+field` pair is wrapped in a non-breaking `StackPanel` so pairs never split across a wrap (the standalone Compose checkboxes stay as their own flow items).

**Capabilities bottom bar** (always visible, below groups):

- Far left: **Add Group** button.
- Far right: ℹ (card help) · **▲** · **▼** · **⧉** · **×** — act on the currently active/selected object (Group, Card, or Basic Logic). Context-sensitive: if a Group is active, they move/duplicate/remove the Group; if a Card or Basic Logic is active, they move/duplicate/remove that item.
- Note: **▲▼⧉×** buttons on individual Card/Basic Logic rows act only on that specific item and are provided as a shortcut — they do not affect the group.

**Capabilities save model — immediate write (contrast with Catalog tab working-copy):**

- Every change in the Capabilities panel (add/remove/reorder Groups, add/remove/edit Cards and Basic Logics) is **written to the capability file immediately** via `CapabilityStore.Save()` — there is no "Save" button and no dirty/discard cycle.
- This is intentional: capability sets are configuration data that should always reflect the current on-screen state. If the user closes the window, no unsaved work is lost.
- **Contrast:** the Catalog tab uses a `_workingCopy` model — edits are buffered, and only committed on "Save" click (with Save/Discard/Cancel prompt when switching away with unsaved changes). See Section 5.8 Catalog DataGrid working-copy note.

**Scrolling — Capabilities view:**

- Entire groups area is mouse-wheel scrollable — the outer `CapabilitiesScrollViewer` handles the wheel for the whole groups / cards / Basic-Logics list via `PreviewMouseWheel` (scrolls vertically and marks the event handled). Because cards have no vertical scroll of their own, the wheel over any card scrolls this outer list.
- Vertical scrollbar: `Auto` (appears when groups overflow height).
- Outer horizontal scrollbar: `Auto` — **kept** (do **not** set `HorizontalScrollBarVisibility="Disabled"` — that silently clips content). In practice it rarely engages now: each group is capped to the viewport width (group-root `MaxWidth` bound to the ScrollViewer `ViewportWidth`, `MinWidth="320"`) and each card bounds its own width (next bullet), so horizontal overflow is handled *per card* rather than by widening the whole groups area. The group header still relies on `Width="*"` on the group-label column to distribute space (see header invariant).
- **(T44) Per-card responsive layout — Card editor area.** Each card's field area **wraps** to the available width. Mechanism: the field area lives in a per-card `ScrollViewer` (`VerticalScrollBarVisibility="Disabled"`, `HorizontalScrollBarVisibility="Auto"`) whose inner panel sets `Width="{Binding ViewportWidth, RelativeSource={RelativeSource AncestorType={x:Type ScrollViewer}}}"` together with `MinWidth="700"`. Effective width = `max(700, viewport)`: when the viewport is ≥ 700 px the panel wraps to the viewport (no horizontal scrollbar); once the viewport drops below 700 px the panel stops shrinking and the **card shows its own horizontal scrollbar**. `MinWidth` is therefore the single knob — it sets both the tallest wrapped height and the window width at which the per-card h-scrollbar appears. There is **no** per-card vertical scroll (the card grows to its wrapped height and the outer list scrolls). The type pill stays outside this scroll (card bottom); the card-collapse style is unaffected. Default window minimum is 600 px (§5.11), so at the smallest window the per-card scrollbar is engaged for content wider than ~510 px.

**Basic Logics panel (far right, collapsible):**

- Custom `Expander` with a fully custom `ControlTemplate` (`BasicLogicsExpander` style). `ExpandDirection` property is NOT set — layout direction is defined entirely by the template Grid columns.
- Template layout: two-column Grid — Col 0 `Width="Auto"` (content area, left), Col 1 `Width="32"` (toggle strip, right).
- **Toggle strip is ALWAYS on the far-right edge.** It never moves. The toggle strip is Col 1 (32px) of the template Grid.
- **Content area expands to the LEFT** of the toggle strip (Col 0, Auto). When collapsed, `ContentSite.Visibility="Collapsed"` so the Auto column shrinks to 0.
- **Window extends to the RIGHT when BL panel opens** (not squeezing the main content). Code-behind (`CatalogBuilderWindow.xaml.cs`) measures `BLExpander.ActualWidth - 32` after the layout pass (`DispatcherPriority.Loaded`) and adds that amount to `Window.Width`. When panel closes, `Window.Width` is restored.
- Maximized guard: if `WindowState != Normal` the resize is skipped — panel opens within the existing window width.
- Toggle strip shows rotated label "Basic Logics" (bottom-to-top) + chevron. Chevron `<` (Data `M 5 0 L 0 5 L 5 10`) when collapsed, `>` (Data `M 0 0 L 5 5 L 0 10`) when expanded.
- ContentSite `BorderThickness="1,0,0,0"` (left border separating content from toggle strip).
- `IsExpanded` bound `TwoWay` to `IsBasicLogicsPanelOpen` (VM property — persisted to registry).
- Content: a **WrapPanel (Orientation=Vertical)** of Basic Logic template buttons. `MaxHeight` bound to `ActualHeight` of ancestor Expander so buttons flow into a second column when the panel height is insufficient.
- Each BL button has a syntax-hint tooltip. Clicking adds a `BasicLogic` card to the active group with the function skeleton pre-filled.
- **Invariant: the WrapPanel must contain exactly one button per function listed in the `FUNCTION(...)` row of the FormulaEngine syntax table (§5.6). The `InfoPanelBuilder.BuildCardHelp()` BL section must list the same functions in the same order.** When FormulaEngine gains a new function, all three (WrapPanel buttons, VM commands, InfoPanelBuilder entries) must be updated simultaneously. Button order = TDD §5.6 function order: CONCATENATE → IF/ELSE → LOOKUP → FORMAT → ROUND → VALUE → STR → EQ → NE → LT → GT → LTE → GTE → AND → OR → NOT → JOIN → LEFT → RIGHT → MID → TRIM → UPPER → LOWER → REPLACE → ABS → LEN → CONTAINS → STARTSWITH → ENDSWITH → ISEMPTY → DEFAULT.
- **Factory default:** `IsBasicLogicsPanelOpen = false` (closed) — registry key absent → `LoadCatalogBuilderBasicLogicsPanel()` returns `false`.

**Cards palette (bottom of the Capabilities view, vertically collapsible):**

The Cards palette sits at the very bottom of the center content area, below the groups ScrollViewer. It is a three-row strip (always anchored to the bottom):

| Row              | Visibility                  | Content                                                                                                                                           |
|------------------|-----------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 — Card buttons | Collapsible (folds UP)      | Horizontal WrapPanel of card-type buttons: **Button · Dropdown · Link · PairTransform · Compose · Prefix/Suffix · Search · MultiPick · Sort · Sync** (BasicLogic is NOT in this palette — its buttons live in the Basic Logics panel on the right) |
| 2 — Toggle strip | Always visible              | Full-width button: centered label "Cards" (SemiBold) + chevron on right edge; ∧ when collapsed, ∨ when expanded. Bound to `ToggleCardPanelCommand`. |
| 3 — Toolbar      | Always visible              | Far left: **+ Add Group**. Far right: **ℹ · ▲ · ▼ · ⧉ · ×** (act on active Group/Card/Basic Logic — see bottom bar above).                                |

- **Fold direction:** card buttons (Row 1) sit **above** the toggle strip (Row 2). The toggle strip is ALWAYS BELOW the card buttons — it stays right above the Toolbar row. When expanded, card buttons appear above the toggle strip (between the groups area and the toggle strip). When collapsed, card buttons are hidden via `Visibility` — the toggle strip stays in place (above Toolbar), and the groups area expands downward to reclaim space.
- **State persisted:** `IsCardPanelOpen` (VM property, persisted to registry).
- **Factory default:** `IsCardPanelOpen = true` (open) — registry key absent → `LoadCatalogBuilderCardPanel()` returns `true`.
- **Single left-click** on any card button adds that card type to the currently active Group.

**Cards vs Basic Logics — conceptual distinction:**

- **Basic Logics** represent spreadsheet-style formula functions. Purely computational — no interactive UI. Full function set available: see `FormulaEngine` row in Section 3. Notable: `LOOKUP(key, searchCol, returnCol [, catalogName])` makes Basic Logics catalog-capable without a Lookup card. They intercept or compute field values using formula expressions.
- **Cards** add higher-level functionality: some provide interactive visual elements (Dropdown, Button, Search, MultiPick, PairTransform); some orchestrate complex multi-field behaviour (Sync, Link). Cards typically require a Catalog as a data source.
- The **spatial split is intentional**: Basic Logics live in the collapsible panel on the far right; Cards are inserted into the Group body. This physical separation mirrors the conceptual separation.
- **BasicLogic card (R1.1):** After R1 removed the Formula card, BasicLogics were restored as a first-class card type. Each BL is stored as `CapabilityCard { Type = "BasicLogic" }` with `Params["Formula"]` and optional `Params["FormulaTargetFieldKey"]`. No dedicated model class — `CapabilityCard` is the common type. Formula card (distinct from BasicLogic) was removed.
  > ⚠ **Case-sensitive Params keys:** `CapabilityCard.Params` is a `Dictionary<string, string>` with case-sensitive lookup (`TryGetValue`). When hand-editing `.capability.json` files the key **must** be `"Formula"` (capital F) — NOT `"formula"`. A lowercase key is silently ignored: the formula field appears empty in the UI, the formula does not execute, and `blOwnsWrite` is incorrectly set to `false` (causing raw user input to bypass the BL formula and write directly to the target field). Same rule applies to `"FormulaTargetFieldKey"`. The addin-generated JSON always writes the correct casing; only hand-edited files are at risk.

---

### 5.9 Info Buttons

Both the main window and the Logics-Constructor window have a dedicated **Info** button. Same visual style in both windows. Each opens its own separate Info Window following all unified window handling rules (Section 5.11).

| Info entry point | Context key | Title key | Default size | Content |
|-----------------|-------------|-----------|-------------|---------|
| Main window ℹ button | `"MainAddin"` | `Win_Title_CheckupInfo` | 520 × 480 | Quick Guide: object selection, inline edit, formula (ƒx) editing, right-click copy, drag reorder, preset management, auto-refresh |
| Role Help ℹ button (catalog column editor) | `"RoleHelp"` | `Win_Title_LogicConstructorInfo` | 600 × 700 | Column roles reference (None/PRI/SEC/TAB/GRP/SRT/GST/TST/AUX), right-click badge, worked example, Search Card search-roles |
| Card Help ℹ button (Logic tab) | `"CardHelp"` | `Win_Title_LogicConstructorInfo` | 650 × 750 | Full card type reference, Basic Logics, Global Actions |

**InfoDialog architecture:**
- `InfoDialog.xaml`: `ContentControl x:Name="InfoContent"` inside the `ScrollViewer` — accepts any `UIElement` as content; text reflows with window resize because `TextWrapping=Wrap` is set per `TextBlock`
- `InfoDialog.xaml.cs`: two constructors — `InfoDialog(UIElement content, …)` (primary) and `InfoDialog(string text, …)` (delegates to primary via `MakeTextBlock()` helper; kept for backward compatibility)
- Window size is persisted via `UiStateStore.TryLoadInfoDialogSize` / `SaveInfoDialogSize` using the `contextKey`; default size is only used on first open or after stored size was cleared

**InfoPanelBuilder** (`Services/InfoPanelBuilder.cs`, identical in both projects):
- Static class; three public methods: `BuildMainWindowHelp()`, `BuildRoleHelp()`, `BuildCardHelp()`
- Each method returns a `StackPanel` composed of styled `TextBlock` / `Border` elements
- All foreground colors use `SetResourceReference(…, "CheckupPrimaryText")` / `"CheckupSecondaryText"` / `"CheckupLabelText"` — theme-aware, not hardcoded
- Separator lines use `SetResourceReference(Border.BackgroundProperty, "CheckupSeparator")`
- Dynamic label references: card type names, tab names, panel names, and field names are read via `LanguageLoader.Get(key)` from existing `CardType_*`, `CatBuilder_Tab_*`, `Cap_BasicLogicsTitle`, `CatBuilder_Panel_Cards`, `Field_Material`, etc. keys — automatically stay in sync if those labels change
- Static descriptions (explanations, examples) are stored in `Info_*` JSON keys

**Visual hierarchy in built panels:**
| Element | Style |
|---------|-------|
| L1 header (section/window title) | Bold 14pt `CheckupPrimaryText` |
| L2 header (tab names, major sub-sections) | Bold 12pt `CheckupPrimaryText` |
| L3 header (inline editing sub-section) | Italic 11pt `CheckupLabelText` |
| Card name / Role name | Bold 11pt `CheckupLabelText` (card); Bold 11pt Consolas `CheckupPrimaryText` (role) |
| Body paragraphs | Normal 12pt `CheckupPrimaryText`, `TextWrapping=Wrap` |
| Code / example blocks | 11pt Consolas `CheckupSecondaryText`, 12px left indent |
| Separator rule | 1px `Border` with `CheckupSeparator` background, 10px top/bottom margin |
| Bullet items | "• " prefix, normal 12pt `CheckupPrimaryText` |

**Language file keys (Info_* section):**
- `Info_Main_Title/Intro/Edit/Formula/RightClick/Drag/Preset/Refresh` — main window quick guide
- `Info_Roles_Title/None/PRI/SEC/TAB/GRP/SRT/GST/TST/AUX/RightClick/ExampleTitle/ExampleColumns/ExampleResult/SearchTitle/SearchBody` — column roles
- `Info_Cards_Title/CatalogTabDesc/InlineTitle/InlineBody/LogicTabDesc/LogicCardsHeader/[CardType]_Desc/FormulaCardLabel/BasicLogics_Desc/GlobalActionsHeader/GlobalActions_Desc` — cards overview
- Removed: `HelpText`, `CatBuilder_RoleHelp`, `Cap_CardHelp` (replaced by above structured keys)

`InfoDialog` receives its title via a `titleKey` constructor parameter → `Title = LanguageLoader.Get(titleKey)` after `LanguageLoader.ApplyTo(this)`. The old `Dlg_Info_Title` key is removed.

### 5.10 Logics-Constructor — concepts and rules

**Design vision:**

The Logics-Constructor's fundamental goal is to give users **spreadsheet-like power over their data — without requiring programming**. It fills the gap between "what Inventor's standard dialogs offer" and "what would need a developer": structured data, lookups, transforms, computed values, and catalog-driven field automation — all configurable by the user through a visual interface.

The primary data surface is **Catalogs** — structured tables the user manages like spreadsheet data (fill, sort, edit, import/export). Cards and Basic Logics then operate on that data to drive field behavior.

> **Expert Mode (V1 Phase 1B):** A secondary surface — using live field values from the main window as inputs to Basic Logic formulas — is available via Expert Mode (per-group opt-in). `$[FIELD_KEY]` syntax in Basic Logic formulas reads the current `DisplayValue` of any main-window row. Expert groups auto-evaluate during DoRefreshCore (no Apply needed). Circular references unified to `LanguageLoader.Get("Cycle_DisplayLabel")` (= "⚠ Zirkelschluss").

The guiding constraint is **versatility without complexity**: the system should be capable enough to replace cases where a developer would otherwise be needed, while remaining understandable and configurable by a technically-minded non-programmer.

---

**Core concepts:**

- **Catalog:** a named table with columns and entries (rows of data).
- **Capability Set:** a named container holding one or more **Groups**. Multiple Groups can live inside one Capability Set.
- **Group:** one logic unit inside a Capability Set. Each Group corresponds to exactly one `SPECIAL:LOGIC:` entry — it appears as one `S:` item in the Field Selector and one Logic Row in the main window. A Group holds Cards and Basic Logics.
- **Card:** one logic brick inside a Group (e.g. Dropdown card, Formula card, Sync card). Catalog-backed or formula-driven.
- **Basic Logic:** a formula-driven function inside a Group. Purely computational — no interactive UI, no catalog required.

**Card types:**

| Card          | Badge color            | Purpose                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|---------------|------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Dropdown      | (default `#556070`)      | Picker that shows catalog entries; user selects one; PRI column value is written                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| Button        | (default `#556070`)      | Same as Dropdown but shown as a button that opens a full Picker Window                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| Search        | `#16A085` (teal)         | Inline search/filter within the Value Field; live filter against catalog                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| Link          | (default `#556070`)      | Locks two rows together for move/add/remove                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| Sync          | (default `#556070`)      | After writing PRI, auto-writes a companion field using a catalog role lookup                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| BasicLogic    | `#C03928` (red)          | Stores one formula expression; evaluates on Apply with `{INPUT}`=typed value; result written to `Params["FormulaTargetFieldKey"]` or group's `TargetFieldKey`. Uses `FormulaEngine`. Stored as `CapabilityCard { Type = "BasicLogic" }` — no dedicated model class. Keys are case-sensitive — see note in Section 5.8.                                                                                                                                                                         |
| MultiPick     | `#2980B9` (blue)         | Multi-token input mode with per-separator autocomplete from catalog                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| PairTransform | `#D35400` (burnt orange) | Splits the current field value into tokens by SourceTokenSeparator; looks up each token by LookupRole in the catalog; outputs the OutputRole value for each; joins results with OutputSep; writes to CompanionFieldKey. Fires on inline-edit Apply (not via picker).                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| Compose       | `#1A7A8A` (teal-dark)    | Expands a packed field value (no separator) into framed text via longest-match sub-tokenization against a catalog column; writes to CompanionFieldKey. **Split Mode** (Task #42): when `SourceSeparator` is set, first splits the source on that separator, then applies direct-match lookup and/or sub-tokenization per token; supports `OutputMode=append` to accumulate outputs from multiple Compose cards into one companion field. Fires on inline-edit Apply. See §5.8 Compose card behaviour. |
| PrefixSuffix  | `#7D3C98` (purple)       | Wraps the target field value with a static prefix and/or suffix. **Add mode** (default): strips prefix/suffix from stored value for display; prepends/appends on write; idempotent — skips if already present. **Remove mode** (inverted): strips prefix/suffix on both read and write. Bidirectional by design. No catalog required. Applies to the row's own target field only — use Sync to propagate to a companion field.                                                                                                                                                                                                                                                                                                                    |
| Sort          | `#27AE60` (green)        | Splits the current target field value into tokens by TokenSeparator; looks up each token's sort key in the catalog via LookupRole; sorts by SRT1…SRTn in index order (multi-level); rejoins with TokenSeparator; writes result back to the target field. **Unknown tokens** (no catalog row matches): placed at end, relative order preserved, shown red via existing `IsMultiTokenMismatch` display. **Empty tokens** (`""`) treated as valid lookup values — if a catalog row has LookupRole=`""` and an SRT value, the empty token sorts at that SRT position (not as unknown); multiple empty-row matches resolved by CatalogIndex order. **Invert toggle**: reverses sort direction (default ascending). Fires on inline-edit Apply. Catalog required. |

**Execution order within a Group:**
Items (Cards and Basic Logics) execute in **top-to-bottom list order** — the same order they appear visually in the group. This means a Basic Logic near the top can produce an intermediate value that a Card or another Basic Logic below it can consume. Users control execution dependencies by reordering items via drag or ▲▼.

**Card Enabled checkbox:** Every card has an `Enabled` checkbox visible in the card row UI. Disabled cards are skipped by the engine at every card-type check point (`HasCard`, `HasBasicLogicCard`, `HasMultiPickCard`, etc.). **Implementation code-verified in both projects. Pending final Inventor runtime confirmation — no code change needed.**

**Card type badge colors** are fixed per type via `CardTypeToBrushConverter`; always white text on colored pill.

**Write gate for SPECIAL:LOGIC: rows (`isFormulaOnlyGroup` / `HasValueChanged`):**

Groups that have at least one of **PairTransform, Compose, BasicLogic, or PrefixSuffix** card — and **no primary card** (Dropdown / Search / Button / MultiPick) — are classified as `isFormulaOnlyGroup = true` in `StartInlineEditCommand`. This sets `OriginalValue = null` (stored as `""`) when inline edit opens, which makes `HasValueChanged = (_editText != "")` immediately true once the user types anything. Apply is therefore always visible on entry. Groups that have a primary card set `OriginalValue = startText` (the current display value), so Apply only appears when the user actually changes the value. **Rule: any new card type that requires a write-through on Apply without a primary card must be added to the `isFormulaOnlyGroup` condition alongside PairTransform / BasicLogic / PrefixSuffix.**

---

### Basic Logic Engine — Implementation Notes & Pitfalls

These notes exist to prevent repeating bugs already discovered in testing. Any change to the BL evaluation pipeline must be checked against each point here.

**Formula syntax quick reference:**

| Token | Resolves to |
|---|---|
| `{INPUT}` | The user's typed value at Apply time (`row.EditText.Trim()`) |
| `{FIELD_KEY}` | `DisplayValue` of the row with that FieldKey — see pitfall below |
| `$[FIELD_KEY]` | **Expert Mode only.** `DisplayValue` of the row with that FieldKey, resolved automatically during DoRefreshCore (no Apply needed). Resolves to `""` if the key is not present in the layout. Participates in DFS cycle detection — circular `$[...]` references within Expert groups produce `#CIRC!`. Only enabled BL cards whose formula contains `$[...]` trigger Expert auto-evaluation. |
| `"literal"` | String literal; `\"` escapes an inner double-quote |
| `123.45` | Numeric literal (always period as decimal in source) |
| `FUNCTION(...)` | One of: `CONCATENATE`, `IF`, `LOOKUP`, `FORMAT`, `ROUND`, `VALUE`/`NUM`, `STR`, `EQ`, `NE`, `LT`, `GT`, `LTE`, `GTE`, `AND`, `OR`, `NOT`, `JOIN`, `LEFT`, `RIGHT`, `MID`, `TRIM`, `UPPER`, `LOWER`, `REPLACE`, `ABS`, `LEN`, `CONTAINS`, `STARTSWITH`, `ENDSWITH`, `ISEMPTY`, `DEFAULT` |

**FormulaContext — how values flow in (`BuildBasicLogicContext`):**

- `InputValue` = `newValue` = `row.EditText.Trim()` — set once at the point `ApplyFieldEdit` builds the context. This is the value AFTER SPEZI autocorrect but BEFORE PrefixSuffix inverse transform.
- `ResolveFieldValue(key)` = `Rows.FirstOrDefault(r => r.FieldKey == key)?.DisplayValue ?? ""`
  - **The field MUST be visible as a row in the current main-window layout.** If the row is absent, `{FIELD_KEY}` resolves silently to `""` — no error, no warning.
  - Returns the **display value** (post-display-transform). For a PrefixSuffix row the decorated value (with prefix/suffix) is returned, not the raw stored value. Design formulas accordingly.
- `Lookup` delegate searches the group's primary catalog (3-arg form) or any catalog by display name (4-arg form: `LOOKUP(key, searchCol, returnCol, "CatalogName")`). Returns `""` if catalog or entry not found.

**`blOwnsWrite` — write suppression for BL-owned targets:**

`blOwnsWrite = CardEngine.HasBasicLogicWritingTo(group, writeFieldKey)` — true when any enabled BL card in the group writes to the same field as the group's `TargetFieldKey` (either via empty `FormulaTargetFieldKey` falling back to `TargetFieldKey`, or via explicit match).

- `blOwnsWrite = true` → raw user input write is **skipped**; only the BL-evaluated result is written. Purpose: prevents corrupting numeric parameters with non-numeric typed text.
- `blOwnsWrite = false` → raw user input write runs first, THEN BL writes its result to a different field. Both writes execute.
- **Rule: if a BL card targets the same field as the group, the raw write is always suppressed — do not rely on the raw write path for that field.**

**Numeric coercion — `ToNum` rules:**

- Trailing unit text is stripped before parsing: `"120 mm"` → `120`, `"1.5 mm"` → `1.5`.
- Comma is replaced with period before parsing: `"1,5"` → `1.5` (German decimal format supported).
- `ToStr(double)` uses InvariantCulture → writes `"1234.5"` with period. `WriteParameter` re-parses with comma/period flexibility downstream.
- Non-numeric strings → `0.0` via `double.TryParse` failure fallback; no exception thrown.
- A formula that produces a number written to a UDEF string field stores the invariant-culture string representation (e.g. `"1000"` not `"1.000"`).

**BL execution guard — `HasBasicLogicCard`:**

The BL block in `ApplyFieldEdit` only runs when `CardEngine.HasBasicLogicCard(logicGroup)` is true. The DiagLogger line `"no primary catalog for group '...' — Sync/PairTransform skipped; BL runs below"` is logged for ANY group with `logicGroup != null && logicCatalog == null` — including PrefixSuffix-only groups where `HasBasicLogicCard` is false and BL does NOT actually run. Do not interpret that log line as confirmation that BL executed.

**FormulaException — error propagation:**

`FormulaEngine.Evaluate` throws `FormulaException` on syntax errors and unknown function names. `CardEngine.GetBasicLogicWrites` does not catch it — the exception propagates up through the `foreach` in `ApplyFieldEdit`. There is no broad try/catch around the BL write loop. A malformed formula will silently fail to write (the outer event handler catches it) and may leave the row in editing state. **Always validate formula syntax against the 31-function set before shipping a capability set.**

**`DOC:Appearance` / `DOC:Material` write via BL — critical constraints:**

- `FieldWriter.ApplyAsset` tries three sources to find the named appearance/material.
- **Only Source C** (`lib.AppearanceAssets` / `lib.MaterialAssets`) returns objects that can be assigned via `part.ActiveAppearance` / `part.ActiveMaterial` in Inventor 2026. Sources A (`AppearanceLibraries`) and B (`dynLib.Appearances`) fail with `"does not contain a definition"` — those COM properties do not exist on the `AssetLibrary` object.
- **"found but assignment failed"** in the error string is **misleading**: it fires whenever `errors.Count > 0`, which includes Source A/B library-access failures even when NO item was matched via Source C. This is NOT a true "found" signal.
- A write to `DOC:Appearance` succeeds only if the appearance name exists in `AssetLibrary.AppearanceAssets` in the loaded library. Custom appearances created as legacy render styles (pre-Inventor 2013 style) are not in `AppearanceAssets` and cannot be assigned via this path.
- **Catalog text labels are not appearance names.** If a BL formula routes a catalog SHORT/LONG column value to `DOC:Appearance`, verify that the column value matches the exact Inventor appearance name — not a human-readable catalog label.
- DiagLogger area `"asset"` logs every match attempt and result; check `diag.txt` when diagnosing assignment failures.

---

**Expert Mode Auto-Evaluation (V1 Phase 1B):**

Expert groups are groups with `CardGroup.IsExpert = true`. During `DoRefreshCore`, after all Normal group post-passes complete, a dedicated Expert post-pass runs:

1. **Collect candidates:** all Expert SPECIAL:LOGIC: rows whose group has at least one enabled BL card with a `$[...]` reference. Groups without `$[...]` in any enabled BL are skipped (InputValue = `""` during refresh — evaluating `{INPUT}`-only formulas would produce misleading empty results). Collected into a `Dictionary<groupId, (Row, Group)>` — if the same field key appears twice in Rows, the second occurrence is silently skipped (prevents `ArgumentException` from a duplicate-key `ToDictionary` call downstream). **Important:** an Expert BL row whose formula uses only `$[...]` refs and no `{INPUT}` will always display the formula result during auto-eval; typing a value in inline edit has no effect because the formula ignores `{INPUT}`. Use `{INPUT}` in the formula if user-typed values must influence the output.

2. **Build dependency graph:** Expert→Expert edges only. Group A depends on Group B when A's BL formula contains `$[SPECIAL:LOGIC:B_groupId]`. References to non-Expert rows (`$[PARAM:...]`, `$[DOC:...]`, etc.) are already resolved by the main refresh loop before this post-pass — no ordering needed for those.

3. **Kahn's topological sort:** queue-based; nodes with zero in-degree go first. Any node still unreachable after the queue drains is in a cycle.

4. **Cycle handling:** cyclic groups → `row.DisplayValue = LanguageLoader.Get("Cycle_DisplayLabel")` (= `"⚠ Zirkelschluss"`, German for "Circular Reference") + `row.ValueForeground = Brushes.Red` + `row.IsExpertPendingApply = false` + `row.ExpertComputedValue = null`. Logged to DiagLogger area `"expertmode"`. No write to Inventor.

5. **Evaluate in topo order:** `FormulaEngine.Evaluate(formula, ctx)` where `InputValue = ""` and `ResolveFieldValue` reads from `Rows` in memory (including values already updated by earlier Expert groups in the same pass). Only `row.DisplayValue` is updated — no write to Inventor.

**Visual signals wired in Phase 1B:**

| Location | Element | Condition | Implementation |
|---|---|---|---|
| Main window Field Selector | `S: ⚡ Label` | Group has `IsExpert = true` | `FieldCatalogBuilder.BuildCatalog`: `"S: ⚡ " + targetLabel` instead of `"S: " + targetLabel` |
| Logics-Constructor — top of content area | Amber info strip | `HasAnyExpertGroup = true` | `CatalogBuilderViewModel.HasAnyExpertGroup`; `Cap_ExpertModeInfoStrip` DynamicResource key |
| Logics-Constructor — BL formula row | ⚡ icon (amber) | `HasExpertRef = true` on `CardRowVm` | `FormulaEngine.HasExpertRef(FormulaText)`; `Visibility` converter binding |
| Logics-Constructor — ⚡ toggle button | Topo order badge (`ExpertTopoLabel`) | Group has `IsExpert = true` | `CardGroupVm.ExpertTopoOrder` (int: 0=Normal, 1..N=eval order, -1=cycle); `ExpertTopoLabel` = `"1"`, `"2"`, `"⟳"` (cycle), or `""` (Normal/non-Expert); `HasExpertTopoLabel` drives `Visibility`; `RecomputeExpertTopoOrder()` on `CatalogBuilderViewModel` called after `RenumberGroups()` and `OnGroupExpertModeChanged()` |

**Key implementation helpers:**

- `FormulaEngine.GetExpertRefs(string formula)` — yields all `$[KEY]` keys from a formula string; used for dependency graph construction.
- `FormulaEngine.HasExpertRef(string formula)` — fast `IndexOf("$[")` check; used by `CardRowVm.HasExpertRef` and by `CheckupViewModel` to filter BL candidates.
- `CardEngine.FormulaReferencesField` extended for `$[...]` — returns true if the formula's `$[KEY]` set contains the given field key; ensures Expert self-reference is caught by the existing cycle guard.

**Expert Pending-Apply State:**

When an Expert BL auto-evaluation produces a value different from what is currently stored in the Inventor document, the row enters *pending-apply* state instead of silently updating or writing:

- `RowModel.IsExpertPendingApply = true` (INPC bool): pending state flag.
- `RowModel.ExpertComputedValue` (string, no INPC): the formula result awaiting write.
- `row.ValueForeground = _expertAmberBrush` (`#D4A017`): amber text color signals pending result.
- **"⚡ Ändern" button** in Col 2 (`Grid.Column="2"` of the outer row grid, same column as Sync card "Abgleich" button), `HorizontalAlignment="Left"` — shrinks to label width only, leaving the Field Selector button accessible on its right portion. Declared last in the DataTemplate → highest z-order in cell. `IsMouseOver → Opacity=1` trigger in style suppresses inherited WPF opacity-based hover (prevents Field Selector label from showing through). No tooltip. Visible via `MultiDataTrigger` on `IsExpertPendingApply=True AND IsInlineEditing=False`; uses `CheckupApplyButtonBackground`. Clicking calls `ApplyExpertValueCommand → ApplyExpertValue(RowModel)`.
- `ApplyExpertValue(RowModel)`: writes `ExpertComputedValue` to the target field via `_fieldWriter.WriteFieldValue(doc, targetKey, ...)` → `_catalogBuilder.InvalidateCache()` → `DoRefresh()`.
- **Pre-reset:** before the Expert post-pass, all `SPECIAL:LOGIC:` rows with `IsExpertPendingApply=true` are reset (`false`/`null`/`Brushes.Black`). Prevents stale amber after document switch or deactivation.
- **Cycle + pending:** cycle handling (step 4 above) also clears `IsExpertPendingApply` and `ExpertComputedValue` — cyclic rows never enter pending-apply state.
- **Language keys:** `"Btn_ExpertApply"` (DE: "⚡ Ändern", EN: "⚡ Change") in DE.json, EN.json, DE.xaml. `Tip_ExpertApply` removed.

---

**Display columns (Dropdown / Button / Search cards):**

- `Display_0_Role` ... `Display_6_Role` stored in Card.Params (max 7).
- Only PRI column is written on selection; additional roles are visual-only in the picker.
- `CatalogDropdownItem.ExtraDisplayValues IReadOnlyList<string>` shows extra columns.

**Search card — `SearchRoles` param:**

- `SearchRoles` (Card.Params): comma-separated role badges to match during filter (e.g. `"PRI"`, `"SEC"`, `"PRI,SEC"`). Empty = default PRI+SEC.
- In Logics-Constructor UI: editable `ComboBox` bound to `AvailableCatalogRoles` (items from card's CatalogId) + `Text="{Binding SearchRoles, UpdateSourceTrigger=PropertyChanged}"`. `UpdateSourceTrigger=PropertyChanged` is required — `LostFocus` misses ComboBox item-selection events.
- `SecRole` and `TooltipRole` remain separate single-role ComboBoxes (not editable, `SelectedItem` binding).

**Storage — two-tier model:**

| Tier         | Location                                             | Purpose                                                                                             |
|--------------|------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| **Distribution** | `bin\Catalogs\` and `bin\Capabilities\` next to DLL      | Files the add-in ships with; read by CatalogStore/CapabilityStore at startup from the DLL directory |
| **User edits**   | `%APPDATA%\Checkup 2026\Catalogs\` and `…\Capabilities\` | Per-user edits; survive Clean Solution; never overwritten by build                                  |

**Build behavior (**`CreateDevSubfolders` **MSBuild target):**

- Build always creates `bin\Catalogs\` and `bin\Capabilities\` if they do not exist.
- Uses `Condition="!Exists(...)"` — **never overwrites files already in those folders**.
- Files with `CopyToOutputDirectory=PreserveNewest` in the `.csproj` are copied there on every build (e.g. `IZ_Spezis_Baukasten.capability.json`).

**Dev phase (current):**

- Test and development files in `bin\Catalogs\` and `bin\Capabilities\` are **intentionally kept** during the development phase. They survive builds and are used to test Logics-Constructor features without needing a full deployment cycle.
- These files are NOT included in `.csproj` — they live only on the developer's disk inside `bin\`.

**V1.0 delivery plan:**

- When the add-in approaches V1.0, all sample / starter Catalog and Capability files that should ship with the product must be **added to the project source** with `CopyToOutputDirectory=PreserveNewest`.
- At that point they become part of every build output and are deployed to every user's machine alongside the DLL.
- Dev-only test files that should not ship must be removed from `bin\` before the release build.

**AppData migration:**

- On first Inventor load, seed entries from `Checkup_Catalogs.json` / `Checkup_Capabilities.json` (project source, flat format) are copied to AppData (skips IDs already present).
- Individual `.catalog.json` / `.capability.json` files in `bin\Catalogs\` / `bin\Capabilities\` are loaded directly from bin at every startup — they are not migrated to AppData. They serve as the base / read-only dataset; user edits go to AppData.
- Deleted items re-seed after Clean+Build unless also removed from project source.

### 5.11 Window Management

Applies to all add-in windows: **CheckupWindow**, **CatalogBuilderWindow**, **CatalogPickerWindow** (Section 5.12), **SpeziBaukastenPickerWindow** (⚠ legacy — spec in Section 5.7), **InfoDialog**, **InputDialog**.

**Default size:** Each window has a code-defined factory size (set in XAML or constructor). This is the size used on first launch and after Reset.

**User resize:** All windows are resizable by the user. The changed size is persisted immediately to the Windows registry so it survives Inventor restarts.

**Size persistence:**

- Storage: `HKCU\Software\Checkup 2026\` (or `\Checkup 2024\` for the 2024 project).
- Standard Windows user rights — no elevated permissions required.
- Managed by `UiStateStore`; one registry value per dimension per window (e.g. `CheckupWindowWidth`, `CheckupWindowHeight`).
- On load: reads stored size and applies it; falls back to factory size if no value found.

**Reset behavior:** Resets the window to the factory (code-defined) size. Clears the stored registry values so the next launch also starts at the factory size.

**Startup position:**

- Window always opens centered on the same monitor that Inventor's main window is on.
- Determined at startup by reading Inventor's window position to identify the target monitor, then centering the add-in window on that monitor.
- `WindowStartupLocation` is NOT set to `CenterScreen` (which would use the primary monitor) — centering is calculated and applied manually.

**Always-on-top behavior:**

- The add-in window is visible above Inventor but does NOT cover unrelated non-Inventor windows.
- Implemented via WPF `Owner` property: `window.Owner = inventorMainWindow` (WPF `Window` wrapping Inventor's HWND via `HwndSource` / `WindowInteropHelper`).
- This gives correct z-order: add-in floats above Inventor but behaves normally relative to other applications — it is NOT `Topmost=True` (which would cover everything).

**Modality — all windows are non-blocking relative to Inventor:**

- All add-in windows are modeless relative to Inventor. The user can interact with Inventor normally while any add-in window is open — Inventor's message loop is never blocked.
- **CheckupWindow**: opened with `Show()` — fully modeless (no owner block at all).
- **CatalogBuilderWindow, InfoDialog, InputDialog**: opened with `ShowDialog()` — modal to their WPF owner window (blocks interaction with the parent add-in window), but Inventor itself remains fully interactive because these windows are owned by a WPF window, not by Inventor's HWND.
- Multiple add-in windows can be open simultaneously (Inventor never blocked regardless of show mode).

**Universal close rules (applies to ALL windows AND all open dropdowns/popups):**

- **ESC key** — closes whichever UI element currently has focus in a strictly layered order:
  1. Any open dropdown/popup (Field Selector, AllowedValues picker, Logic Dropdown, autocomplete) → close that popup; ESC is consumed here, window stays open.
  2. Active inline-edit row → cancel the edit; ESC is consumed here, window stays open.
  3. Window itself → close. Same effect as the dedicated close button.
- **Single left-click anywhere outside an open popup** — handled by `Popup.StaysOpen="False"` on every popup. No explicit code needed; WPF's built-in Mouse.Capture mechanism intercepts outside clicks and closes the popup automatically.
- **Single left-click anywhere outside the editing row** — cancels inline edit. Implemented via `OnPreviewMouseLeftButtonDown` override on `CheckupWindow`. Guards: (a) if any popup is open for the editing row, return early — let `StaysOpen=False` handle the click naturally; (b) walk visual tree up from `e.OriginalSource` via `IsWithinRowContext(target, editingRow)`; if no ancestor has `DataContext == editingRow`, call `CancelFieldEditCommand`. This allows clicking Apply/Cancel/Arrow buttons (all have `DataContext == editingRow`) without cancelling, while clicking on any other row, the status bar, or empty space cancels immediately.
- Neither ESC nor click-outside triggers a confirmation dialog — the action is immediate.

**Closing a window:**

- Dedicated close button within the window UI.
- `ESC` key — see Universal close rules above. Per-window priority details:
  - **CheckupWindow:** (1) Field Selector popup → close; (2) AllowedValues popup → close; (3) Logic Dropdown popup → close; (4) inline-edit row → cancel edit; (5) close window.
  - **CatalogBuilderWindow:** (1) close Find bar if open; (2) close any open Target-Field or Card-Field picker; (3) close the window.
  - **InfoDialog:** (1) if Cancel button is visible → set `DialogResult=false` and close; otherwise set `DialogResult=true` and close. Implemented via `OnPreviewKeyDown` override because `IsCancel="True"` on a `Visibility.Collapsed` button is not processed by WPF.
  - **InputDialog, SpeziBaukastenPickerWindow:** `IsCancel="True"` on a visible Cancel button handles ESC natively.
- Standard Windows title-bar `×` close button also works.
- No "are you sure?" confirmation on close — windows close immediately.

### 5.12 Catalog Picker Window

**Purpose:** A standalone secondary window opened by the **Button card** when the user clicks the Action Button in a card row's Value Field. Presents a searchable, filterable list of catalog entries and returns one selected value (single-select mode) or a set of selected values (multi-select mode) to the caller.

**Invocation:** `CatalogPickerWindow.ShowDialog()` — modal to its owner (CatalogBuilderWindow or CheckupWindow). Returns `SelectedPriValue` (single) or `SelectedPriValues` (multi) via public properties after dialog closes.

**Constructor parameters:** `items` (flat list of `CatalogDropdownItem`), `tabs` (optional tab filter definitions), `catalogId` (used for registry persistence), `app` (for theme/language), `multiSelect` flag, `preSelectedPriValues` (for multi-select pre-check).

**Layout:**

- **Tab row** (top, optional): one "All" tab + one tab per `CatalogTabEntry`. Shown only if tabs exist; collapsed otherwise. Active tab highlighted; last-used tab restored from registry via `UiStateStore.LoadCatalogPickerLastTab(catalogId)`.
- **Search box**: live filter on `PriValue`, `SecValue`, and all extra display columns (case-insensitive). Keyboard: `↓` moves focus to list, `Enter` confirms selection.
- **Item list**: `ListBox` with optional `GroupDescription` grouping (when any item has a `GroupName`). Alternating row colors.
- **Preview bar** (multi-select mode only): shows count of currently selected items and their PRI values as a preview.
- **OK / Cancel buttons**: OK disabled until an item is selected (single-select) or always enabled (multi-select — allows clearing the field with zero selections).

**Single-select mode:** Click an item to select it; click the same item again to deselect. `_selected` tracks the single active item. OK enabled only when `_selected != null`.

**Multi-select mode:** Checkbox per item; selection state stored in `_selectedPriValuesSet` (canonical across tab switches). OK always enabled. Returns `SelectedPriValues` in catalog order.

**Tab switch behavior:** Rebuilds visible items from `_allItems` filtered by the active tab. In multi-select mode, previously checked items are re-applied via `_selectedPriValuesSet` so selections survive tab switches.

**Multi-tab membership (Task #40 — planned, not yet implemented):** A value row may belong to several tabs by listing comma-separated tab names in its TAB-role cell; the picker splits on comma and lists the row under each named tab (the item filter becomes `item.TabIds.Contains(activeTab)`). Single-value cells are unaffected — the feature is opt-in and backward-compatible. Full design in §10.1.

**Size persistence:** Window size saved to / restored from registry via `UiStateStore.TryLoadCatalogPickerSize` / `SaveCatalogPickerSize`. Default: 480 × 520 px.

**Styling:** `ThemeLoader.ApplyTo()` + `LanguageLoader.ApplyTo()` called in constructor — same theme/language pipeline as all other windows.

---

### 5.13 Dropdown / Popup Behavior

Applies to all **runtime value-entry** dropdown/popup controls: Field Selector popup (§5.1), Logic Dropdown in the Value Field, AllowedValues popup, Target Field ComboBox (P3), and Card field-picker ComboBoxes (P3).

**Scope boundary:** CatalogBuilderWindow card-editor configuration dropdowns (CatalogId picker, SecRole, TooltipRole, CompanionRole, SearchRoles) are editor-time configuration controls, not runtime value-entry controls, and are NOT covered by this section. They use theme-styled WPF ComboBoxes with `AllowsTransparency="True"` on the popup for visual consistency.

**Keyboard focus on open (Field Selector popup):** opening the Field Selector popup moves keyboard focus straight into the Zone-1 search box (`FindVisualChild<TextBox>(popup.Child)?.Focus()` in `FieldSelectorPopup_Opened`) so the user can type-to-filter immediately without a mouse click. **Both projects must do this** — a 2026 regression where this focus call was missing (2024 had it) was fixed during Task #25 testing.

**Field width (the control in the grid):**

- The dropdown field itself auto-sizes to fit its label text (the currently selected entry or placeholder).
- This field width then defines the width of the popup when it opens.

**Popup width:**

- Matches the field width at open time.
- No independent fixed width.

**Column widths inside a multi-column popup:**

- Each column auto-sizes to fit its content (the widest visible text in that column).
- Total width of all columns is bounded by the popup width (= field width) at open time — columns share the available space proportionally. After the popup is open, the user may drag individual columns beyond this bound.
- **All N−1 separators** (one between each adjacent column pair) must be drag-resizable. A popup with N columns has exactly N−1 draggable separators — none may be fixed or inert.
- User can override individual column widths via mouse drag on the column separator (visible line + 6 px transparent hit-target `Thumb` at the right edge of each non-last column header cell).
- User-set column widths are stored in registry and restored on next open (see Persistence below).
- **Implementation:** popup border uses `MinWidth="{Binding LogicDropdownFieldWidth}"` (not a fixed `Width`) so the popup can expand beyond the field width when columns are dragged wider — this keeps all separator Thumbs inside the Popup HWND and hittable. `RescaleLogicDropdownColumns` scales columns to fit within field width at open time; user drag widths may exceed it afterward.
- **Implementation status:** 2026 — implemented. 2024 — implemented.

**Height:** User-resizable via mouse drag on the bottom edge of the popup. There is no fixed maximum height.

**Placement rule — own row must remain fully visible:**

A dropdown or popup opened from a row must **never visually cover or overlap any element of that same row** — neither the Value Field nor the Field Selector of the row that owns the dropdown. The user must always be able to see what they are editing while the dropdown is open.

- Rows **above** and **below** the editing row may be partially or fully covered by the popup — this is acceptable.
- Only the **row that initiated the popup** is protected from overlap.
- Implementation: popups use `Placement="Custom"` with a callback that forces the popup to open flush with the **bottom edge** of the initiating row (i.e. `new Point(0, targetSize.Height)`). WPF's default flip-to-top-on-screen-edge behavior is overridden because flipping would violate this rule by covering the editing row from above.
- This rule applies to: Field Selector dropdown, Logic card inline Dropdown/Search popups, SPEZI autocomplete popup, multi-token autocomplete popup, and any future dropdown control added to a row.

**Close triggers (any one of these closes the popup):**

- `ESC` key
- Single left-click on an entry in the list — selects it and closes
- Single left-click anywhere outside the open popup

**AllowedValues popup (standard field rows):**

Normal rows (prefix `IPROP|`, `UDEF:`, `PARAM:`, `DOC:`) may have an `AllowedValues` list provided by Inventor at runtime (e.g. Material names, Appearance names). When present, the Value Field switches to a TextBox + dropdown arrow button combo (`IsComboEditMode = true`).

- **On popup open (arrow button click or Down key):** The full `AllowedValues` list is shown immediately — NO filtering is applied on open. Rule: the user should always see all options when they deliberately open the dropdown.
- **While popup is open:** User may type in the TextBox to filter the list (case-insensitive prefix/substring match via `_allowedValuesFilterText`). The live filter only activates after the popup is already open.
- **On popup close (ESC, click outside, or selection):** `_allowedValuesFilterText` is reset to `null` (= full list). The next open always shows the complete list again.
- **Keyboard navigation:**
  - **Down arrow (popup closed):** Opens popup with full list; moves highlight to first item.
  - **Down / Up arrow (popup open):** Moves keyboard highlight through the visible `FilteredAllowedValues` list. Wraps to first/last when at an end. Highlighted item gets `IsSelected = true` on the `ListBoxItem` (via `SelectedItem` TwoWay binding) → `CheckupComboItemHoverBackground` highlight. `HighlightedAllowedValue` (string) on `RowModel` drives the binding.
  - **Enter (item highlighted):** Sets `EditText = HighlightedAllowedValue`, closes popup, applies value.
  - **Enter (popup open, no highlight):** Closes popup, no value change.
  - **ESC (popup open):** Closes popup, restores keyboard focus to `AllowedValuesTextBox` via deferred `Dispatcher.BeginInvoke(Input)`.
  - **Typing (popup already open):** Filter applies live via `AllowedValuesTextBox_TextChanged` → `SetAllowedValuesFilter`. Highlight cleared on filter change.
- **Arrow button focus retention:** The `_allowedValuesPopupWasOpenBeforeButtonClick` guard prevents the popup from re-opening when `StaysOpen=False` closes it at `PreviewMouseDown` time and then `Click` fires. Same pattern as Logic Dropdown. After clicking the arrow button, focus returns to `AllowedValuesTextBox` via `RestoreFocusToAllowedValuesTextBox`.
- **ESC priority:** ESC closes the AllowedValues popup before any other ESC action (including cancel-edit or close-window). Window-level `OnPreviewKeyDown` checks `row.IsAllowedValuesPopupOpen` first; also calls `RestoreFocusToAllowedValuesTextBox` after closing.
- **Implementation:** `RowModel._allowedValuesFilterText` (null = full list, string = active filter) is decoupled from `_editText`. `RowModel.HighlightedAllowedValue` (string, `INotifyPropertyChanged`) tracks the keyboard-highlighted item; bound to `ListBox.SelectedItem` (TwoWay). `MoveAllowedValuesHighlight(int delta)` iterates `FilteredAllowedValues`. `SetAllowedValuesFilter` and `IsAllowedValuesPopupOpen` setter both clear `HighlightedAllowedValue`. XAML: `x:Name="AllowedValuesTextBox"` on the TextBox (required by `FindDescendantTextBox`); `IsSelected` trigger on `ListBoxItem.ControlTemplate` shows hover background when selected.

**Field Selector popup — ESC key:**

The Field Selector popup uses `AllowsTransparency="True"`, which creates a separate Win32 `HwndSource` for the popup window. Keyboard events inside this popup do **not** propagate to the parent window's `OnPreviewKeyDown`. ESC is therefore handled by a dedicated `PreviewKeyDown="FieldSelectorSearchBox_PreviewKeyDown"` handler wired directly to the SearchBox TextBox inside the popup. That handler sets `row.IsFieldSelectorOpen = false` and marks the event Handled.

**Persistence:**

- User-adjusted popup height and column widths saved to `HKCU` registry immediately when changed.
- Standard Windows user rights — no elevation required.
- Managed by `UiStateStore`; one registry key per dropdown context (identified by window + control name or field key).
- On next open: stored dimensions restored; falls back to auto-width defaults if no value found.

**Reset:** Clears all stored dropdown dimensions from the registry; next open reverts to auto-width and default height.

**Logic Dropdown keyboard navigation:**

- **Focus retention:** `LogicDropdownTextBox` always retains keyboard focus during inline edit. Clicking the arrow button does not steal focus — `RestoreFocusToLogicTextBox` defers focus restoration via `Dispatcher.BeginInvoke(Input)`.
- **Down arrow (popup closed):** Opens popup, moves highlight to first item.
- **Down / Up arrow (popup open):** Moves `HighlightedLogicItem` (`LogicDropdownItemRow`) through the `_logicDropdownRowsView` (preserving all sort/group/filter state). Arrow-navigated item gets `IsHighlighted = true` → `CheckupComboItemHoverBackground` via `DataTrigger` on the popup item Button.
- **Enter (item highlighted):** Sets `EditText = item.PriValue`, closes popup, applies value via deferred `ApplyFieldEditCommand`.
- **Enter (popup open, no highlight):** Closes popup only.
- **ESC (popup open):** Closes popup, restores keyboard focus to `LogicDropdownTextBox`.
- **Arrow button guard:** `_logicPopupWasOpenBeforeButtonClick` prevents re-opening when `StaysOpen=False` closes the popup before `Click` fires.
- **Scroll into view:** `ScrollLogicHighlightedItemIntoView` walks the visual tree to find the open `Popup`, then finds the `Button` whose `DataContext == HighlightedLogicItem` and calls `BringIntoView()`.

**Dropdown arrow — must never be hidden:**

- The Dropdown arrow button is the primary interaction handle for Logic rows. It must **never** be hidden, broken, or made invisible by any other card type (Search card, Button card, or any future card). Users add the Dropdown card specifically to get this handle; removing it defeats the purpose of the card.
- **Implementation:** `LogicDropdownPanel` (the unified Logic panel) is always visible when any Logic group is active in edit mode (`IsLogicComboEditMode = IsEditMode && HasCatalogDropdownItems` — no search-mode condition). Both `LogicSearchPanel` and the separate two-popup ghost rule have been removed.

**Unified Logic panel (replaces the former two-panel approach):**

- `LogicDropdownPanel` is the single edit panel for ALL Logic rows (Dropdown card, Search card, or both).
- `IsLogicComboEditMode` = `IsEditMode && HasCatalogDropdownItems` — always true for any Logic row, regardless of search mode.
- `IsLogicSearchEditMode` = always `false` — no longer used.
- The panel contains one TextBox (editable, bound to `EditText`) and one arrow button. Both are always shown.
- **Search mode** (`IsLogicSearchMode = true`): `TextChanged` on the TextBox calls `LogicSearchTextBox_TextChanged`, which auto-opens the popup and applies the live filter via `RowModel.ApplySearchFilter`. The TextBox is the live-filter input.
- **Dropdown-only mode** (`IsLogicSearchMode = false`): `TextChanged` guard (`if (!row.IsLogicSearchMode) return`) skips the auto-open. The user types (editing `EditText`) but no filter is applied; popup opens on arrow click only.
- The popup is bound directly to `IsLogicPopupOpen` (not the former `IsLogicComboPopupOpen` / `IsLogicSearchPopupOpen` computed properties). With a single panel there is no "ghost popup" risk.
- Popup auto-open on edit-mode entry (`row.IsLogicPopupOpen = true`) removed from both VMs — popup opens only on arrow click or on typing in Search mode.

**Visual styling of dropdown rows and columns:**

- **Column separators:** Visible vertical separator lines between columns; consistently aligned across all rows for a clean, tabular appearance. Implemented as `<Border HorizontalAlignment="Right" Width="1" Background="{DynamicResource CheckupSeparator}"/>` inside each column cell.
- **Row alternating colors:** Rows alternate between `CheckupRowBackground0` and `CheckupRowBackground1` (same palette as the main grid) — follows the active theme automatically.
- **Row separator:** A 1 px dotted horizontal line between each row for additional readability and visual separation.
- All styling uses the shared theme resource dictionary — no hardcoded colors.

**Implementation pattern (all scrollable item lists):**

- `AlternationCount="2"` on the `ItemsControl` (or `ComboBox`).
- Alternating background: `DataTrigger` on `(ItemsControl.AlternationIndex)` Value="1" → `CheckupRowBackground1`. For `ComboBoxItem` styles: `Style.Triggers Trigger Property="ItemsControl.AlternationIndex"`.
- Dotted row separator: `<Rectangle Height="1" Stroke="{DynamicResource CheckupSeparator}" StrokeThickness="1" StrokeDashArray="1 2" SnapsToDevicePixels="True"/>` — either `DockPanel.Dock="Bottom"` or in a dedicated `Grid.Row="1"` spanning all columns.
- **Applies to:** Field Selector group items, ValueCombo dropdown items, Spezi/multi-token autocomplete rows, Logic Dropdown rows, Catalog Picker list items.

### 5.14 Visual Row Indicators

All stripes are 4px wide, on the far left edge of the row, theme-colored via `DynamicResource`.

---

### 5.15 Scrollbar Styling

All scrollbars in all add-in windows use a thin, modern style matching the Windows 11 / Inventor 2026 visual language.

- **Width / Height:** 8 px (vs. the WPF default ~17 px).
- **Thumb:** Rounded corners (`CornerRadius="3"`). Normal: 4 px visible width (2 px margin each side). Hover / drag: 6 px visible (1 px margin), brighter color.
- **Track:** Transparent background — no visible rail.
- **Arrow buttons:** None — removed for a minimal look.
- **Colors (theme keys):** `CheckupScrollBarThumb` (normal) · `CheckupScrollBarThumbHover` (hover/drag). Defined in both `DarkTheme.xaml` and `LightTheme.xaml`.
- **Implementation:** Implicit `ScrollBar` ControlTemplate defined in both theme dictionaries via helper styles `ModernScrollBarThumb` and `ModernScrollBarPageButton`. Because the style is implicit (no `x:Key`) it applies to every `ScrollBar` inside any window that merges a theme dictionary — no per-`ScrollViewer` opt-in is required.

**Color values:**

| Theme | `CheckupScrollBarThumb` | `CheckupScrollBarThumbHover` |
|-------|------------------------|------------------------------|
| Dark  | `#5A6880`              | `#8090A8`                    |
| Light | `#AAAAAA`              | `#888888`                    |

| Condition   | Property                                     | Visual                                                                                                |
|-------------|----------------------------------------------|-------------------------------------------------------------------------------------------------------|
| `IsLinked`    | Row is part of a Link card pair              | 4px `CheckupLinkStripe` bar at far left                                                                 |
| `IsConnected` | Row participates in a Sync card relationship | 4px `CheckupSyncStripe` bar at far left                                                                 |
| Both active | `IsLinkedAndConnected`                         | Dual stripe: Link bar at left edge; Sync bar shifted 4px right — two adjacent colored bars, 8px total |

Both colors follow the active theme (DarkTheme / LightTheme resource dictionaries). Neither color is hardcoded.

---

### 5.16 Formula (fx) Editing — iProperty Expressions & Parameter Equations

**Status:** Implemented in both projects (2026 + 2024), tested in Inventor 2026 and 2024. Diagnostics tagged `"fx"` (disabled by default).

Inventor lets text iProperties and parameters be **formula-driven**: an iProperty holds an expression like `=<NUP_BENENNUNG> <NUP_ABMESSUNG>` (leading `=`, parameter/property names in `<…>`), and a parameter holds an equation like `d3 + 10 mm`. Editing the displayed *value* of such a field silently destroys the formula. This feature mirrors Inventor's own behaviour: the row shows the **evaluated value**, and an **fx toggle** reveals/edits the **formula** behind it.

**Two visual states (per row, mirroring the iProperties dialog):**

| State | Value field shows | fx button |
|-------|-------------------|-----------|
| Read (default) | evaluated value (e.g. `Winkel 50x50`, `120 mm`) | `ƒx` — normal |
| Formula (fx pressed) | editable equation (`=<…>` or `d3 + 10 mm`) | `ƒx` — engaged (active-preset colors) |

**Read/Write semantics:**

- **iProperty value** = `Property.Value` (evaluated text). **iProperty formula** = `Property.Expression` (starts with `=`, empty when literal). Read via late binding (`CallByName(prop,"Expression",Get)`) so a missing member can't break the build.
- **Write** (`FieldWriter.SetPropertyValueOrFormula`): a literal (`no leading =`) is written to `Property.Value`, replacing any expression — **no warning, by design** (matches Inventor; the user is expected to notice the fx button). A formula (`leading =`) is probed in order **1) `Property.Expression =` 2) `Property.Value = "=…"`**, logging which path takes. The read-back verification is skipped for formula writes (a formula's `Value` evaluates to a different string than the `=…` expression).
- **Parameters are value-first now (behaviour change):** `PARAM:` rows display the **evaluated value** (`PropertyReader.ReadParameterValue` → `UnitsOfMeasure.GetStringFromValue` + unit token) instead of the raw expression. The equation is revealed via fx. A PARAM row offers fx only when the expression is genuinely formula-driven (`PropertyReader.IsParameterFormula` — references another parameter or contains arithmetic), not for plain literals like `120 mm`. Writes still go through `Parameter.Expression` (existing path), so both literal and equation edits round-trip.

**State model (`RowModel`):** `HasFormula` (formula present → show fx), `FormulaText` (the equation), `IsFormulaEditing` (fx engaged). `ShowFormulaToggle = HasFormula && (IsDisplayMode || IsFormulaEditing)`. While `IsFormulaEditing`, the normal value editors (`IsPlainTextEditMode` / `IsComboEditMode` / `IsLogicComboEditMode`) are suppressed and `IsFormulaEditMode` shows a monospace formula `TextBox`. Apply/Cancel reuse the standard `HasValueChanged` path.

**Resolution:** `FieldCatalogBuilder.ResolveFieldFormula(fieldKey, doc)` returns the equation (or `""`). Computed only in **single-selection** (formulas are per-document; multi-select hides fx). Refreshed every `DoRefresh` via `CheckupViewModel.UpdateFormulaState`.

> **Not-found sentinel discipline (fix 2026-06-08).** Expression readers must return `""` — never the display sentinel `"n/a"` — for a missing field, because the result feeds `IsParameterFormula`. `IsParameterFormula` flags a formula on any `+ - * / ^ ( )`, and `"n/a"` contains `/`; a missing `PARAM:` row therefore read as formula-driven, which cleared `IsFieldMissing` and **blanked the greyed/strikethrough missing label of §172** (the value still showed `n/a` and a stray fx appeared). Fixed at the source: `ReadParameterExpression` returns `""` for a missing parameter (matching the UDEF/IPROP expression readers); `IsParameterFormula` also rejects a bare `n/a`. General rule: value/display sentinels (`PropertyReader.NotAvailable`) must never reach a content heuristic.

**Layout:** the fx button shares the value-field's right-hand button slot (Col 1, sub-col 1) — rightmost element, mutually exclusive per row with the catalog-picker button. The formula editor spans only sub-col 0 so the fx toggle stays visible to switch back. Tooltip key: `Tip_FormulaToggle`.

**Scope:** `UDEF:` and `IPROP|` text iProperties and `PARAM:User:` / `PARAM:Model:` parameters. DOC rows never offer fx. Perf note: detection adds one expression read per value-bearing row per refresh in single-selection (acceptable for ≤30 rows; revisit if profiling shows cost).

**Propagation after iProperty write (Task #31).** Because `FieldWriter` now calls `TryUpdate(doc)` after every successful write, a formula iProperty (e.g. `Description = =<NUP_BENENNUNG> <NUP_ABMESSUNG>`) **recomputes automatically** when a referenced iProperty like `NUP_BENENNUNG` is edited. The next `DoRefresh` reads the updated evaluated value. The same applies to iLogic rules that read an iProperty and to geometry expressions. This was not the case before Task #31: iProperty writes stored the own value but did not trigger dependents.

**Logic rows (`SPECIAL:LOGIC:`) — fx beside the Window-Picker.** A logic row offers fx when its **target field** is equation-driven, so a Button-card row over an equation gives the user three exits: **fx** (edit the equation), **inline override** (type a static value), **Window-Picker** (insert a static catalog value). The picker and fx sit **side by side** in the value-field's right slot (a horizontal stack; each independently visible).

- **Detection:** `ResolveFieldFormula` follows the group's `TargetFieldKey` with the **same cycle-guard** as `ResolveFieldValue` (a `SPECIAL:LOGIC:` chain `A→B→A` would otherwise stack-overflow → Inventor crash). On a cycle it returns `""` (no fx); the value path still raises `⚠ Zirkelschluss`.
- **Carve-out:** fx is suppressed when the group **auto-owns its target** — a BasicLogic card writing to it (`CardEngine.HasBasicLogicWritingTo`) or an Expert auto-eval group (`CardGroup.IsExpert`) — because that target is recomputed every refresh and a hand-edited equation would be clobbered.
- **Write:** `ApplyFormulaEdit` writes the raw equation to the **terminal real field** (`ResolveTerminalFieldKey` walks the alias chain, cycle-guarded), **bypassing** the logic-card transforms (PrefixSuffix/Sort/BasicLogic) that decorate a normal logic Apply. The static paths (inline/picker) still go through the logic pipeline.

**Invalid equation → red text (parameters only, decision B).** On Apply, if `FieldWriter` returns an error (Inventor rejects the expression — e.g. `d1 + d2` with `d1` missing, or a circular parameter reference), the row **stays in edit** with `RowModel.IsFormulaInvalid = true`, painting the equation editor red (foreground + border, via a Style trigger — *not* a local value, which would outrank it) and surfacing the message. Cleared on the next keystroke or a successful Apply. This relies on Inventor's own validation: **parameters** throw on a bad expression; **iProperty** unknown `<refs>` are accepted-but-empty (exactly as Inventor's own dialog behaves), so there's no error to redden there. `FieldWriter.WriteParameter` wraps the write in `Application.SilentOperation = true` (restored in `finally`) so Inventor's native **"Invalid Value" modal is suppressed** — the rejection still throws and drives the red text instead of a blocking dialog.

---

## 6. Design Decisions

### 6.1 Architecture decisions

- **MVVM strict (CheckupWindow only):** `CheckupWindow.xaml` code-behind is limited to drag-and-drop row reordering and right-click copy-to-clipboard — all other logic lives in the ViewModel. `CatalogBuilderWindow` intentionally has extensive code-behind (DataGrid dynamic column building, programmatic context menus, keyboard handling) because WPF DataGrid dynamic column management cannot be done cleanly in pure MVVM. This is a deliberate and documented exception, not a violation of the pattern.
- **COM late-binding for SheetMetal:** `CallByName()` used for `FlangeFeature` sub-objects to avoid hard version binding. Catch `Exception` broadly.
- **Never auto-save:** StylePurger and all write operations never call `doc.Save()`. User saves manually. This was explicitly enforced after early versions auto-saved.
- **Single-process hosting:** add-in runs inside Inventor's process. WPF resources from Inventor's app-level resource dictionary can conflict — use explicit ControlTemplates with TemplateBinding instead of relying on default Button/ComboBox rendering.
- **SPECIAL:LOGIC: isolation:** formula and card logic runs only on these rows. Intercepting normal PARAM:/UDEF:/IPROP: rows is explicitly prohibited ("absolutely prohibited and intransparent to user").

### 6.2 Maximum row count — intentional limit

The main window is artificially limited to **30 rows** (`MAX_ROWS = 30`). This is a deliberate design decision, not a technical constraint. The limit keeps the UI from becoming unmanageable and forces users to think about which fields they actually need visible at once. Raising or removing the limit requires an explicit decision — do not treat it as a bug or remove it without user confirmation.

### 6.3 Unified visual design — "one product" rule

All add-in windows must look and feel like one cohesive product. This applies to:

- **Font:** same typeface, same sizes, same weights across all windows and controls.
- **Colors:** all colors come from the shared theme resource dictionary (`DarkTheme.xaml` / `LightTheme.xaml`) via `DynamicResource`. No hardcoded color values anywhere in XAML or code-behind except where a specific semantic override is documented (e.g. `CheckupErrorText` for mismatch red, `CheckupActionItemForeground` for the cyan accent).
- **Spacing and sizing:** margins, padding, row heights, button sizes, border thickness — kept consistent across windows.
- **Control styles:** buttons, text boxes, ComboBoxes, list items all use the same implicit/explicit styles defined in the shared resource dictionaries.
- **Alternating rows:** all list/grid views use the same `AlternationIndex` DataTrigger pattern with `CheckupRowBackground0` / `CheckupRowBackground1`.
- **Row separators:** all scrollable item lists use a 1 px dotted `CheckupSeparator` line between items (see §5.13 implementation pattern).
- **Scrollbars:** all windows use the 8 px thin modern scrollbar defined in the theme dictionaries (see §5.15). Never add per-control scrollbar overrides — the implicit theme style covers all instances.
- **Toggle-type button active state (Option C):** all toggle-type buttons (preset buttons, Logics-Constructor tab buttons, and any future on/off toggle in the add-in) use the same visual language: inactive = button background matches panel background (dissolves in); active = 1 px `CheckupPresetActiveBorder` border + `CheckupPresetActiveBackground` subtle tint. Text and label unchanged in both states. This is the standard for the entire add-in — do not invent per-feature active state visuals.

**Enforcement:** shared `ResourceDictionary` files merged into every window via `ThemeLoader.ApplyTo(window)`. Any new window must call `ThemeLoader.ApplyTo()` before being shown. Any new control style must be added to the shared dictionaries, not defined locally in one window's XAML.

**Exceptions only by explicit user decision.** If a window or control needs to deviate visually (e.g. a special accent color for a status indicator), that decision is noted in this TDD. Undocumented deviations are treated as mistakes to be corrected.

### 6.4 Spezi / Halbzeug design decisions — ⚠ Legacy guardrails

The Spezi/Halbzeug system is fully replaced by the Logics-Constructor. All code was removed. The following decisions are permanent guardrails — do not reverse them.

- **Never re-add hardcoded `SPECIAL:` entries to `FieldCatalogBuilder`** — the only allowed SPECIAL: entries are `LOGIC:` groups. The former hardcoded keys (`MiterGap`, `FlangeDistance`, `Spezi1/2`, `HalbzeugName/Ident`) and their resolver paths were fully removed (Task #29) and must not be re-added without explicit user approval.
- **Never implement a monolithic "Spezi Card"** — the composable bricks approach (individual cards combined) was chosen explicitly after a monolithic approach was proposed and fully reverted.
- **CSV catalog (`Spezi_Katalog.csv`)** — superseded by CatalogStore JSON (catalog ID `spezi001`). CSV is a one-time import seed only.

Full legacy design history (field keys, prefix choice rationale, picker window, sync behavior) in **Appendix B**.

### 6.5 Logics-Constructor composability rule

Cards are composable bricks with one job each. Button card = shows button; MultiPick card = multi-select behavior. Never merge responsibilities into one card type. Before adding a new card type, scope the missing capability and confirm with user.

### 6.6 Catalog data persistence

- AppData locations are outside `bin\` — Clean Solution never touches them.
- Deleted items re-seed after Clean+Build unless also deleted from project source.
- To update base set: edit via UI → export → replace `Checkup_Catalogs.json` in project source. Project source is authoritative.

### 6.7 Multi-select display

- Differing values shown as `|`-separated list in red.
- Identical values shown once in normal color.
- Edit box always opens empty in multi-select (forces explicit value).

### 6.8 Theme detection

- `app.ThemeManager.ActiveTheme.Name` is the correct API. All other approaches (AppearanceManager, ActiveColorScheme, XML Colors, registry) read 3D viewport colors or unrelated state.
- OS dark mode / Windows registry are never used.
- DWM title bar color set explicitly via P/Invoke for every window.

### 6.9 Ribbon icon

- Icon file: `Icon Checkup.png` in the Visual Studio project source folder. The developer replaces this file and rebuilds — no code changes required. The filename is intentionally version-neutral so icon updates are decoupled from code.
- Embedded as `EmbeddedResource` with a fixed `LogicalName` (`CheckupAddIn.checkup_icon.png`) in the `.csproj` — the code always references the logical name, never the physical filename.
- P/Invoke `OleCreatePictureIndirect` converts `Bitmap` → `IPictureDisp` for the Inventor API call.
- `StandardIcon` parameter in `AddButtonDefinition` — NOT `LargeIcon` alone (causes silent load failure).
- `System.Drawing.Common` NuGet package required for 2026 (.NET 8); built-in for 2024 (.NET 4.8).
- See `StandardAddInServer.cs` for the full P/Invoke implementation.

### 6.10 Dropdown placement — own row must not be covered

Any dropdown or popup that opens from a row must not visually overlap any element of that row (Value Field or Field Selector). The user must always be able to read the field they are editing while the dropdown is open. Rows above and below may be covered.

WPF's default auto-flip behavior (which repositions the popup above the anchor when near the screen bottom) is explicitly **disabled** for all row-level popups — it would flip the popup upward and cover the editing row, violating this rule. The popup always opens downward, even if it runs off the bottom of the screen.

Any new dropdown or popup added to a row must follow this rule. See Section 5.13 for implementation details.

### 6.11 Catalog Editor — spreadsheet-first design principle

The Catalog Editor is deliberately designed to **mimic standard spreadsheet behavior**. Every editing interaction should feel like working in Excel or LibreOffice Calc. Fill and Sort are canonical examples of this principle.

**Implications:**

- **Keyboard shortcuts** follow spreadsheet conventions: Ctrl+C copy, Ctrl+X cut, Ctrl+V paste, Ctrl+D fill down, Ctrl+R fill right, Del clear contents — not Inventor-style or WPF-default shortcuts.
- **Fill operations** (Fill Down same value, Fill Down series, Fill Right same value, Fill Right series) match spreadsheet Fill behavior exactly, including step-auto-detect for series.
- **Sort** (A→Z / Z→A, Task #27): click the **sort caret** at the right edge of a column header to sort; click again to toggle direction (`⇅` unsorted, `▲`/`▼` active). The caret is the *only* header click that sorts — clicking the header **body** selects the whole column (below). Caret zone = `SortCaretZoneWidth` just left of the resize gripper (`ColumnHeader_Click` → `IsSortCaretClick`). Context-menu sort (Sort A→Z / Z→A) remains, is persistent, marks dirty, supports partial-range selection.
- **Multi-cell selection** with Ctrl+click, Shift+click, and keyboard extension — same selection model as spreadsheet range selection.
- **Whole-row / whole-column selection (Task #27):** left-click a **row number** selects the entire row; left-click a **column-header body** selects the entire column. **Ctrl**+click adds (multi-select); **Shift**+click selects the range from the anchor (`SelectColumnRange` / `SelectRowRange`). After a header-driven selection the grid takes keyboard focus (`FocusGridForKeyboard`, deferred) so `Del` / context **Clear Contents** clears the whole selection — exactly like Excel. Implemented by populating `SelectedCells`; the row-header click is wired via an `EventSetter Event="Click"` on the `DataGridRowHeader` style (a `MouseLeftButtonUp` EventSetter never fires — the header swallows it internally). **Crash guard:** because bulk selection makes a stale multi-cell selection easy to have around, the **sort-caret path clears the selection (`UnselectAllCells`) before sorting** — the same guard the context-menu sort uses (sorting mutates the row collection; see WPF DataGrid crash patterns).
- **Clipboard** uses tab-separated values (TSV) so copy/paste works natively between the Catalog Editor and Excel — no custom format.
- **Column reorder:** drag column headers — same as spreadsheet column reorder.
- **Row numbers:** 1-based row header column — same as spreadsheet row index.
- **Delete Row / Delete Column** (structural removal, Task #26): one context-menu label each removes the entire selected row(s) / column(s) — **single OR multiple** selection — through one VM path (`RemoveEntries(IList<EntryRow>)` / `DeleteColumns(IList<CatalogColumn>)`; the single-item delete delegates to these). **Never** separate singular/plural labels. The code-behind collects the distinct selected rows/columns from `SelectedCells` *before* `UnselectAllCells()`. **Both** row and column delete confirm once for the whole batch (Task #27 unified the guard — previously rows deleted without confirmation).

**Guiding rule for new features:** When a new editing capability is requested for the Catalog Editor, the first question is *"how does a standard spreadsheet handle this?"* — implement it the same way unless there is a specific reason not to. Do not invent custom interaction patterns when a spreadsheet convention already exists.

---

## 7. Technical Constraints

### 7.1 .NET 4.8 porting rules (shared code compiled for 2024/2025)

Since Task #37 all common code lives in `CheckupAddin.Shared/`. When editing shared files, write code that compiles for **both** net48 (2024/2025) and net8 (2026/2027). Use `#if NET48` / `#else` / `#endif` guards for divergences. Three polyfill files in the shared project fill the most common gaps:

| Polyfill file | What it enables in net48 |
|---|---|
| `IsExternalInitPolyfill.cs` | C# 9 `init`-only property setters |
| `RangeIndexPolyfill.cs` | C# 8 range/index syntax: `arr[..N]`, `arr[^1]`, `str[n..]` |
| `SharedGlobalUsings.cs` | `global using` for System, Collections, Linq, IO, Text, Threading |

Remaining patterns that still need attention in shared code:

| Pattern (net8) | net48 workaround |
|---|---|
| `System.Text.Json` serializer | `#if NET48` → `Newtonsoft.Json` (6 files already dual-coded) |
| `str.Contains(sub, StringComparison)` | `str.IndexOf(sub, StringComparison) >= 0` |
| `str.Split(char, StringSplitOptions)` | `str.Split(new[] { char }, StringSplitOptions)` |
| `foreach (var (key, val) in dict)` KVP deconstruct | `foreach (var kvp in dict) { var key = kvp.Key; var val = kvp.Value; }` |
| `AppConstants.RegistryBaseKey` / `.AppDataFolderName` | Provided by each head project's `AppConstants.cs` — use the constant, never hardcode a year string in shared code |
| LINQ `FirstOrDefault(predicate)` | Works in net48 (not a divergence) |
| `new()` target-typed constructors | Work in net48 with `LangVersion:latest` |
| Switch expressions, `is not` patterns | Work in net48 with `LangVersion:latest` |
| `?.` null-conditional chains | Work in net48 |

### 7.2 COM / Inventor API gotchas

- `doc.Update()` required after every successful write — iProperties (UDEF/IPROP), DOC:Material/Appearance, and parameters. Own value is stored immediately without it, but formula iProperties that reference the changed field, iLogic rules, and geometry driven by the changed value stay stale until `Update()` fires. `FieldWriter.TryUpdate(doc)` handles the Part/Assembly cast and wraps the call in `SilentOperation`. **Batched for cascades:** `CheckupViewModel` cascade Apply methods (multi-field or multi-doc writes) wrap their write burst in `_fieldWriter.BeginBatch()` → `IDisposable` that records dirtied docs and flushes one `TryUpdate` per doc on dispose, preventing a 100+ recalc storm on 27-doc multi-select Logic Apply. Forgetting to batch falls back to per-write recalc — slower, never a missed propagation.
- COM collections: use `.Item(name)` not `[name]` indexer — C# `[]` is unreliable on COM collections.
- Active styles block `UpdateFromGlobal()` — Update Styles dialog reads on-disk state.
- `AppContext.BaseDirectory` in COM-hosted .NET 8 points to Inventor's process dir, not add-in DLL dir. Use `typeof(SomeAddinClass).Assembly.Location` instead.
- **`Parameter.Units` / `Parameter.Value` are COM dual-accessors** the C# compiler won't bind as plain properties (CS1545 / CS1503). Use `param.get_Units()` (returns `object`) and `Convert.ToDouble(param.Value)`. Used by `PropertyReader.ReadParameterValue` for value-first PARAM display (see §5.16). **Text-type parameters return a `string` from `.Value`** (not a number) — `Convert.ToDouble` throws and the catch block used to fall through to the `NotAvailable` sentinel, so a Text parameter with an equation showed `n/a` even though `ReadParameterExpression`/`IsParameterFormula` correctly detected the formula and offered the fx toggle. Fixed by checking `up.Value is string s` first and returning it directly (no unit formatting — text parameters are unitless); only a non-string `.Value` goes through `Convert.ToDouble` + `FormatParameterValue`.
- **iProperty formula detection:** `Property.Expression` (the `=…` formula behind a text iProperty) is read via late binding (`CallByName(prop,"Expression",Get)`) — never a hard interop member call, so a member-shape change can't break the build. Writing a formula probes `Property.Expression =` then `Property.Value = "=…"`; see §5.16.


### 7.3 WPF in Inventor's process

- Inventor injects app-level resources that break default Button `ContentPresenter`. Fix: inline `ControlTemplate` + `TemplateBinding` on all buttons.
- **`ItemsControl` default template** includes an inner `ScrollViewer` that Inventor's app resources override, breaking hit-testing on child elements (e.g. `Thumb` drag handles lose mouse events). Fix: always override with `<ItemsControl.Template><ControlTemplate TargetType="ItemsControl"><ItemsPresenter/></ControlTemplate></ItemsControl.Template>` on **every** `ItemsControl` inside a `Popup`. This applies to both the column-header row ItemsControl and the item-rows ItemsControl in the multi-column Logic dropdown. Never rely on the default ItemsControl template inside a Popup.
- `ComboBox` popup: `AllowsTransparency="True"` forces GPU layered-window rendering which flips content on some GPU/driver combinations — always `False`.
- `SelectedItem` + `SelectionUnit=Cell` on DataGrid is a hard crash (dotnet/wpf #4279/#4382). Never use.
- **Resetting another property's backing field inside a setter requires raising *its* `PropertyChanged`.** When one setter clears a *different* property's backing field directly (e.g. `IsInlineEditing=false` resets `_isFormulaEditing`/`_isFormulaInvalid` for efficiency), any binding or `DataTrigger` on that property goes **stale** until some later unrelated notify fires. Always `OnPropertyChanged(nameof(IsFormulaEditing))` in the same block. **Symptom (Task #25):** the fx button stayed visually "pressed"/engaged until the window was reopened.
- `UnselectAllCells()` required before `Columns.Clear()` and collection mutations.
- Never update VM state inside `SelectedCellsChanged` handler.
- Stale BAML: after XAML changes that don't rebuild, do Clean+Rebuild to clear.
- WPF CollectionView: never call `Refresh()` after setting `Filter` — double-refresh during ComboBox selection crashes Inventor.


### 7.4 Deployment

- **2026:** `.addin` manifest in `%PROGRAMDATA%\Autodesk\Inventor 2026\Addins\`. DLL in project `bin\` folder (manifest points there directly).
- **2024:** `.addin` manifest in `%APPDATA%\Autodesk\ApplicationPlugins\`. DLL in project `bin\` folder (manifest points there directly).

**No COM registration (RegAsm) required — either version.** Both manifests
specify an explicit `<Assembly>` element pointing at the DLL, so Inventor's
managed add-in loader loads the assembly directly from that path and matches
the class by its `[Guid]` via reflection. It never performs a registry
(`HKCR\CLSID`) COM lookup, so `RegAsm.exe /codebase` is unnecessary. Verified
empirically on clean Win 11 CAD machines (Inventor + Office only, **no** dev
tools) under **non-admin** standard-user accounts with Inventor libraries on a
UNC multi-user path: both add-ins loaded with RegAsm never run (a standard user
cannot elevate to run it anyway). The earlier "2024 needs RegAsm" instruction
was incorrect and has been removed from the README.

**Unsigned-add-in block (both versions).** The DLLs are not digitally signed
(Add-In Manager shows Publisher *Unknown*, Signature *"No signature was present
in the file"*). Inventor blocks an unsigned add-in on first detection. To
enable it: open the **Add-In Manager** (Inventor ▸ **Tools ▸ Add-Ins**, or it
pops up at startup for a blocked add-in), select **Checkup** on the
**Applications** tab, and in the **Load Behavior** box untick **Block** and tick
**Load Automatically**, then restart Inventor. This is a one-time per-user step.

**Managed/policy environments (admin note).** Inventor's add-in security can be
configured to load only policy-approved add-ins (signed, or from trusted/approved
locations). In tightly managed multi-user deployments this can prevent a standard
user from unblocking an unsigned add-in at all; the add-in or its install location
must then be approved centrally by an administrator.

**Side-by-side installs — version-independent folder gotcha.** Inventor scans two
kinds of add-in folders: **version-specific** (`…\Autodesk\Inventor <year>\Addins\`)
and **version-independent** (`…\Autodesk\Inventor Addins\`, with *no* year — both the
`%PROGRAMDATA%` and `%APPDATA%` variants). A manifest in the version-independent
folder is read by **every** installed Inventor release. Combined with the open-ended
lower bound that the manifests originally used (`SupportedSoftwareVersionGreaterThan`
only), a single variant placed there loaded in its target release *and every newer
one* — e.g. a 2026 manifest (`GreaterThan>29..`) in `%PROGRAMDATA%\Autodesk\Inventor
Addins\` showed up as a second "Checkup" in Inventor 2027 (v31 > 29). This only
surfaces on machines with **multiple Inventor versions installed side-by-side**
(typically developers/testers). Two safeguards, both now in place: (1) every manifest
sets **both** `SupportedSoftwareVersionGreaterThan` *and* `SupportedSoftwareVersionLessThan`
so each variant self-restricts to exactly one release even from a shared folder; and
(2) deploy each variant only to its **version-specific** folder, not the
version-independent one. (Inventor's own "Override settings" file it writes when you
toggle an add-in in the Add-In Manager — `LoadOnStartUp 0/1`, header `Created by
Autodesk Inventor Version …` — can also linger in a version folder and re-appear each
startup; delete it with Inventor closed.)

### 7.5 Build

```
msbuild CheckupAddin2026/CheckupAddin2026/CheckupAddin2026.csproj /p:Configuration=Debug /p:Platform=x64
```

Full MSBuild path required — `msbuild` is not on PATH. `dotnet build` does not work for COM-interop WPF projects. Before first build, run `fetch_interop.ps1` to populate `lib\<year>\` with each variant's interop (gitignored, never committed). Release zips for all four variants are produced locally by `build_release.ps1` — there is no CI build (a hosted runner has no Inventor install).

NuGet build failure (MSB4018): delete `obj\project.assets.json` + `obj\project.nuget.cache`; Clean Solution alone does not fix this.

**Post-build targets:**

- `CreateDevSubfolders` (`AfterTargets="Build"`): creates `bin\Catalogs\` and `bin\Capabilities\` with `Condition="!Exists(...)"` — idempotent, never deletes or overwrites existing files. Ensures a developer can drop test files into these folders once and they survive all subsequent builds.
- Files declared `<None Update … CopyToOutputDirectory="PreserveNewest">` in `.csproj` are copied flat into `bin\` (e.g. `IZ_Spezis_Baukasten.capability.json` → `bin\Capabilities\`). See the `<TargetPath>` element in `.csproj` for the exact destination path.

**Design Harness build** (see §7.7; included in release bundles since v0.13.x):
```
dotnet restore DesignHarness\DesignHarness.csproj --runtime win-x64   # required on first build on a machine without VS NuGet fallback folder
msbuild DesignHarness\DesignHarness.csproj /p:Configuration=Debug /p:Platform=x64
```
Output: `DesignHarness\bin\CheckupAddIn.DesignHarness.exe` (flat output, no framework/RID subfolder).

**Runtime requirements for the DesignHarness release bundle:**
- .NET 8 Desktop Runtime (x64) must be installed on the target machine
- `Autodesk.Inventor.Interop.dll` from the Inventor 2026 install must be placed next to `CheckupAddIn.DesignHarness.exe` (not shipped in the zip — gitignored, same as the add-in bundles)

`build_release.ps1` handles the DesignHarness automatically (included by default; skip with `-SkipHarness`).

### 7.6 Cross-Variant Resource File Linking

**Canonical location:** The visual and language resource files below live **only** in the 2026 project. All other variants reference them via MSBuild `<Link>` items so a single edit automatically propagates to all four variants on the next build. Physical copies in the other three projects have been deleted.

| File | Canonical path (2026) | Link target (2024/2025/2027) |
|---|---|---|
| `DarkTheme.xaml` | `CheckupAddin2026\Resources\DarkTheme.xaml` | `Resources\DarkTheme.xaml` |
| `LightTheme.xaml` | `CheckupAddin2026\Resources\LightTheme.xaml` | `Resources\LightTheme.xaml` |
| `Addin_Language_File_DE.json` | `CheckupAddin2026\Resources\Languages\Addin_Language_File_DE.json` | `Resources\Languages\Addin_Language_File_DE.json` |
| `Addin_Language_File_EN.json` | `CheckupAddin2026\Resources\Languages\Addin_Language_File_EN.json` | `Resources\Languages\Addin_Language_File_EN.json` |
| `Addin_Language_File_DE.xaml` | `CheckupAddin2026\Resources\Languages\Addin_Language_File_DE.xaml` | `Resources\Languages\Addin_Language_File_DE.xaml` (**2027 only**) |

**`.csproj` pattern** (identical for DarkTheme/LightTheme XAML pages):
```xml
<Page Include="..\..\CheckupAddin2026\CheckupAddin2026\Resources\DarkTheme.xaml">
    <Link>Resources\DarkTheme.xaml</Link>
</Page>
```
**`.csproj` pattern** (JSON files — copy to output directory):
```xml
<None Include="..\..\CheckupAddin2026\CheckupAddin2026\Resources\Languages\Addin_Language_File_DE.json">
    <Link>Resources\Languages\Addin_Language_File_DE.json</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <TargetPath>Languages\Addin_Language_File_DE.json</TargetPath>
</None>
```
The `<Link>` value sets the logical path inside the compiled assembly (pack URI) and the copy destination. WPF resolves pack URIs relative to the **consuming** assembly name, so each variant compiles the linked XAML files under its own assembly name — no URI conflicts.

**Why `DE.xaml` is NOT linked into 2024/2025:** `Addin_Language_File_DE.xaml` contains a `<sys:String>` resource that references `xmlns:sys`. .NET 8 requires `assembly=System.Runtime`; .NET 4.8 requires `assembly=mscorlib`. A single physical file can only carry one `xmlns:sys` declaration, so sharing across framework lineages is impossible. **Do not attempt to link or unify `DE.xaml` between net48 (2024/2025) and net8 (2026/2027).** 2027 (net8) safely links from 2026 because both use `assembly=System.Runtime`.

### 7.7 Design Harness (build-time tool)

The **Design Harness** (`DesignHarness\`) is a standalone WPF application for a **designer** to tune the add-in's visuals without building or running Inventor. It is packaged and published alongside the four add-in bundles as `CheckupDesignHarness_<Tag>.zip` — intended for users who want to customize the add-in's appearance without cloning the full repo. It is also useful for contributors who cloned the public repo and want to preview visual changes before building all four variants.

**What it provides:**
- **Window picker:** launches all 8 add-in windows as live previews (Main, Logics-Constructor [Catalog + Capabilities tabs], Catalog Picker, 3 Info windows, Input, PresetPicker).
- **Colors editor:** change any theme color → all open preview windows update live → **Export** writes the new color table back into 2026's `DarkTheme.xaml` and `LightTheme.xaml` source files.
- **Labels editor:** change any UI string → live update → **Export** writes back to `Addin_Language_File_DE.json` / `EN.json`.
- **Font / size sliders:** adjust `CheckupFontFamily` and `CheckupBaseFontSize` typography resources → live update → **Export** writes back to the theme XAML files.
- **Window W/H fields:** read the active preview window's current size; **Apply Size** resizes it; **Export Size** writes back to the Window root XAML (`Width`/`Height` attributes).
- **Structural editing** (positional XAML): use **Blend for Visual Studio** targeting this harness — it provides full WYSIWYG layout.

**Architecture:**
- `ProjectReference` to CheckupAddin2026 only (2026 is the canonical source; linked files propagate the 2026 changes to all variants at build time).
- One shared `ResourceDictionary` instance is added to every preview window's `MergedDictionaries` — editing a resource updates all open windows without leaking into the harness shell.
- `UiStateStore.DesignMode = true` is set at startup so all `TryLoad*` size methods return `false` — windows always open at their factory/XAML sizes, not the developer's persisted registry values.
- Preview windows use in-memory demo catalog/capability data (harness never calls any `Save()` method — source files are written only on explicit Export).
- `InternalsVisibleTo("CheckupAddIn.DesignHarness")` is declared in each variant's `InternalsVisibleTo.cs` so the harness can access `internal` members (e.g. `UiStateStore.DesignMode`, `CatalogBuilderViewModel`).

**Run:**
```
DesignHarness\bin\CheckupAddIn.DesignHarness.exe
```
Crash log (WPF unhandled exceptions are otherwise silent): `%TEMP%\CheckupHarness_error.log`.

**Laptop / cold-cache build note:** On machines where `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages` does not exist (absent on some VS Community installs), MSBuild's NuGet restore task fails with a missing fallback folder error. Fix: run `dotnet restore DesignHarness\DesignHarness.csproj --runtime win-x64` before MSBuild. This is a one-time step per machine.

**Known init-time gotcha:** XAML `IsChecked="True"` / `SelectedIndex` / `Slider Value` attributes fire their event handlers **during `InitializeComponent`**, before later-declared controls exist. All such handlers are guarded with `if (!IsLoaded) return;`.

### 7.8 VS Shared Project Structure (Task #37)

**Since Task #37**, all common C# and XAML lives in `CheckupAddin.Shared/` (a Visual Studio Shared Project). The four head project directories (`CheckupAddin2024/` … `CheckupAddin2027/`) each contain only variant-specific identity files:

| File in head project | Purpose |
|---|---|
| `StandardAddInServer.cs` | COM entry point — unique GUID + ProgId per variant |
| `AppConstants.cs` | Year-specific string constants: `RegistryBaseKey`, `AppDataFolderName` |
| `InternalsVisibleTo.cs` | `[assembly: InternalsVisibleTo]` attributes |
| `*.csproj` | Framework, Inventor interop reference, NuGet packages, shared project import |
| `Autodesk.CheckupAddIn<year>.addin` | Inventor manifest |
| `Autodesk.CheckupAddIn<year>.addin.template` | Manifest template for release packaging |

**Shared Project layout** (`CheckupAddin.Shared/`):

```
CheckupAddin.Shared.shproj        — VS Shared Project definition (GUID 5E7A4B3C-...)
CheckupAddin.Shared.projitems     — MSBuild item list (50+ files)
SharedGlobalUsings.cs             — global using declarations for net48
IsExternalInitPolyfill.cs         — System.Runtime.CompilerServices.IsExternalInit stub (net48)
RangeIndexPolyfill.cs             — System.Index, System.Range, RuntimeHelpers.GetSubArray<T> (net48)
InternalsVisibleTo.cs             — shared assembly-level attributes
Converters\                       — WPF value converters
Helpers\                          — BindingProxy and other helpers
Models\                           — RowModel, CapabilityData, CatalogData, PresetData, …
Services\                         — FieldCatalogBuilder, PropertyReader, UiStateStore, …
ViewModels\                       — CheckupViewModel, CatalogBuilderViewModel, RelayCommand
Views\                            — All XAML windows (Page items) + code-behind (Compile items)
```

**Import pattern** added to each head `.csproj`:
```xml
<Import Project="..\..\CheckupAddin.Shared\CheckupAddin.Shared.projitems" Label="Shared" />
```

**Editing shared code:** make the change once in `CheckupAddin.Shared/` — it compiles into all 4 variants automatically. For net48 divergences use `#if NET48` guards (see §7.1). Use `AppConstants.RegistryBaseKey` and `AppConstants.AppDataFolderName` where year strings are needed; never hardcode them in shared files.

**All 4 variants produce `AssemblyName = CheckupAddIn` → `CheckupAddIn.dll`.** Pack URIs are therefore `/CheckupAddIn;component/Resources/...` uniformly across all variants.

---

## 8. UI Vocabulary

Agreed terms — use these in all conversations to avoid ambiguity.

| Term                | Description                                                                                                                                                                                                                                                                                                                  | Code identifiers                                                                         |
|---------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------|
| **Field Selector**      | ComboBox column (right side of each Row); auto-widths to longest label; user-draggable; double-click border resets width; pinned top items (Add/Remove Row + Logics-Constructor specials); scrollable grouped+natural-sorted field list below; missing fields shown greyed+strikethrough; Special Functions prefixed `S:` (red) | `FieldKey`, `FieldItem`, `FieldCatalog`, `FieldCatalogBuilder`                                   |
| **Value Field**         | The UI cell spanning from Drag Handle to Field Selector; shows read value; single left-click enters inline edit (full-width frame); can host a Dropdown and/or an Action Button at the far right                                                                                                                             | `DisplayValue`, `EditText`, `AllowedValues`, `IsInlineEditing`                                   |
| **Row**                 | One Field Selector + Value Field pair                                                                                                                                                                                                                                                                                        | `RowModel`                                                                                 |
| **Document Name Field** | Header bar element showing the active/selected document filename(s); auto-wraps and trims at 2 lines (Plain/Compact) or 5 lines (Detailed); full text on mouse-over tooltip; three view modes (Plain / Compact / Detailed) cycled via the View Mode button (label `S ⇄` / `C ⇄` / `D ⇄`) or left-click on the field itself; mode persisted in Registry; Reset returns to Plain | `FileName`, `FileNameViewModeLabel`, `FileNameMaxHeight`, `BuildFileName*()`, `GetTopLevelName()`, `_fileNameViewMode`, `CycleFileNameViewModeCommand` |
| **Field Catalog**       | Runtime-discovered set of all available fields                                                                                                                                                                                                                                                                               | `FieldCatalog`, `FieldCatalogBuilder`, `FieldItem`                                             |
| **Source Object**       | The Inventor document(s) currently being read                                                                                                                                                                                                                                                                                | `DocumentResolver`, `_selectedDocs`                                                          |
| **Presets**             | Named row-layout configurations                                                                                                                                                                                                                                                                                              | `PresetsManager`, `UiStateStore`                                                             |
| **Language System**     | Runtime language from Inventor locale; DE/EN JSON strings; DynamicResource                                                                                                                                                                                                                                                   | `LanguageLoader`, `Strings.*.json`                                                           |
| **Theme System**        | Runtime dark/light following Inventor color scheme                                                                                                                                                                                                                                                                           | `ThemeLoader`, `DarkTheme.xaml`, `LightTheme.xaml`                                             |
| **Special Fields**      | `SPECIAL:`-prefixed fields. The only valid one is `SPECIAL:LOGIC:` (Logics-Constructor group rows). **⚠ ALL hardcoded SPECIAL: fields REMOVED (Task #29):** `MiterGap`, `FlangeDistance`, `Spezi1`, `Spezi2`, `HalbzeugName`, `HalbzeugIdent` — all code paths (including resolvers and `SheetMetalReader`), XAML panels, and language keys removed from both projects. Logics-Constructor (`IZ_Spezis_Baukasten.capability.json`) is the replacement. For historical reference: these were computed or UDEF-backed hardcoded fields, replaced because the Logics-Constructor covers the same use cases with full user configurability. Old presets still referencing them degrade to greyed/strikethrough missing-field rows. | shown with "S:" tag                                                                      |
| **Spezi Baukasten**     | ⚠ **REMOVED.** Was: catalog-backed Spezi1+Spezi2 pair with `SpeziBaukastenPickerWindow`, CSV-backed catalog. Replaced by Logics-Constructor capability set `IZ_Spezis_Baukasten.capability.json` + CatalogStore JSON. All code removed. `SpeziAutoCompleteItem.cs` and `SpeziSegment.cs` retained — they serve the MultiToken system (MultiPick card), not the legacy Spezi feature. | ~~`SpeziBaukastenCatalog`~~ ~~`SpeziBaukastenPickerWindow`~~ (removed)                   |
| **Picker Window**       | Full window (not popup) for browsing and selecting catalog entries — opened from a **Button card row** in the Logics-Constructor. The `SpeziBaukastenPickerWindow` variant is legacy (used by old `SPECIAL:Spezi1/2` rows).                                                                                                           | `SpeziBaukastenPickerWindow` (legacy), `CatalogPickerWindow` (Logics-Constructor)             |
| **Logics-Constructor**   | Both the feature system (card-based logic for SPECIAL:LOGIC: rows) and the window used to configure it. German: *Logik Baukasten*. Older name "Logic Builder" is retired.                                                                                                                                                      | `CardEngine`, `CatalogStore`, `CapabilityStore`, `CatalogBuilderWindow`, `CatalogBuilderViewModel` |
| **Catalog**             | Named table with columns + entries                                                                                                                                                                                                                                                                                           | `CatalogData`, `CatalogStore`                                                                |
| **Capability Set**      | Named container holding one or more Groups; each Group = one SPECIAL:LOGIC: row                                                                                                                                                                                                                                              | `CapabilitySet`, `CapabilityStore`                                                           |
| **Group**               | One logic unit inside a Capability Set; one S: entry in the Field Selector                                                                                                                                                                                                                                                   | `CardGroup`                                                                                |
| **Card**                | One logic brick inside a Group; catalog-backed or formula-driven                                                                                                                                                                                                                                                             | `CapabilityCard`, `CardEngine`                                                                    |
| **Basic Logic**         | Formula-driven function inside a Group; purely computational, no catalog needed                                                                                                                                                                                                                                              | `CapabilityCard { Type = "BasicLogic" }` (no dedicated class)                                                                          |

---

## 9. Scope Boundaries

### What the add-in does

- Display and edit iProperties, parameters, and computed values for active/selected **parts (IPT — all types: sheet metal, standard, weldment) and assembly components (IAM)**.
- Support multi-selection across IAM assemblies (IPT parts only — no batch write to IAM itself).
- Catalog-driven field logic (Logics-Constructor) configurable without rebuilding.
- ~~Specialty designation entry (Spezi Baukasten) with catalog-backed multi-value picker.~~ **⚠ Legacy** — replaced by Logics-Constructor capability set.
- Style cleanup (Style Purger) for IDW/IPT/IAM documents.
- Ribbon integration (Sheet Metal, 3D Model, Assemble, Drawing tabs).

### What the add-in deliberately does NOT do

- It does not modify Inventor's native property dialogs or browser.
- It does not access vault / PDM systems (Vault integration is a pending item, not yet implemented).
- Style Purge does not auto-save — user must save manually.
- Logics-Constructor runs only on `SPECIAL:LOGIC:` rows — never intercepts normal PARAM:/UDEF: rows.
- Multi-select write covers IPT parts only — no batch write across assemblies or IDW sheets.

---

## 10. Open Items / Future Work

| ID | Area | Description | Status |
|----|------|-------------|--------|
| T1 | 2026 | Vault Professional integration — `VAULT:` field key prefix; enumerate loaded add-ins; late-bind or reference `VaultInventorServer.dll`; add a `Services/VaultReader.cs`; add `VAULT:*` keys to `FieldCatalogBuilder`; non-Vault files show `—`. | Optional — deferred indefinitely. |
| T40 | shared | Multi-tab catalog membership — one value row appears under several picker tabs (comma-separated tab names in the TAB cell). Design decided (names + definition rows). See §10.1. | **Planned** — TDD done; code + Test_Spezifik data migration pending. |
| T42 | shared | Compose Split Mode — expand packed material doubles and feature-axis stacks embedded inside a separator-delimited SPEZIFIK1 short form into a correct SPEZIFIK2 long form (D1/D2 material framing + positional feature-axis expansion). Extends the Compose card with 6 new params; requires two domain-specific sub-catalogs as adjustments. See §10.2. | **Code-complete + verified (Inventor 2026)** — commit deferred; folded with T43 to land the SPEZIFIK pipeline once. |
| T43 | shared | Generation-scoped expansion + placing-order sort — fixes two correctness gaps in the SPEZIFIK1→2 expansion that T42 verification surfaced: a token **collision** (`fnnn`/`fpnn`/`nnnn` = Blech sheet-edge vs Paneel feature-axis), resolved by a new catalog **Generation** column + a short-derived generation scope filter; and output **order**, resolved by sorting the assembled long form on `placing_order`. One in-place rework of the `spezi-g1` expansion. See §10.3. | **DONE — implemented + Inventor-verified (2026-06-26)**; also canonicalizes the short form. 104 tests, build ×4 clean. Commit bundles T41+T42+T43. |
| T44 | shared | Logics-Constructor card-editor responsive layout — card fields **wrap** to the available width instead of clipping or forcing the giant outer horizontal scrollbar; each `label+field` pair is kept together (no orphaned labels); the whole field area (type controls **+** companion/partner picker) flows as one `WrapPanel`; each card has its own **horizontal** scrollbar that engages below a `MinWidth` floor (700 px) while the outer view keeps **vertical** scrolling; no per-card vertical scroll. XAML-only (shared `CatalogBuilderWindow.xaml`). See §5.8 (Card / Basic Logic row layout + Scrolling). | **DONE — implemented + Inventor-verified (2026-06-26)**; build ×4 clean. |

### 10.1 Task #40 — Multi-tab catalog membership (planned)

**Goal.** Let a single catalog value row appear under multiple tabs in the Catalog Picker Window. Restores a capability the legacy hardcoded Special Functions had, now in the CardEngine model. Opt-in and backward-compatible: a TAB cell with no comma behaves exactly as today.

**Data model (decision D1 — "names + definition rows").**
- **Data rows** carry, in the TAB-role (3) cell, a comma-separated list of the tab **names** the row belongs to (e.g. `BG ILKAsys, Pfosten`).
- **Definition rows** (empty-PRI rows) declare each tab via `TAB = <name>` + `TST = <order>`; they fix the tab caption and left-to-right order. (A catalog may omit them — tabs then derive from the distinct names found in data rows, ordered first-seen.)
- The tab **name is the identifier** — no code→label map. Constraint: a tab name may not contain a comma (the list delimiter).
- Chosen over a "codes + label map" model — codes' only edge (comma-tolerant names + single-point rename) does not justify the extra label-resolution step.

**Engine changes (3 runtime touch-points; the other 3 TabId consumers are unchanged):**
- `CardEngine.GetPickerTabs` — split each TAB cell on comma; each trimmed token is a distinct tab (caption + TST order from its definition row).
- `CardEngine.GetDropdownItems` + `CatalogDropdownItem` — store the split membership as a `TabIds` set (was a single `TabId` string).
- `CatalogPickerWindow` item filter — `item.TabIds.Contains(activeTab)` instead of `item.TabId == activeTab`.
- Unchanged: the tab-button identity logic (active/last-tab compare) stays single-valued and correct.

**Role-multiplicity.** The change is confined to TabId parsing + the filter; it never touches `GetColumnKey` / `GetRoleKey` / `ParseRoleBadge`, so every other role — including multi-instance badges (`SEC2`, `SRT2`) — is unaffected. TabId is the sole exception: multi-membership is a comma list in the **single index-1** TabId column, not multiple TabId columns (those still resolve to index 1).

**Backward compatibility.** A no-comma TAB cell yields one token, so `Contains` ≡ the old `==`; Demo and all existing single-tab catalogs are unchanged.

**Tests (net8 + net48).** Single-tab regression (Demo); a row `"A,B"` appears under A and B but not C, and once under All; whitespace / duplicate tokens normalized; tab order follows definition-row TST.

**Documentation (part of "done").** Ship doc updates WITH the feature, both directions: (a) **in-app** — the Catalog/Capabilities ℹ Info-window help via `InfoPanelBuilder`; (b) **public** — `docs/Getting-Started.md` (and README if the feature list changes). EN primary + DE.json parity.

**Test_Spezifik migration (separate, one-time).** Translate each data row's legacy code CSV (`G1,G2…` in the Role-None `value_visibility_in_group_tab_id` column) into the tab names in the TAB column; keep the 9 header rows as definition rows; drop the visibility column + the `G1…G9` codes.

### 10.2 Task #42 — Compose Split Mode (planned)

**Goal.** When the user types a SPEZIFIK1 short form such as `100-pur-l5a1-fnpp-dvsp`, the add-in must produce a complete SPEZIFIK2 long form including the expansion of packed material doubles (`l5a1` → `D1 Edelstahl… / D2 PVC-w…`) and packed feature stacks (`fnpp` → `Feder links, angeschäumter Pfosten oben/unten`). Today PairTransform drops any token that has no direct catalog match, so packed codes are silently lost. This task introduces Split Mode on the existing Compose card to fill that gap.

**Design decisions.**

- **PairTransform is untouched.** It handles all direct-match tokens and continues to write the base SPEZIFIK2 value. No change to existing PairTransform code or configuration.
- **Two additional Compose cards** are added to the SPECIAL:LOGIC: group alongside PairTransform, each in `OutputMode=append` and `OnDirectMatch=skip`, so they contribute only the tokens PairTransform could not expand.
- **Material doubles** use longest-match sub-tokenization (existing Compose mechanism) against a material-only sub-catalog. Mixed-length codes (`hlz`=3-char + `a1`=2-char) are correctly handled. Same-material doubles collapse to `D1/D2 X`; mixed doubles produce `D1 X / D2 Y`.
- **Feature stacks** use a pre-computed combinations sub-catalog (4-char PRI entries) so that axis-specific semantics (`fn`=L-R "Feder links" vs `fn`=O-U "Feder oben") are resolved at catalog-authoring time, not by the engine. `LongestMatchTokenize` finds the 4-char whole-token match, bypassing axis ambiguity entirely.

**Code changes (rebuild required):**
- Six new Compose card params in `CardEngine.cs` + `CatalogBuilderViewModel` + `CatalogBuilderWindow.xaml`: `SourceSeparator`, `TokenOutputSeparator`, `ComposeFallbackCatalogId`, `OnDirectMatch`, `OutputMode`, `AppendSeparator`. Full spec: §5.8 Compose Split Mode.
- `IsComposeSplitMode` computed property on `CardItemViewModel` (`!string.IsNullOrEmpty(SourceSeparator)`); drives Row 3 visibility in the card editor.
- Card editor XAML (`CatalogBuilderWindow.xaml`): Compose params restructured from a flat `WrapPanel` to a 3-row `StackPanel(Vertical)` — Rows 1+2 always visible for Compose; Row 3 gated by `IsComposeSplitMode`. No other card types touched.

**Adjustments (no rebuild):**

| Artifact | Change |
|---|---|
| `001-test-spezifik-material-katalog.json` | New sub-catalog: Material + Holz_Material entries only (no Merkmale/Blechkante); used as `ComposeFallbackCatalogId` for the Material Compose card |
| `001-test-spezifik-feature-combos-katalog.json` | New sub-catalog: pre-computed 4-char feature stacks as direct PRI entries, correct combined SEC; used as `ComposeFallbackCatalogId` for the Feature Compose card |
| `Test_Spezifik.capability.json` | `spezi-g1` / `spezi-g2` groups: add Material Compose card + Feature Compose card after existing PairTransform card |
| `Addin_Language_File_EN/DE.json` | Keys for the 6 new params: `Compose_SourceSeparator`, `Compose_TokenOutputSep`, `Compose_FallbackCatalogId`, `Compose_OnDirectMatch`, `Compose_OutputMode`, `Compose_AppendSeparator` (+ tooltip keys) |

**Example — `100-pur-l5a1-fnpp-dvsp-tpl`:**

| Card | Processes | Writes to SPEZIFIK2 |
|---|---|---|
| PairTransform | `100`→ISO 100, `pur`→PUR-Schaum, `dvsp`→Druckverteilerplatte Spanplatte, `tpl`→T-Pfosten… | `ISO 100, PUR-Schaum, Druckverteilerplatte Spanplatte, T-Pfosten…` |
| Material Compose | `l5a1` → `l5`+`a1` → D1/D2 framing | appends `, D1 Edelstahl 1.4301 2,0 rutschh. R11 / D2 PVC-w 0,6 RAL9010` |
| Feature Compose | `fnpp` → 4-char direct match in combos catalog → "Feder links, angeschäumter Pfosten oben/unten" | appends `, Feder links, angeschäumter Pfosten oben/unten` |
| **Final SPEZIFIK2** | | `ISO 100, PUR-Schaum, Druckverteilerplatte Spanplatte, T-Pfosten…, D1 Edelstahl 1.4301 2,0 rutschh. R11 / D2 PVC-w 0,6 RAL9010, Feder links, angeschäumter Pfosten oben/unten` |

**Tests (net8 + net48):**
- SourceSeparator splits on `-`; direct-match tokens pass through / are skipped per `OnDirectMatch`.
- Material doubles: same-material collapse (`g2g2`→D1/D2), mixed (`l5a1`→D1/D2), 3-char+2-char (`hlzg2`), unknown token → skip.
- Feature combos: 4-char direct hit (`fngg`→"Feder links, stirnseitig glatt"), `nnnn`→empty→no append, single-axis (`fnnn`→"Feder links").
- `OutputMode=append`: appends with separator when companion non-empty; writes directly when companion empty.
- `OnDirectMatch=skip`: direct-match tokens excluded from output; sub-expanded tokens included.
- IPT (single material, no doubles): PairTransform handles everything; both Compose cards produce empty output; SPEZIFIK2 unchanged.

**Documentation (part of done):**
- §5.8 Compose Split Mode (this section — already present).
- Card editor UI: new param editors visible when `IsCompose` in `CatalogBuilderWindow.xaml`.
- `docs/Getting-Started.md`: brief note in the Compose card reference.
- EN/DE language JSON parity across all 4 variants via cross-variant linking (§7.6).

### 10.3 Task #43 — Generation-scoped expansion + placing-order sort (IMPLEMENTED + Inventor-verified 2026-06-26)

> **AS-BUILT (2026-06-26).** Engine: new `ColumnRole.Generation` (badge `GEN`); `EntryInGenerationScope` (blank cell = universal) + `EntryInCategory` + `DetectGeneration` threaded into `BuildPairTransformValue`/`BuildComposeValue`/`BuildComposeSplitValue`/`FindDirectMatchSec` (all default-inert). Category & generation matching is **case-insensitive STARTS-WITH over a comma-list** (handles the real `Value_ID` = `CategoryNNN` convention; e.g. signal `Schaum,Füllung`, material scope `Material,Holz_Material`). Sorted assembly: `OrderedSegment` + `BuildPairTransformSegments` + `GetComposeSegments` (Compose `OutputPlacing` param) + `AssembleSorted` (sort placing→internal→order, join once). **Also `BuildSortedShort`** canonicalizes the SHORT (SPEZIFIK1) by the same placing — *added post-verify on user feedback*. Group config on `CardGroup`: `OrderCompanionByPlacing` (⇅ toggle, `GEN`-amber, mirrors Expert `⚡`), `GenerationSignalColumn/Values`, `GenerationWhenPresent/Absent`. Material sub-catalog **deleted** (category scope replaces it → two catalog files). `spezi-g1` placings: Schaum 1, Füllung 2, Material 3, feature axes 4 (so Material `OutputPlacing=3`, Feature `=4`). Editor: `GEN` role in the column picker + a "Rank" (`OutputPlacing`) field. 104 tests; build ×4 clean; verified in Inventor (Blech/Paneel detection, collision fix, sorted long + short). spezi-g2 (reverse long→short) not touched.

**Goal.** Verifying T42 on real `spezi-g1` data surfaced two correctness gaps in the SPEZIFIK1 → SPEZIFIK2 expansion. They live in the same pipeline and are fixed together so it lands once:

1. **Token collision.** Three codes — `fnnn`, `fpnn`, `nnnn` — mean a Blech sheet-edge ("umlaufend C-Kante 17/10") on a Blech-world part but a Paneel feature-axis stack ("Feder links") on a foam Paneel. The two readings come from a deliberate old/new ("ILKAsys Plus") token-space overlap and are never both valid at once, but they share one catalog. PairTransform's flat main-catalog lookup grabs the Blech reading first, so a Paneel short like `60-pur-g2g2-fnnn` expands the wrong long form.
2. **Output order.** The long form is assembled in token-input order (PairTransform) then card-execution order, with no sort. Advanced users *type* shorts (the picker is for newcomers) and routinely mix token order, so the long form must be re-ordered into the canonical category order — Isolation < Schaum < Material < Feature < Rest — defined by the catalog's `placing_order` column.

(The expansion — not the picker — is the primary path for advanced users; regenerating the long from the short already corrects manual long-form typos. This task adds correct **content** (collision) and correct **order** (sort).)

**Design decisions — collision (Generation scope; data-validated).**

- New catalog **"Generation" column** (a dedicated role, label "Generation"). **Blank = universal** — the row matches any generation; a generation-specific row beats a blank one for the same short. Only the 3 colliding Blech rows are tagged `Generation = Blech`; every other row stays one untouched row. (Short and long are *columns* of one row, so this never multiplies rows — cost = 1 column + 3 tagged cells. The Paneel "Feder" reading keeps coming from the existing feature-combos sub-catalog.)
- **Generation is detected from the short, not fixed on the group** (both worlds type through the same `spezi-g1`): a **Füllung/Schaum-category token present ⇒ Paneel**, absent ⇒ Blech-world. Configurable — the capability names the catalog category that flags Paneel; no hard-coded token list.
- **Scope filter** in the expansion lookup: match rows whose Generation ∈ {detected, blank}; exclude the other generation's tagged rows. A Paneel short skips the Blech `fnnn`→C-Kante row, so `fnnn` falls through to the feature-combos fallback → "Feder links"; a Blech short direct-matches `fnnn`→C-Kante and Compose `OnDirectMatch=skip` prevents a duplicate Feder.
- **Empirically validated** on the 364k-row real export (`NLi1_010+015+050+120`): the foam token disambiguates 100 % of colliding-code rows — foam-present → 16,345 (Paneel/Feder), foam-absent → 655 (all IPTs, Blech/C-Kante), and **0 of 20,336 foam-less IAMs carry these codes**. No filename/part-type detection is needed.

**Design decisions — order (placing-order sorted assembly).**

- The companion (SPEZIFIK2) is assembled by **collecting tagged segments from every contributing card, sorting by `placing_order` (Role 7 / GroupSortKey) then `internal_order` (Role 5 / SortKey), then writing once** — replacing T42's "PairTransform writes, Compose appends" sequential model.
  - Each PairTransform direct token → one segment tagged with that token's catalog `placing_order` / `internal_order`.
  - Each Compose card → one segment tagged with its category rank (see D-sort-2).
- This **supersedes T42's `OutputMode=append` + `AppendSeparator`** for this pipeline (those params remain valid for standalone Compose use elsewhere).

**Sub-decisions — RATIFIED 2026-06-26:**

- **D-sort-1 (sorted mode enable) → group-level toggle.** A per-group boolean, surfaced as a small **pictogram toggle button** in the group header, a sibling to the Expert `⚡` and built the same way (`ToggleExpertCommand` pattern — a direct group property, NOT card-driven). Glyph: a sort pictogram (`⇅`, exact glyph tweakable); **dim when OFF, amber + bold when ON** (mirrors the `⚡` highlight); a **brief hover tooltip** that only states it enables sorting (e.g. "Enables sorting — orders the output in catalog order") — the **in-depth explanation lives in the Info Window (card help via `InfoPanelBuilder`) + `Getting-Started.md`, NOT the tooltip**. Click toggles that group's output-sorting. Gates the collect-sort-write path without touching any non-SPEZIFIK group.
- **D-sort-2 (Compose segment placing) → per-card `OutputPlacing` integer** (Material card = 3, Feature card = 12). Confirmed by the two-file reduction below: with the material sub-catalog merged back into the main catalog, a sub-catalog placing column is moot, so the card-level number is the natural source.
- **D-sort-3 (replace append) → confirmed.** `spezi-g1`'s two Compose cards move from `OutputMode=append` to contributing tagged segments; the sorted assembly subsumes append. Modify-in-place on the still-uncommitted T42 config — no public/back-compat impact.
- **Two-file reduction (folded into T43).** The `001-test-spezifik-material-katalog` sub-catalog is eliminated — its only job (constrain `g2g2` sub-tokenization to material rows) is now done by a **category scope** on the Compose fallback (the same scope machinery T43 adds for Generation): the Material Compose card sub-tokenizes against the MAIN catalog restricted to the Material category instead of a separate file. Removes the material double-entry upkeep. The feature-combos catalog stays separate (it is *generated* combination data, not a subset). Net: **two catalog files** (main + feature-combos).

**Code changes (rebuild required):**

- `CardEngine`: a **Generation** column role (extend `GetRoleKey`/role parsing); a **generation-scope filter** threaded into `BuildPairTransformValue` + `BuildComposeSplitValue` / `GetComposeWritesEx` (match {gen, blank}); a **category scope** on the Compose fallback (sub-tokenize against the main catalog restricted to a named category — replaces the material sub-catalog file); a **generation detector** (short contains a token of the configured signal category ⇒ Paneel); the **sorted-assembly** path (collect tagged segments → sort by placing/internal → join once) + the `OutputPlacing` Compose param (per D-sort-2).
- `CheckupViewModel` SPECIAL:LOGIC apply path: when the group enables sorted mode (per D-sort-1), replace the sequential PairTransform-write + Compose-append loop with the collect → sort → write-once coordinator.
- `CatalogBuilderViewModel` + `CatalogBuilderWindow.xaml`: Catalog-tab editor support for the Generation role; card-editor support for `OutputPlacing` (Compose Split-Mode row) and the group flag (group header).

**Adjustments (no rebuild):**

| Artifact | Change |
|---|---|
| `Test_Spezifik.catalog.json` (×4) | Add the **Generation** column (role); tag `fnnn` / `fpnn` / `nnnn` rows `Generation = Blech`, leave every other row blank; **merge the former material sub-catalog rows back in** (they already live here as the Material category — nothing to copy if the sub-catalog was a subset; verify parity) |
| `001-test-spezifik-material-katalog.json` (×4) | **Removed** — the Material Compose card now sub-tokenizes against the main catalog scoped to the Material category (category scope on the Compose fallback) instead of a separate file |
| `Test_Spezifik.capability.json` (×4) | `spezi-g1`: turn ON the group sort toggle (D-sort-1); set the generation-signal category (Füllung); point the Material Compose card at the Material **category scope** of the main catalog (was `ComposeFallbackCatalogId` → material sub-catalog); set each Compose card's `OutputPlacing` (Material 3, Feature 12); drop `OutputMode=append` / `AppendSeparator` |
| `Addin_Language_File_EN/DE.json` | Keys for the Generation role label, the group sort-toggle tooltip, and the new card param(s) |

(Catalog files net: **two** — main `001-test-spezifik-katalog` + `001-test-spezifik-feature-kombis-katalog`.)

**Example — `g2g2-pur-60-fnnn` (deliberately mis-ordered by the user):**

| Step | Contributes | placing |
|---|---|---|
| detect generation | `pur` / `60` present ⇒ **Paneel** | — |
| PairTransform | `60`→ISO 60 · `pur`→PUR-Schaum · `fnnn`→(Blech row out of scope → no direct match) | 1, 2 |
| Material Compose | `g2g2` → `D1/D2 Edelstahl 1.4301 0,8 Korn 320` | 3 |
| Feature Compose | `fnnn` → combos fallback → `Feder links` | 12 |
| **sort by placing + join** | | `ISO 60, PUR-Schaum, D1/D2 Edelstahl 1.4301 0,8 Korn 320, Feder links` |

Even though the user typed the tokens out of order, the output is canonical.

**Tests (net8 + net48):**

- Generation: `blank` matches both scopes; a `Generation=Blech` row is skipped under Paneel scope and matched under Blech scope; generation detected from the configured signal category (foam present ⇒ Paneel).
- Collision: Paneel `60-pur-g2g2-fnnn` → "… , Feder links" (not C-Kante); Blech `g2-fnnn` → "… , umlaufend C-Kante"; `g2-fnnn` does NOT also emit Feder (`OnDirectMatch=skip`).
- Sort: mis-ordered input (`fnnn-g2g2-pur-60`) → canonical placing order; ties broken by `internal_order` then stable input order; a "Rest" token (placing > feature) lands after features.
- Regression: a group WITHOUT sorted mode behaves exactly as T42 (append order); base Compose unaffected; Demo groups unaffected.
- IPT single material (`60-pur-g2`): Compose cards emit nothing; sort is a no-op over the PairTransform segments.

**Documentation (part of done):**

- §4 / §5.8: Generation role + generation-scoped lookup + sorted-companion assembly + `OutputPlacing`.
- In-app ℹ help (`InfoPanelBuilder`) + `docs/Getting-Started.md` Compose/SPEZIFIK note.
- EN/DE parity across all 4 variants via cross-variant linking (§7.6).

**Bundling.** Lands with T41 + T42 in one commit (public parts only; `Assert-NoPrivateFiles` keeps all `Test_Spezifik` data out). The temporary T42 diagnostics (DiagLogger enable in `StandardAddInServer.cs` + the `"compose"` logging in `CheckupViewModel.cs`) are stripped in the same rebuild.

---

## Appendix A — Naming Glossary

Alphabetical quick-reference. Every term used in this TDD, conversations, and code comments should match these names exactly.

| Term                | Brief description                                                                                                                                                                                                                                                                                          |
|---------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Action Button**       | Optional button at the far right of the Value Field frame; opens a secondary window (e.g. Picker Window)                                                                                                                                                                                                   |
| **Basic Logic**         | Formula-driven function inside a Group; purely computational; no catalog required; rudimentary spreadsheet-style formulas (IF, CONCAT, ROUND, etc.)                                                                                                                                                        |
| **Bottom bar**          | The bottommost row of the main window; contains Style Purger (left), Preset buttons (centre), Info/Reset/Close (right)                                                                                                                                                                                     |
| **Capability Set**      | Named container holding one or more Groups; the top-level organisational unit in the Logics-Constructor                                                                                                                                                                                                     |
| **Card**                | One logic brick inside a Group; catalog-backed or higher-level; may have interactive visual component (Dropdown, Button, Search, Link, Sync, MultiPick, PairTransform, Compose, PrefixSuffix, Sort, BasicLogic)                                                                                                                            |
| **Catalog**             | Named data table with columns and entries; the data source for Dropdown/Button/Search cards                                                                                                                                                                                                                |
| **Document Name Field** | Header bar element showing the active/selected document filename(s); three view modes (Plain/Compact/Detailed) cycled via the View Mode button (label `S ⇄` / `C ⇄` / `D ⇄`) or left-click; auto-wraps/trims at 2 lines (Plain/Compact) or 5 lines (Detailed); full text on tooltip; mode persisted in Registry                                                                   |
| **Drag Handle**         | The 2×3 dot grid used as the sole initiation point for drag-and-drop reordering. In the main window: far left of every Row (before the Value Field). In the Logics-Constructor: also appears in Group header bars (reorders Groups) and in Card/Basic Logic rows (reorders items within or between Groups). |
| **ESC key**             | Closes the active add-in window — same effect as the dedicated close button                                                                                                                                                                                                                                |
| **Factory size**        | The code-defined default window dimensions; restored on Reset                                                                                                                                                                                                                                              |
| **FallbackLabel**       | Text shown inside the Field Selector when no field is assigned or the field is missing                                                                                                                                                                                                                     |
| **Field Catalog**       | Runtime-discovered set of all fields available in the Field Selector                                                                                                                                                                                                                                       |
| **Field Key**           | Stable string identifier for a property (`DOC:`, `IPROP                                                                                                                                                                                                                                                      |
| **Field Selector**      | The ComboBox on each Row that chooses which property the Row reads                                                                                                                                                                                                                                         |
| **Flange Distance**     | ⚠ **Removed (Task #29).** Was a computed sheet metal value (`SPECIAL:FlangeDistance`). Fully removed — no catalog entry, no resolver, no `SheetMetalReader`. Old presets degrade to a missing-field row.                                                                                                                                                |
| **Group**               | One logic unit inside a Capability Set; corresponds to one `SPECIAL:LOGIC:` row / one `S:` entry in the Field Selector                                                                                                                                                                                         |
| **Header bar**          | The topmost row of the main window (above all data rows); contains (left→right): View Mode Cycle button (label `S ⇄` / `C ⇄` / `D ⇄`), "File:" label, Document Name Field, Logics-Constructor button                                                                                              |
| **Logics-Constructor**   | The complete feature (card-based logic for SPECIAL:LOGIC: rows) and the window for configuring it. German: *Logik Baukasten*. Code classes: `CatalogBuilderWindow`, `CardEngine`, `CatalogStore`, `CapabilityStore`. Retired name: *Logic Builder*                                                                     |
| **Logic Row**           | A Row whose Field Key starts with `SPECIAL:LOGIC:` — the only rows cards apply to                                                                                                                                                                                                                            |
| **Miter Gap**           | ⚠ **Removed (Task #29).** Was an editable sheet metal value (`SPECIAL:MiterGap`, German: "Gehrungslücke"). Fully removed — no catalog entry, no resolver, no `SheetMetalReader`. Old presets degrade to a missing-field row.                                                                                                                              |
| **MultiPick Card**      | Card enabling multi-token input with per-separator autocomplete                                                                                                                                                                                                                                            |
| **Picker Window**       | Full window (not popup) for browsing and selecting catalog entries — opened from a **Button card** row. `SpeziBaukastenPickerWindow` is the legacy variant (used by old `SPECIAL:Spezi1/2` rows). `CatalogPickerWindow` is the Logics-Constructor variant.                                                            |
| **Preset**              | Named saved row-layout configuration. Default names: Part (German: "Bauteil") and Assembly (German: "Baugruppe"). All preset names and row layouts are user-configurable.                                                                                                                                                                                                                                   |
| **Reset**               | Returns window to factory size, reloads default preset, AND clears persisted Logics-Constructor panel states (via `UiStateStore.ClearCatalogBuilderPanelStates()`). After Reset, the next Logics-Constructor open uses factory defaults: Cards=open, Basic Logics=closed.                                                                                                                                                                                                                          |
| **Row**                 | One configurable entry in the main grid: Field Selector + Value Field pair                                                                                                                                                                                                                                 |
| **Source Object**       | The Inventor document(s) currently being read (active IPT or selected component(s))                                                                                                                                                                                                                        |
| **Special Field**       | Any field with a `SPECIAL:` prefix — computed or catalog-backed; not a raw iProperty/parameter                                                                                                                                                                                                               |
| **Spezi Baukasten**     | ⚠ Legacy. Catalog-backed IZ specialty designation system; produces Spezi1 (short) + Spezi2 (long) values. Replaced by Logics-Constructor capability set `IZ_Spezis_Baukasten.capability.json`. Catalog transitioned from CSV to CatalogStore JSON.                                                            |
| **Spezi1 / Spezi2**     | ⚠ **Removed (Task #29).** Were the two IZ Spezifik fields (`SPECIAL:Spezi1` / `SPECIAL:Spezi2`), backed by `SPEZIFIK1/2`. Fully removed — no resolver code remains. Replaced by Logics-Constructor groups.                                                                                                                                      |
| **Style Purger**        | Feature that removes unused styles from IDW/IPT/IAM documents                                                                                                                                                                                                                                              |
| **Theme System**        | Detects Inventor light/dark scheme and swaps the add-in's visual resource dictionaries                                                                                                                                                                                                                     |
| **Value Field**         | UI cell spanning Drag Handle → Field Selector; shows read value; single left-click → full-width inline edit frame; can host Dropdown and/or Action Button (far right)                                                                                                                                      |

> Section 8 (UI Vocabulary) contains the full table with code identifiers. This appendix is the quick-reference version.

## 11. Key File Locations

| File / Path                                               | Purpose                                                                                                                                                                                                                                    |
|-----------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `CheckupAddin.Shared\`                                      | VS Shared Project — all common C# and XAML; compiled into all 4 variants; edit here once, builds everywhere (see §7.8)                                                                                                                    |
| `CheckupAddin.Shared\CheckupAddin.Shared.projitems`         | MSBuild item list imported by each head csproj; lists every shared file                                                                                                                                                                   |
| `CheckupAddin2026\CheckupAddin2026\`                        | 2026 **head project** (thin): StandardAddInServer, AppConstants, InternalsVisibleTo, .csproj, .addin; also the **canonical source** for shared resource files (DarkTheme.xaml, LightTheme.xaml, language JSON/XAML)                       |
| `CheckupAddin2024\CheckupAddin2024\`                        | 2024 head project (thin): same identity files; DarkTheme/LightTheme/language JSON linked from 2026 (no physical copies)                                                                                                                   |
| `CheckupAddin2025\CheckupAddin2025\`                        | 2025 head project (thin): same linking rules as 2024                                                                                                                                                                                      |
| `CheckupAddin2027\CheckupAddin2027\`                        | 2027 head project (thin): same linking rules as 2026 (also links DE.xaml from 2026)                                                                                                                                                       |
| `DesignHarness\`                                            | Build-time designer tool; standalone WPF exe; `ProjectReference` to 2026; not shipped (see §7.7)                                                                                                                                         |
| `DesignHarness\bin\CheckupAddIn.DesignHarness.exe`          | Design Harness executable (flat output path — no framework/RID subfolder)                                                                                                                                                                |
| `DesignHarness.slnx`                                        | Solution file for the Design Harness (opens in VS 2022 v17.14+; set DesignHarness as startup project, then F5)                                                                                                                           |
| `docs\CheckupAddin - Technical Design Document.md`          | This TDD                                                                                                                                                                                                                                   |
| `bin\Catalogs\`                                             | Distribution + dev catalogs; created by build (`CreateDevSubfolders`); never overwritten by build; shipped files added as `CopyToOutputDirectory=PreserveNewest` in `.csproj` for V1.0                                                           |
| `bin\Capabilities\`                                         | Distribution + dev capabilities; same rules as `bin\Catalogs\`                                                                                                                                                                               |
| `%APPDATA%\Checkup 2026\Catalogs\`                          | Per-user catalog edits; survives Clean Solution; never touched by build                                                                                                                                                                    |
| `%APPDATA%\Checkup 2026\Capabilities\`                      | Per-user capability edits; same rules as AppData Catalogs                                                                                                                                                                                  |
| `%PROGRAMDATA%\Autodesk\Inventor 2026\Addins\`              | 2026 addin manifest location                                                                                                                                                                                                               |
| `%APPDATA%\Autodesk\ApplicationPlugins\`                    | 2024 addin manifest location                                                                                                                                                                                                               |
| `V:\CAD\INV\Templates\Standard.idw`                         | IDW style template (deploy value)                                                                                                                                                                                                          |
| `Bereinigen IDW+IPT+IAM.iLogicVb`                           | iLogic port of StylePurger (kept in sync with StylePurger.cs)                                                                                                                                                                              |
| `Checkup_Settings.json`                                     | Main settings file (presets + StylePurge config + SpeziBaukastenCatalogPath); deployed next to DLL; loaded at startup as `UserSettings` object                                                                                               |
| `Addin_Language_File_DE.json` / `Addin_Language_File_EN.json` | UI strings; deployed next to DLL; loaded by `LanguageLoader` at startup                                                                                                                                                                      |
| `Spezi_Katalog.csv`                                         | ⚠ Legacy one-time import seed. On first Inventor load the addin imports this CSV into CatalogStore (ID `spezi001`) and never reads it again. Can be removed from the project source once all deployments have completed the one-time import. |
| `Checkup_Catalogs.json`                                     | Seed catalog data; in project source → copied to bin on build; migrated to AppData on first Inventor load                                                                                                                                  |
| `Checkup_Capabilities.json`                                 | Seed capability sets; same pattern as Checkup_Catalogs.json                                                                                                                                                                                |
| `Resources\Demo.catalog.json`                               | Demo / tutorial catalog (100 entries: RAL colors + Inventor materials); copied to `bin\Catalogs\Demo.catalog.json` on build; ID `demo-001-catalog`; `IsLocked: true`                                                                       |
| `Resources\Demo.capability.json`                            | Demo / tutorial capability set (12 groups — all card types); copied to `bin\Capabilities\Demo.capability.json` on build; ID `demo-001-capabilities`; `IsLocked: true`                                                                     |

---

## 12. Code Quality Guidelines

When contributing code, observe the following rules:

- **COM two-dot rule:** never chain two COM property accesses without an intermediate variable (e.g. `doc.ComponentDefinition.Parameters` → store `doc.ComponentDefinition` first). Avoids RCW lifetime issues.
- **Async safety:** all COM calls must run on the STA thread. Never call Inventor COM objects from `Task.Run`, `async void`, or background threads. `.Wait()` / `.Result` on a WPF Dispatcher thread will deadlock.
- **IValueConverter:** never throw in `Convert` / `ConvertBack` — return `DependencyProperty.UnsetValue` or the original value on failure.
- **Log injection:** all values written to `DiagLogger` must be newline-stripped first — use `DiagLogger.S(value)`.
- **Path construction:** always use `Path.Combine` — never string concatenation with `\`.
- **String comparison:** use `StringComparison.Ordinal` for all internal key/tag comparisons; `OrdinalIgnoreCase` only where case-insensitive matching is semantically required.
- **INPC completeness:** every property that a binding can observe must raise `OnPropertyChanged()` in its setter, including computed / derived properties.
- **`StylePurger` loop pattern:** `while (collection.Count > 0)` with live `.Count` is intentional — items are deleted during iteration; caching the count would break deletion. Do not refactor this loop.
- **2024 porting:** see §7.1 for the full .NET 4.8 / C# 12 equivalence table.

---

## 13. Licensing & Dependencies

### License

This project is released under the **GNU General Public License v3.0 (GPL-3.0)**.
Full text: `LICENSE` in the repository root.

Key implications for contributors:
- Anyone who distributes a modified version must release their source under GPL-3.0 as well.
- Commercial internal use is permitted without disclosure; distribution of modified binaries is not.
- The GPL-3.0 includes an explicit patent non-aggression clause protecting end users.

### Third-Party Dependencies

Full notices: `THIRD_PARTY_NOTICES` in the repository root.

| Component | License | Notes |
|---|---|---|
| Autodesk Inventor Interop (`Autodesk.Inventor.Interop.dll`) | Proprietary (Autodesk) | **Not redistributed** (gitignored, never committed, not shipped in any release zip). Each variant references its own version's PIA from `lib\<year>\`, copied per-machine from the matching local Inventor install by `fetch_interop.ps1`. Requires a licensed copy of the matching Autodesk Inventor (2024–2027) on the build machine. |
| System.Drawing.Common (NuGet, v8.0.0) | MIT | Used in the .NET 8 variants (CheckupAddin2026 / 2027). |
| .NET 8 runtime, WPF, Microsoft.VisualBasic.Core | MIT | Part of the .NET platform. `Microsoft.VisualBasic.Interaction.CallByName` is used for late-binding COM property access throughout the service layer. |
| Newtonsoft.Json (NuGet) | MIT | Used in the .NET 4.8 variants (CheckupAddin2024 / 2025) in place of System.Text.Json. Must ship in the release zip (packaged by `build_release.ps1`) — .NET Framework has no fallback resolution for a missing PackageReference DLL; this was missing from packaging until fixed 2026-06-16. |

### Inventor API Caveat

The GPL-3.0 formally requires all linked libraries to be free software. `Autodesk.Inventor.Interop.dll` is proprietary. This is handled by the established open-source add-in practice: the assembly is never distributed with the project, users must own a licensed copy of Inventor, and the interop DLL is solely an interface layer to a separately-licensed host application. This situation is analogous to the "system library" exception and is universally accepted for Inventor/Revit/SolidWorks add-ins.

**The release zip does not run as-is** — `CheckupAddIn.dll` hard-references `Autodesk.Inventor.Interop.dll` (it's a real type-load dependency, not resolved by Inventor's own native process), so the user must copy it from their own Inventor install (`...\Inventor <year>\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll`, matching the variant) into the extracted add-in folder before first load — see README "Copy the Inventor Interop assembly" step. Confirmed empirically (`AssemblyLoadContext` probe, 2026-06-16): without the file present next to `CheckupAddIn.dll`, `Assembly.GetTypes()` throws `FileNotFoundException` for `Autodesk.Inventor.Interop`; `CheckupAddIn.deps.json` and `CheckupAddIn.pdb`, by contrast, are **not** required at runtime (deps.json's absence doesn't block load — the default ALC probing falls back to the app-base directory; pdb is debug symbols only). Each variant is built against **its own** Inventor version's copy of the interop DLL (`lib\<year>\`, populated by `fetch_interop.ps1`; see §7.2 / [[project_2024_vs_2026_differences]]), so a user installs the variant matching their Inventor and copies that same version's interop. The earlier limitation — everything built against the 2026 PIA, leaving an Inventor-2024-only machine with no validated path — no longer applies.

---

## 14. Change History

Public release history.

### v0.15.0 — Compose card + Split Mode + generation-scoped SPEZIFIK pipeline + responsive card editor (2026-06-27)

- **Compose card** (Task #41): a new general-purpose Logics-Constructor card. Longest-match split-lookup tokenizes a separator-less packed code against a catalog column and emits framed text (configurable per-item prefixes, collapse-when-equal, empty-output drop, max-items, on-unknown mode). The no-separator sibling of PairTransform. See §5.8.
- **Compose Split Mode** (Task #42): six optional Compose params (`SourceSeparator`, `TokenOutputSeparator`, `ComposeFallbackCatalogId`, `OnDirectMatch`, `OutputMode`, `AppendSeparator`) let one card process a separator-delimited source where some tokens are direct catalog entries and others are packed sub-codes; multiple Compose cards can append into one companion field. See §5.8 + §10.2.
- **Generation-scoped expansion + placing-order sort** (Task #43): a new catalog **Generation** column (blank = universal) plus a source-derived generation scope filter resolves same-code collisions between catalog generations; the assembled long form is sorted on `placing_order` and the short form is canonicalized. See §10.3.
- **Multi-tab catalog membership** (Task #40): one catalog value row can appear under several picker tabs via comma-separated tab names in its TAB cell. See §10.1.
- **Responsive Logics-Constructor card editor** (Task #44): card fields wrap to the available width with each label+field pair kept together; the companion-field picker shares the same flow; each card gets its own horizontal scrollbar below a width floor, while the outer view keeps scrolling the whole list vertically. See §5.8.
- Engine unit tests grown to 104 (net8 + net48); build clean across all four variants.

### v0.13.2 — DesignHarness public + VS Shared Project restructure + multi-version manifest fix (2026-06-17)

- **DesignHarness now public and shipped:** included in release bundles as `CheckupDesignHarness_<Tag>.zip`; packaged by `build_release.ps1` (default; opt-out with `-SkipHarness`). Requires .NET 8 Desktop Runtime + Inventor 2026 interop DLL from user's install. Source already in the public repo under `DesignHarness\`.
- **VS Shared Project restructure** (Task #37): all common C# and XAML moved from 4 full copies into `CheckupAddin.Shared/` (`.shproj` + `.projitems`). The four head project directories now contain only variant-specific identity files (`StandardAddInServer.cs`, `AppConstants.cs`, `InternalsVisibleTo.cs`, `.csproj`, `.addin`). Future code changes go in the shared project once and compile into all 4 variants automatically (see §7.8).
- **Assembly name unification**: all 4 variants now produce `AssemblyName = CheckupAddIn` → `CheckupAddIn.dll`. Pack URIs are uniform: `/CheckupAddIn;component/...`. The 2024 and 2025 release zips were previously named `CheckupAddIn2024.dll` / `CheckupAddIn2025.dll`; these are now `CheckupAddIn.dll` in all zips.
- **`AppConstants.cs`** added to each head project: provides `RegistryBaseKey` and `AppDataFolderName` year-specific constants consumed by shared `UiStateStore` and `PresetsManager`.
- **net48 polyfills** (`IsExternalInitPolyfill.cs`, `RangeIndexPolyfill.cs`, `SharedGlobalUsings.cs`): enable C# 9 `init` setters, C# 8 range/index syntax, and implicit usings on .NET Framework 4.8 builds.
- **`#if NET48` dual-coding** applied to 6 shared files that use `System.Text.Json` (CapabilityStore, CatalogStore, PresetsManager, UserSettings, CapabilityData, CatalogData): each switches between `System.Text.Json` (net8) and `Newtonsoft.Json` (net48) at the call site.
- **Add-in manifest version gating fixed**: every `.addin` / `.addin.template` now sets **both** `SupportedSoftwareVersionGreaterThan` *and* `SupportedSoftwareVersionLessThan`, pinning each variant to exactly one Inventor release. Previously the open-ended lower bound let a lower-year variant load in newer Inventor versions when placed in the version-independent add-in folder (see §7.4).
- **Language parity fix**: added the missing `Info_Main_Header` key to the EN language file and extended `Info_Main_RightClick` with the hover-tooltip sentence (DE↔EN now identical, 309 keys each).

### v0.13.2 — Design Harness + cross-variant resource linking (Task #36) (2026-06-17)

- **Design Harness** (`DesignHarness\`): new standalone WPF build-time tool allowing a designer to preview all 8 add-in windows, live-edit colors / labels / typography, and export changes directly back to the 2026 source XAML and JSON files (see §7.7).
- **Cross-variant resource file linking** (Phase 2 of Task #36): `DarkTheme.xaml`, `LightTheme.xaml`, `Addin_Language_File_DE.json`, `Addin_Language_File_EN.json` are now **canonical in the 2026 project** and MSBuild-linked into 2024/2025/2027 via `<Page Include>` + `<Link>` and `<None Include>` + `<Link>` items. Physical copies deleted from 2024/2025/2027. `Addin_Language_File_DE.xaml` additionally linked into 2027 (net8 only; net48 guardrail for 2024/2025 — see §7.6).
- **`UiStateStore.DesignMode`**: new `public static bool` flag; when `true`, all five `TryLoad*` size methods skip registry reads and return `false`, so harness preview windows always open at factory/XAML sizes (see §3 Services table / §7.7).
- **`InternalsVisibleTo("CheckupAddIn.DesignHarness")`**: added to `InternalsVisibleTo.cs` in all four variants, granting the harness access to `internal` members.
- **`CheckupFontFamily` / `CheckupBaseFontSize`** typography resources added to both theme XAML files; `CheckupWindow.xaml` now binds `FontFamily` / `FontSize` via `DynamicResource`, enabling live font-editing from the harness.

### v0.9.9 — Initial public release (2026-06-06)

- First public version; builds for Autodesk Inventor 2026 (.NET 8.0) and 2024 (.NET 4.8).
- Added a user-facing visual guide (`docs/Getting-Started.md`) with GIFs.
- Documented installation via the Inventor Add-In Manager unblock flow for the unsigned add-in.
- Standardized the "Logics-Constructor" naming across the UI strings and docs.
