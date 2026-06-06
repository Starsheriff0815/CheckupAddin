# Getting Started with Checkup — A Quick Visual Tour

Scroll through and watch the clips to see what Checkup does and where everything
lives. No setup knowledge needed.

> Looking for how to **install** it? See the [README](../README.md).
> Looking for the **technical** details? See the
> [Technical Design Document](CheckupAddin%20-%20Technical%20Design%20Document.md).

---

## The Main Checkup Window

![The main Checkup window](images/main-window.gif)

*The Main Addin Window Presents Users with a Maximum of 30 Rows which can be Sorted via Drag & Drop Handles.
Users can Add / Remove Rows from the Dropdown Menu on the Right. And Select, for the Row on which the Dropdown Menu
is Opened, any Value Present in the Object which is Selected in Inventor's Model Space / Model Browser.
The Addin either Lists a Single or Multiple Selected Objects. Within an Assembly (IAM) or a Part (IPT) it Lists the
Document itself if nothing is Selected.
At the Bottom of the Addin Window are three Preset Buttons Editable by Users through a Single Mouse Right Click (Context Menu).
Also on the Bottom Left is the "Purge Styles" Button which Cleans IAM / IPT / IDW.
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
Link, PairTransform, Prefix/Suffix, Search, MultiPick, Sort and Sync. A Single Click adds the Chosen Card to the Active Group.
On the Far Right is a Collapsible "Basic Logics" Panel holding Spreadsheet-Style Formula Functions (IF, LOOKUP, CONCATENATE,
ROUND and more). Clicking one adds a Basic Logic Card with the Function Skeleton Pre-Filled.
Cards and Basic Logics inside a Group Run from Top to Bottom, so Users can Reorder them to Control which Value Feeds the Next.
At the very Bottom is the Toolbar: on the Left the "+ Add Group" Button; on the Right the "I" (Info), ▲ (Up), ▼ (Down),
⧉ (Duplicate) and × (Delete) Buttons which Act on the Active Group or Card.*

---

> **Want more?** Every window has its own **"i" (Info)** button with a short
> quick-guide built right in. For the full technical depth, see the
> [Technical Design Document](CheckupAddin%20-%20Technical%20Design%20Document.md).
