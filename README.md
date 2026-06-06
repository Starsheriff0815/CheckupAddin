# CheckupAddin

#### Introduction / Foreword / Prose:

This Project Started with me Wanting a Tool for Autodesk Inventor. But i had and Still have Zero Knowledge in Coding or Design. All i had and still do, is an Strong Opinion. :)  
At the Beginning, Trying LLMs to Guide me through Autodesks iLogic so i could “Materialize” what i had in Mind Ended Pretty fast in an Dead End. Learnings while “Playing” with LLMs like GitHub Copilot and Anthropics Claude ended in Building an Full Fledged Microsoft Visual Studio Project and Developing this Addin.  
From its first Stage Beeing a Independend Window to Simply View Parameters, iProperties or Document Values. It Iterated to also enabling Users Editing these Values and then to its Current State.  
Where Users Additionaly have a Tool which can Help on more Repetetive Workflows. This Later Part the so called “Logics-Constructor” is Intended to not just Rely on MS Excel to Feed Information and make use of its Functionality. Altough it would have been Easy to just Build a MS Excel Interface and be done.  
  
But my Intention is to not Rely on Proprietary Software. And giving User the Freedom of Choice in many Ways. Starting with Multilanguage Management Located in Seperate Files. Or Setting Presets for what Values you want to See / Edit. And the “Logics-Constructors” Capability to Create “Special Functions” which Provide things Like Dropdown Menus or more Complex things MS Exel can do.  
Quiet Simmilar to General Spreadsheet…  
  
And Providing the Full Code is the Last Step.  
  
This is 99% my personal opinion and how I imagine things should work. So don’t be surprised if what makes sense to me doesn’t make sense to you. However, I’d be happy if with this I could give something back to others as well. Criticism and discussion are very welcome!  
  
*» I am Sorry to everyone who Actualy can Code! (Blame Claude if you want) «*

---

#### A WPF MVVM add-in for Autodesk Inventor that provides a fast, user-configurable property panel for parts and assemblies.

- Read and write iProperties and parameters directly — no need to open Inventor's own dialogs
- Saveable preset layouts — load a named preset to show the relevant fields for that document type
- Logics-Constructor: configure derived/computed fields without coding, using catalog-backed cards and formula expressions
- Style Purger: one-click cleanup of unused styles in IDW, IPT, and IAM documents
- German and English UI **\[more can be Added\]** (language detected automatically from Inventor)

Two Tryed and Tested variants are included:

| Variant          | Inventor version          | Framework     |
|------------------|---------------------------|---------------|
| **CheckupAddin2026** | Inventor 2026 (API v29.x) | .NET 8.0, x64 |
| **CheckupAddin2024** | Inventor 2024 (API v28.x) | .NET 4.8, x64 |

> 🚀 **New to Checkup?** Start with the [Getting Started visual tour](docs/Getting-Started.md) — a quick, picture-led look at the windows and what each part does, in plain language.

---

## Installation

### 1. Download

Go to the [Releases](../../releases) page and download the latest release zip for your Inventor version:

- `CheckupAddin2026_vX.X.X.zip` for Inventor 2026
- `CheckupAddin2024_vX.X.X.zip` for Inventor 2024

### 2. Unpack

Extract the zip to a permanent folder on your machine. Example:

```
C:\Addins\CheckupAddin2026\
```

> **Multi-user / network install:** You can place the extracted files on a UNC share (e.g. `\\server\Addins\CheckupAddin2026\`). The DLL and support files load from that shared location; each user only needs the manifest file (step 4) on their own machine.

### 3. Create the manifest file

Inside the extracted folder you will find `Autodesk.CheckupAddIn2026.addin.template` (or `...2024...`).

1. Open the template file in any text editor (Notepad is fine).
2. Replace `[ASSEMBLY_PATH]` with the full path to the folder where you unpacked the files. Example:

   ```
   C:\Addins\CheckupAddin2026\
   ```

    The `<Assembly>` line should read:

   ```xml
   <Assembly>C:\Addins\CheckupAddin2026\CheckupAddIn.dll</Assembly>
   ```

    For a network install:

   ```xml
   <Assembly>\\server\Addins\CheckupAddin2026\CheckupAddIn.dll</Assembly>
   ```
3. Fill in `<Author>` and `<Company>` with your own values (or leave the placeholders).
4. Save the file as `Autodesk.CheckupAddIn2026.addin` (remove the `.template` suffix).

### 4. Place the manifest

Copy the `.addin` file you just created to Inventor's add-ins folder:

- **2026:** `%PROGRAMDATA%\Autodesk\Inventor 2026\Addins\`
- **2024:** `%APPDATA%\Autodesk\ApplicationPlugins\`

> **Multi-user note:** Each user copies only the `.addin` manifest to their own machine. The manifest points to the shared network path from step 3. Only the manifest needs to be on each user's local machine — everything else stays on the share.

### 5. Start Inventor and unblock the add-in

Inventor reads the manifest on startup and loads the add-in automatically.

Because the add-in is **not digitally signed**, Inventor blocks it the first
time it is detected. On startup a message names the blocked add-in and
explains how to enable it. To unblock it:

1. Open the **Add-In Manager** — in Inventor via **Tools ▸ Add-Ins**. (It can
   also be started on its own, and pops up automatically when a blocked
   unsigned add-in is detected.)
2. On the **Applications** tab, select **Checkup** (and **Checkup 2024** if you
   installed it) in the **Available Add-Ins** list. The **Signature** field
   will read *"No signature was present in the file"* and **Publisher** will be
   *Unknown* — this is expected for an unsigned add-in.
3. In the **Load Behavior** box (lower right), untick **Block** and tick
   **Load Automatically**.
4. Click **OK** and restart Inventor.

A **Checkup** button then appears on the Sheet Metal, 3D Model, Assemble, and
Drawing ribbon tabs.

> **For administrators (managed/policy-controlled environments):** Inventor's
> add-in security can be configured to load only add-ins permitted by policy
> (for example, signed add-ins or those from approved/trusted locations). In
> tightly managed multi-user deployments this can prevent a standard user from
> unblocking an unsigned add-in at all. If Checkup will not load even after the
> steps above, the add-in (or its install location) must be approved centrally
> by an administrator under your organization's Inventor add-in security policy.

---

## Building from Source

Requirements:

- Visual Studio 2022 (v17.14 or later)
- Autodesk Inventor 2026 installed (for 2026 build) — the COM interop DLL is referenced from the Inventor install folder
- Autodesk Inventor 2024 installed (for 2024 build)

Open `CheckupAddin2026.sln` or `CheckupAddin2024.sln` and build with **x64 / Debug or Release**.

From the command line (full MSBuild path required — `msbuild` is not on PATH by default):

```
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" CheckupAddin2026\CheckupAddin2026.csproj /p:Configuration=Release /p:Platform=x64
```

Build output is placed in `CheckupAddin2026\bin\`.

---

## Technical Documentation

For architecture details, feature inventory, design decisions, and the full change history see:

[docs/CheckupAddin - Technical Design Document.md](docs/CheckupAddin%20-%20Technical%20Design%20Document.md)

---

## License

This project is licensed under the **GNU General Public License v3.0** — see the [LICENSE](LICENSE) file for the full text.

Third-party dependency notices (Autodesk Inventor API, .NET runtime, System.Drawing.Common) are listed in [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES).

> **Note on the Autodesk Inventor API:** This add-in links against `Autodesk.Inventor.Interop.dll`, which is proprietary Autodesk software. That assembly is **not** included in this repository and is not redistributed. Building or running the add-in requires a licensed installation of Autodesk Inventor on the build machine.

---