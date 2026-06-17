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

A variant is included for each supported Inventor release. Each variant is built against **its own** version's Inventor interop assembly:

| Variant          | Inventor version          | Framework     |
|------------------|---------------------------|---------------|
| **CheckupAddin2024** | Inventor 2024 (API v28.x) | .NET 4.8, x64 |
| **CheckupAddin2025** | Inventor 2025 (API v29.x) | .NET 4.8, x64 |
| **CheckupAddin2026** | Inventor 2026 (API v30.x) | .NET 8.0, x64 |
| **CheckupAddin2027** | Inventor 2027 (API v31.x) | .NET 8.0, x64 |

> 🚀 **New to Checkup?** Start with the [Getting Started visual tour](docs/Getting-Started.md) — a quick, picture-led look at the windows and what each part does, in plain language.

---

## Installation

### 1. Download

Go to the [Releases](../../releases) page and download the latest release zip for your Inventor version:

- `CheckupAddin2024_vX.X.X.zip` for Inventor 2024
- `CheckupAddin2025_vX.X.X.zip` for Inventor 2025
- `CheckupAddin2026_vX.X.X.zip` for Inventor 2026
- `CheckupAddin2027_vX.X.X.zip` for Inventor 2027
- `CheckupDesignHarness_vX.X.X.zip` — optional standalone tool to customize the add-in's visual appearance without running Inventor (see [DesignHarness](#designharness--visual-customization-tool) below)

### 2. Unpack

Extract the zip to a permanent folder on your machine. Example:

```
C:\Addins\CheckupAddin2026\
```

> **Multi-user / network install:** You can place the extracted files on a UNC share (e.g. `\\server\Addins\CheckupAddin2026\`). The DLL and support files load from that shared location; each user only needs the manifest file (step 6) on their own machine.

### 3. Copy the Inventor Interop assembly

The release zip does **not** include `Autodesk.Inventor.Interop.dll` — it is proprietary Autodesk software and is never redistributed (see [License](#license) below). Each variant is built against **its own** Inventor version's copy of this file, so copy the interop from the **matching** Inventor installation into the folder you extracted in step 2. For example, for `CheckupAddin2026`:

```
copy "C:\Program Files\Autodesk\Inventor 2026\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll" "C:\Addins\CheckupAddin2026\"
```

Use the path for your version — `...\Inventor 2024\...`, `...\Inventor 2025\...`, `...\Inventor 2026\...`, or `...\Inventor 2027\...` — copied into the corresponding extracted folder. Because you install the variant that matches your Inventor, the correct interop is always available locally on that machine.

Without this file the add-in fails to load — `CheckupAddIn.dll` has a hard type-load dependency on the interop assembly, so Inventor reports it as a failed/blocked add-in with no further detail.

### 4. Create the manifest file

Inside the extracted folder you will find `Autodesk.CheckupAddIn2026.addin.template` (named for your variant, e.g. `...2024...`, `...2025...`, `...2027...`).

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

### 5. Place the manifest

Copy the `.addin` file you just created to Inventor's add-ins folder:

- **2024:** `%APPDATA%\Autodesk\ApplicationPlugins\` (or `%PROGRAMDATA%\Autodesk\Inventor 2024\Addins\`)
- **2025:** `%PROGRAMDATA%\Autodesk\Inventor 2025\Addins\`
- **2026:** `%PROGRAMDATA%\Autodesk\Inventor 2026\Addins\`
- **2027:** `%PROGRAMDATA%\Autodesk\Inventor 2027\Addins\`

> **Multi-user note:** Each user copies only the `.addin` manifest to their own machine. The manifest points to the shared network path from step 4. Only the manifest needs to be on each user's local machine — everything else stays on the share.

### 6. Start Inventor and unblock the add-in

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
- The Autodesk Inventor version matching the variant you want to build — its COM interop DLL is referenced per-version from `lib\<year>\`

**Provide the interop assemblies.** `Autodesk.Inventor.Interop.dll` is proprietary and is **not** committed to this repository (see [License](#license)). Each variant references its own copy from `lib\<year>\`. Populate those folders from your local Inventor installs before building:

```
pwsh ./fetch_interop.ps1
```

This copies the interop from each installed `...\Inventor <year>\Bin\Public Assemblies\` into `lib\<year>\` (versions you don't have installed are skipped).

Open the matching solution (`CheckupAddin2024.sln` … `CheckupAddin2027.sln`) and build with **x64 / Debug or Release**, or from the command line (full MSBuild path required — `msbuild` is not on PATH by default):

```
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" CheckupAddin2026\CheckupAddin2026\CheckupAddin2026.csproj /p:Configuration=Release /p:Platform=x64
```

Build output is placed in `CheckupAddin<year>\CheckupAddin<year>\bin\`.

### Building the release bundles

`build_release.ps1` builds, packages, and (optionally) publishes the release zips for all four add-in variants and the DesignHarness. It uses 7-Zip if installed, otherwise PowerShell's `Compress-Archive`:

```
pwsh ./build_release.ps1                           # build + zip all variants + DesignHarness into dist\
pwsh ./build_release.ps1 -Tag v0.13.0 -Publish    # also create the GitHub release via gh
pwsh ./build_release.ps1 -SkipHarness             # add-in variants only, skip DesignHarness
```

Releases are built **locally** — there is no CI build, because a hosted runner has no Inventor install to reference the interop from.

---

## DesignHarness — Visual Customization Tool

The DesignHarness is a standalone WPF application for previewing and adjusting the add-in's visual appearance — colors, fonts, labels, and window sizes — **without opening Inventor**.

**Who it is for:** Anyone who cloned the repo and wants to customize how the add-in looks. Edits made in the harness are exported back to the source XAML and JSON files so they can be committed and rebuilt.

**Requirements:**
- .NET 8 Desktop Runtime (x64)
- `Autodesk.Inventor.Interop.dll` from your **Inventor 2026** install — copy it next to the exe before running (same requirement as the Inventor 2026 add-in bundle; not shipped in the zip)

**Running from the release zip:**
1. Download `CheckupDesignHarness_vX.X.X.zip` from the [Releases](../../releases) page and extract it to any folder.
2. Copy `Autodesk.Inventor.Interop.dll` from `C:\Program Files\Autodesk\Inventor 2026\Bin\Public Assemblies\` into that folder.
3. Run `CheckupAddIn.DesignHarness.exe`.

**Building from source:**

The harness lives in `DesignHarness\` and is built automatically by `build_release.ps1`. To build it on its own (after running `fetch_interop.ps1` to populate `lib\2026\`):

```
msbuild DesignHarness\DesignHarness.csproj /p:Configuration=Release /p:Platform=x64
```

Output: `DesignHarness\bin\CheckupAddIn.DesignHarness.exe`

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