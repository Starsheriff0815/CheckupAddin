# Getting Started with Checkup — A Quick Visual Tour

Scroll through and watch the clips to see what Checkup does and where everything
lives. No setup knowledge needed.

> Looking for how to **install** it? See the [README](../README.md).
> Looking for the **technical** details? See the
> [Technical Design Document](CheckupAddin%20-%20Technical%20Design%20Document.md).
> Looking for a **plain-language list of every function**? Jump to the
> [Function Reference](#function-reference) further down this page.

---

## The Main Checkup Window

![The main Checkup window](images/main-window-v2.gif)

> **Note:** This Clip still Shows the Previous Version (Purge Styles Button and the old three Preset Buttons) — an Updated Clip will Follow Soon.

*The Main Addin Window Presents Users with a Maximum of 30 Rows which can be Sorted via Drag & Drop Handles.
Users can Add / Remove Rows from the Dropdown Menu on the Right. And Select, for the Row on which the Dropdown Menu
is Opened, any Value Present in the Object which is Selected in Inventor's Model Space / Model Browser.
The Addin either Lists a Single or Multiple Selected Objects. Within an Assembly (IAM) or a Part (IPT) it Lists the
Document itself if nothing is Selected.
At the Bottom Left of the Addin Window are the Preset Buttons (up to 12) Editable by Users through a Single Mouse Right Click (Context Menu).
The "+" Button Adds a Copy of the Active Preset, and Preset Buttons which do not Fit are Listed under "More ›".
On the Bottom Right is the "I" (Info) Button, the "Reset" Button and the "Close" Button.
At the Top Right is the "Logics-Constructor" Button Opening a Separate Window.
(see Info Windows for Further Information)*

---

## Logics-Constructor — Catalog Tab

![The Logics-Constructor on the Catalog tab](images/logics-constructor-catalog.gif)

*When Opening the Logics-Constructor Window from the Main Addin Window, Users will be Presented with the Option to
Switch via the "Catalogs" and "Capabilities" Tabs on the Top Left.
This Switches Views so Users can Create, Edit, Delete, Export, Import, Lock and Unlock Catalogs.
The Addin has Multiuser Workflow Rules Built in. This Means: when Catalogs / Capabilities are Stored on a UNC Path,
this is Automatically Detected and Sets a Locked State! When Users Unlock, the Catalog / Capability will be Migrated to
Local User Space and Set to Unlocked as a Safeguard. This is also the Intended Way for Updating Team-Managed Catalogs / Capabilities:
Unlock -> Edit -> Export to UNC Path to Replace the File (see Documentation).
For Example, the Next Day when Users Start Inventor, the Catalogs / Capabilities Read the Latest Data. Or, if they Migrated
Catalogs / Capabilities (through Unlocking) to their User Space, they can See that their Local Copy is Out of Date.
And they can Delete their Local Copy from within the Addin, which will Notify that a Restart of Inventor is Mandatory.*

---

## Logics-Constructor — Capabilities Tab

![The Logics-Constructor on the Capabilities tab](images/logics-constructor-capabilities.gif)

*When the "Capabilities" Tab is Active, Users will See Existing Capabilities in Groups. Each Group Represents one Special
Function which will be Listed in the Main Addin on the Dropdown Menu (the "S:" Entries). Each Group can be Named and
Rearranged via Drag & Drop.
At the Bottom is a Collapsible "Cards" Palette holding the Card Types Users can Add to the Active Group — Button, Dropdown,
Link, PairTransform, Compose, Prefix/Suffix, Search, MultiPick, Sort and Sync. A Single Click adds the Chosen Card to the Active Group.
On the Far Right is a Collapsible "Basic Logics" Panel holding Spreadsheet-Style Formula Functions (IF, LOOKUP, CONCATENATE,
ROUND and more). Clicking one adds a Basic Logic Card with the Function Skeleton Pre-Filled.
Cards and Basic Logics inside a Group Run from Top to Bottom, so Users can Reorder them to Control which Value Feeds the Next.
At the very Bottom is the Toolbar: on the Left the "+ Add Group" Button; on the Right the "I" (Info), ▲ (Up), ▼ (Down),
⧉ (Duplicate) and × (Delete) Buttons which Act on the Active Group or Card.*

---

## Function Reference

> **Work in Progress** — Annotated screenshots and detailed descriptions are
> being added. The outline below shows the planned structure; all section
> headings and numbered items are already in place.

A plain-language companion to the
[Technical Design Document](CheckupAddin%20-%20Technical%20Design%20Document.md):
the same feature set, explained for users instead of coders. Each window below
has **one annotated screenshot** — the numbered markers on the screenshot match
the numbered list underneath it. Where a short clip explains a step better than a
still image, an optional GIF can be added under the relevant item.

<!-- DRAFT SKELETON — brief seed descriptions only; expand the text in your own words.
     IMAGE PLACEHOLDERS (all are HTML comments, so nothing renders broken):
       • One SCREENSHOT per window — annotate it with numbered markers (1, 2, 3 …)
         that line up with the numbered list below it.
         Replace the comment with: ![alt text](images/filename.png)
       • Each list item also has an OPTIONAL detail-clip slot for a GIF, in case a
         single function needs its own animation. Delete the ones you do not use;
         add more wherever you like. -->

---

### The Main Checkup Window

<!-- SCREENSHOT — annotate with numbered markers matching the list below.
     Replace with: ![The main window, annotated](images/main-window-annotated.png) -->

The numbers below correspond to the markers on the screenshot:

1. **The Row Grid** — Shows up to 30 rows of values from the part or assembly you
   are looking at. Drag the dotted handle on the left of any row to reorder it.
   <!-- optional clip → ![Row reorder](images/row-reorder.gif) -->
2. **The Field Selector (right-hand dropdown)** — Decides which value each row
   shows. It has a search box at the top, "Add Row" / "Remove Row" actions, a
   Favorites area you fill by right-clicking entries to pin them, and the full
   list of available values grouped by where they come from. Values that do not
   exist on the current object appear greyed out and struck through; special
   functions are tagged "S:" in red — "Run iLogic Rule" first (see 8), then the
   functions you built in the Logics-Constructor.
   <!-- optional clip → ![Field Selector](images/field-selector.gif) -->
3. **Viewing and editing a value** — Each row's value field shows what was read
   from Inventor. Click it once to edit inline and write the new value straight
   back to the model. Right-click a value to copy it to the clipboard.
   <!-- optional clip → ![Editing a value](images/value-edit.gif) -->
4. **Editing formulas (fx)** — Beyond plain values, you can edit iProperty
   expressions and parameter equations, so the underlying formula updates rather
   than just the displayed result.
   <!-- optional clip → ![Formula editing](images/fx-edit.gif) -->
5. **What Checkup reads (the source object)** — Reads the active part, the
   component(s) you select in an assembly, or — if nothing is selected — the open
   part or assembly document itself. You can select several parts at once.
   <!-- optional clip → ![Source object](images/source-object.gif) -->
6. **The document-name header** — Shows the file name of whatever is being read.
   When you select more than one object, all names are listed and the text turns
   red as a reminder that you are in multi-select. When multiple instances of the
   same component are selected in an assembly, a `(N)` counter appears next to the
   name — e.g. `Schraube_M8.ipt (6)`. Use the **view-mode button** on the left of the
   header (its label shows the current mode and a `⇄` arrow — `S ⇄`, `C ⇄`, `D ⇄`)
   to cycle through three display modes:
   - **Plain (S)** — filename + count (default)
   - **Compact (C)** — adds how many sub-assemblies contain this component, e.g. `(6, 2 IAM)`
   - **Detailed (D)** — groups by sub-assembly, e.g. `Baugruppe.iam > Schraube_M8.ipt (3)`;
     components directly in the top assembly are listed under the assembly's own name
   The selected mode is remembered between sessions (and resets to **S** when you press
   Reset). The header wraps to at most 2 lines (5 in Detailed) and then trims; hover
   over it to see the full text. Right-click the file name to copy the shown text to
   the clipboard, just like a row value. Long row values follow the same 2-line rule.
   ![The document-name header showing the instance counter across different selections and the three S / C / D display modes](images/main-window-Instances-counter.gif)
   > **Note:** This Clip still Shows the Previous Version of the Window — an Updated Clip will Follow Soon.
7. **Presets** — The buttons at the bottom left store row layouts you use often.
   A fresh install (and **Reset**) starts with a single **Demo** preset — or with
   your company's presets, if your CAD administrator put them into
   `Checkup_Settings.json`.
   - **Click** a preset to switch to it. The active preset has a blue frame.
   - **"+"** adds a copy of the active preset — including row changes you have not
     saved yet — named e.g. `Demo (2)`, and makes it active. Up to **12** presets.
   - **Right-click** a preset to save the current rows into it (this is also where
     you rename it), export it (or all presets), import, or delete it.
   - **Drag** a preset to reorder. When the window is too narrow, the presets that
     do not fit move into **More ›** — click it to switch to them, or drag them
     back onto the bar. Drop a preset onto **More ›** to move it to the end.
   - **Import** lets you tick one or more presets from a file: **Replace this
     preset** overwrites the right-clicked one; **Add as new** adds them as new
     buttons (if a name or ID already exists, you choose to overwrite or add).
     This lets you build a personal library and share it between machines.
   <!-- optional clip → ![Presets](images/presets.gif) -->
8. **Run iLogic Rule** — Turn any row into a one-click button for an iLogic rule.
   Pick **S: Run iLogic Rule** in the Field Selector: the row shows a button that
   says *Right Click to Set iLogic Rule*. **Right-click** it to choose a rule — the
   list shows the rules stored in the open document, the rules in your iLogic rule
   folders (as set in Inventor's iLogic Configuration), grouped by folder, and the
   rules that come with Checkup (group "Checkup"). The button then shows the rule's
   name; a **left-click** runs it on the open document, exactly as if you ran it
   from Inventor's iLogic browser. Right-click again at any time to pick another
   rule. Save the row into a preset to keep it. If the rule file cannot be found
   (for example on another PC), the button is greyed out and struck through.
   Checkup never saves your files — save manually afterwards.
   - **Style cleanup rules included:** *Bereinigen IDW+IPT+IAM* cleans up unused
     styles in the open part, assembly or drawing (this replaces the former
     "Purge Styles" button). *Purge Styles Selection IDW+IPT+IAM* does the same, but
     when an assembly is open and parts or sub-assemblies are selected, it cleans the
     selected objects — a selected sub-assembly including everything below it.
   - **Before you use them:** both rules start with a CONFIGURATION block (drawing
     template path, border, title block and sketched symbol names). The shipped
     values are examples — **copy the rule into your own iLogic rule folder and
     adapt the configuration to your paths and names there.** Checkup updates
     replace the files in its own `Rules` folder, so edits made there are lost.
   <!-- optional clip → ![Run iLogic Rule](images/run-ilogic-rule.gif) -->
9. **Info, Reset, Close and the status line** — The bottom-right buttons open the
   built-in help, reset the layout to defaults, and close the window. The small
   status line just above them reports the result of edits, rule runs, and refreshes.
   The main window and the Logics-Constructor open again where you last closed them —
   same monitor, position and size (the Logics-Constructor also stays maximized).
   Info windows and dialogs always open on top of the window they belong to.
   **Reset** brings all window and dialog sizes back to their factory values and
   centers the main window on the monitor Inventor is running on.
   <!-- optional clip → ![Bottom-right buttons](images/bottom-right-buttons.gif) -->
10. **Automatic behaviors** — The grid keeps itself up to date when you switch
    documents or change your selection, follows Inventor's dark or light theme
    automatically, and displays German or English to match Inventor's language.

---

### The Logics-Constructor — Catalogs Tab

<!-- SCREENSHOT — annotate with numbered markers matching the list below.
     Replace with: ![The Catalogs tab, annotated](images/catalogs-tab-annotated.png) -->

The numbers below correspond to the markers on the screenshot:

1. **What a catalog is** — A named table of columns and rows that feeds the
   special functions — think of it as the lookup data behind a dropdown or a
   search.
2. **Creating and editing catalogs** — Create, rename, and delete catalogs, and
   edit their contents in a spreadsheet-style grid with copy/paste, fill-down, and
   insert/delete of rows and columns. Click a row-number to select the whole row;
   click a column-header body to select the whole column; hold Ctrl or Shift to
   add to or extend the selection. Right-click the selection to delete all chosen
   rows or columns in one step. Click the sort caret (⇅/▲/▼) in a column header
   to sort ascending or descending.
   <!-- optional clip → ![Catalog editor](images/catalog-editor.gif) -->
3. **Importing and exporting catalogs** — Share a catalog as a file and import one
   from elsewhere, so catalogs can move between machines or teammates.
   <!-- optional clip → ![Catalog import/export](images/catalog-import-export.gif) -->
4. **Lock / unlock and the multi-user workflow** — Catalogs stored on a network
   (UNC) path are detected automatically and shown as locked / read-only.
   Unlocking makes a personal local copy (the shared original is never changed).
   The intended way to update a team catalog is: Unlock → Edit → Export back to
   the network path. When a shared catalog is newer than your local copy, Checkup
   offers an "Update" button.
   <!-- optional clip → ![Catalog lock workflow](images/catalog-lock.gif) -->
5. **Picker tabs and multi-tab values** — Give a column the **TAB** role and each
   unique value becomes a tab in the picker dialog. To list the same row under more
   than one tab, put several tab names in a single cell separated by commas
   (e.g. `Profiles, Brackets`) — the entry then appears under each of those tabs.
   <!-- optional clip → ![Multi-tab picker](images/picker-multi-tab.gif) -->

---

### The Logics-Constructor — Capabilities Tab

<!-- SCREENSHOT — annotate with numbered markers matching the list below.
     Replace with: ![The Capabilities tab, annotated](images/capabilities-tab-annotated.png) -->

The numbers below correspond to the markers on the screenshot:

1. **What a capability and a group are** — A capability set holds one or more
   Groups. Each Group is one special function and shows up as a single "S:" entry
   in the main window's Field Selector. The Group's name becomes that entry's
   label.
2. **Managing groups** — Add, name, reorder (drag and drop, or the ▲ / ▼ buttons),
   duplicate, and delete groups. Each group gets its own accent color for easy
   visual tracking.
   <!-- optional clip → ![Group management](images/group-management.gif) -->
3. **The Cards palette** — Cards are the building blocks you add to a group from
   the palette at the bottom. A single click adds the chosen card to the active
   group:
   <!-- optional clip → ![Cards palette](images/cards-palette.gif) -->
    - **Button** — adds a button that opens the Catalog Picker window to choose an entry.
    - **Dropdown** — offers a fixed list of catalog choices directly in the row.
    - **Link** — pulls its value from another (partner) field.
    - **PairTransform** — looks up an input value and replaces it with a paired output value from a catalog.
    - **Compose** — splits a single packed code (no separator) into catalog codes by longest match, looks each up, and writes framed text to a companion field. The no-separator companion to PairTransform — e.g. a two-sided `rdbl` becomes "Front Red / Back Blue", `rdrd` collapses to "Both sides Red". Handles mixed-length codes and can drop codes whose value is empty.
    - **Prefix/Suffix** — adds (or removes) fixed text before or after a value.
    - **Search** — type-ahead search against a catalog's entries.
    - **MultiPick** — lets you pick several values at once, kept in sync with a companion field.
    - **Sort** — sorts multi-part values using a catalog and a separator.
    - **Sync** — keeps a companion field in step with this one.

   A group also has a **⇅ Sort toggle** in its header (next to the Expert `⚡`). When on, the group's assembled output — and the typed short itself — are reordered into the catalog's canonical sequence (its placing/sort columns), so tokens entered in any order come out consistently. An optional per-row **Generation** tag lets a single code mean different things by context (e.g. a sheet-metal edge vs a panel feature), chosen automatically from the input.
4. **Basic Logics** — The collapsible panel on the right holds spreadsheet-style
   formula functions you can drop into a group. Clicking one adds a card with the
   formula skeleton pre-filled. Examples:
   <!-- optional clip → ![Basic Logics](images/basic-logics.gif) -->
    - **IF / ELSE** — choose a value based on a condition.
    - **LOOKUP** — fetch a value from a catalog by key.
    - **CONCATENATE / JOIN** — combine several values into one.
    - **ROUND, ABS, VALUE** — number handling.
    - **LEFT, RIGHT, MID, TRIM, UPPER, LOWER, REPLACE** — text handling.
    - … and more — the panel lists the full set.
5. **Card order and the toolbar** — Cards and Basic Logics run from top to bottom,
   so reorder them to control which value feeds the next. The toolbar at the
   bottom has "+ Add Group" on the left and Info (ℹ), Up (▲), Down (▼), Duplicate
   (⧉) and Delete (×) on the right, acting on whichever Group or Card is active.

---

### Across All Windows

#### The Catalog Picker Window

<!-- SCREENSHOT / clip → ![Catalog Picker](images/catalog-picker.png) -->

Opened from a Button card, this full window lets you browse a catalog and pick an
entry to drop into the row.

#### Built-in Info Buttons

Every window has its own "i" (Info) button with a short quick-guide built right
in — open it any time for a refresher on that window.

---

> **Want more?** For the full technical depth, see the
> [Technical Design Document](CheckupAddin%20-%20Technical%20Design%20Document.md).
> Curious how earlier versions looked? See [Earlier Designs](images/archive/README.md).
