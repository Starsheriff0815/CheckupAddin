# CheckupAddin

A WPF MVVM add-in for Autodesk Inventor that provides a fast, user-configurable property panel for parts and assemblies.

- Read and write iProperties and parameters directly — no need to open Inventor's own dialogs
- Preset layouts for different part types (Bauteil, Baugruppe, Gehrungslücke)
- Logic Constructor: configure derived/computed fields without coding, using catalog-backed cards and formula expressions
- Style Purger: one-click cleanup of unused styles in IDW, IPT, and IAM documents
- German and English UI (language detected automatically from Inventor)

Two variants are included:

| Variant | Inventor version | Framework |
|---|---|---|
| **CheckupAddin2026** | Inventor 2026 (API v29.x) | .NET 8.0, x64 |
| **CheckupAddin2024** | Inventor 2024 (API v28.x) | .NET 4.8, x64 |

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

### 5. COM registration (2024 only)

After placing the files, run this command as Administrator once:

```
RegAsm.exe "C:\Addins\CheckupAddin2024\CheckupAddIn2024.dll" /codebase
```

(Replace the path with wherever you placed the DLL.)

### 6. Start Inventor

Inventor reads the manifest on startup and loads the add-in automatically. A **Checkup** button appears on the Sheet Metal, 3D Model, Assemble, and Drawing ribbon tabs.

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

## Author

Norman Lindner
