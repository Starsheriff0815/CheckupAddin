#!/usr/bin/env bash
set -euo pipefail

# Usage: clone_variant.sh <SRC_YEAR> <DST_YEAR> <OLD_GUID> <NEW_GUID> <OLD_SLNPROJ> <NEW_SLNPROJ> <OLD_SLNSOL> <NEW_SLNSOL> <SUPPORTED_VER>
SRC="$1"; DST="$2"; OLD_GUID="$3"; NEW_GUID="$4"
OLD_SLNPROJ="$5"; NEW_SLNPROJ="$6"; OLD_SLNSOL="$7"; NEW_SLNSOL="$8"; SUPP="$9"

ROOT="C:/Daten/Autodesk/_Inventor/_iLogic Projekte/VS Projects"
SRCDIR="$ROOT/CheckupAddin$SRC"
DSTDIR="$ROOT/CheckupAddin$DST"

if [ -d "$DSTDIR" ]; then echo "ABORT: $DSTDIR already exists"; exit 1; fi

echo "Copying $SRCDIR -> $DSTDIR (tar, excluding build/IDE dirs) ..."
mkdir -p "$DSTDIR"
# tar-based copy never reads excluded dirs, so a VS-locked .vs folder can't block us
( cd "$SRCDIR" && tar cf - \
    --exclude=.vs --exclude=bin --exclude=obj --exclude=.claude \
    --exclude=Dotfuscated --exclude='*.csproj.user' . ) | ( cd "$DSTDIR" && tar xf - )

# --- Rename inner project folder ---
mv "$DSTDIR/CheckupAddin$SRC" "$DSTDIR/CheckupAddin$DST"
INNER="$DSTDIR/CheckupAddin$DST"

# --- Rename year-bearing files ---
mv "$DSTDIR/CheckupAddin$SRC.sln"                 "$DSTDIR/CheckupAddin$DST.sln"
mv "$INNER/CheckupAddin$SRC.csproj"               "$INNER/CheckupAddin$DST.csproj"
[ -f "$INNER/Autodesk.CheckupAddIn$SRC.addin" ]          && mv "$INNER/Autodesk.CheckupAddIn$SRC.addin"          "$INNER/Autodesk.CheckupAddIn$DST.addin"
[ -f "$INNER/Autodesk.CheckupAddIn$SRC.addin.template" ] && mv "$INNER/Autodesk.CheckupAddIn$SRC.addin.template" "$INNER/Autodesk.CheckupAddIn$DST.addin.template"
# Dotfuscator config (no-space token Checkup<year>)
if [ -f "$INNER/Dotfuscator/Dotfuscator_Checkup$SRC.xml" ]; then
  mv "$INNER/Dotfuscator/Dotfuscator_Checkup$SRC.xml" "$INNER/Dotfuscator/Dotfuscator_Checkup$DST.xml"
fi

# --- Targeted text replacements over text files only ---
mapfile -t FILES < <(find "$DSTDIR" -type f \( \
  -name '*.cs' -o -name '*.csproj' -o -name '*.sln' -o -name '*.addin' -o -name '*.template' \
  -o -name '*.json' -o -name '*.xaml' -o -name '*.xml' -o -name '*.iLogicVb' -o -name '*.txt' -o -name '*.md' \) )

for f in "${FILES[@]}"; do
  # COM GUID (with or without braces; source is uppercase)
  sed -i "s|$OLD_GUID|$NEW_GUID|g" "$f"
  # Project identity tokens (case-sensitive, two distinct casings)
  sed -i "s|CheckupAddin$SRC|CheckupAddin$DST|g" "$f"   # folder / csproj / sln / profile
  sed -i "s|CheckupAddIn$SRC|CheckupAddIn$DST|g" "$f"   # assembly / dll / .addin filename
  sed -i "s|Checkup$SRC|Checkup$DST|g" "$f"             # Dotfuscator no-space token
  # Data + display namespace
  sed -i "s|Checkup $SRC|Checkup $DST|g" "$f"
  sed -i "s|Add-In $SRC|Add-In $DST|g" "$f"
done

# Fix cross-contaminated "Checkup 2026" left in 2024-lineage settings _Info (only when SRC=2024)
if [ "$SRC" = "2024" ]; then
  for f in "${FILES[@]}"; do
    sed -i "s|Checkup 2026|Checkup $DST|g; s|Checkup Add-In 2026|Checkup Add-In $DST|g" "$f"
  done
fi

# --- File-specific replacements (avoid touching API-version comments in .cs/.xaml) ---
# Inventor <year> only in build/manifest files (exe path, descriptions)
for f in "$INNER"/Properties/launchSettings.json "$INNER/CheckupAddin$DST.csproj" \
         "$INNER/Autodesk.CheckupAddIn$DST.addin" "$INNER/Autodesk.CheckupAddIn$DST.addin.template"; do
  [ -f "$f" ] && sed -i "s|Inventor $SRC|Inventor $DST|g" "$f"
done

# SupportedSoftwareVersionGreaterThan in manifest + template
for f in "$INNER/Autodesk.CheckupAddIn$DST.addin" "$INNER/Autodesk.CheckupAddIn$DST.addin.template"; do
  [ -f "$f" ] && sed -i "s|<SupportedSoftwareVersionGreaterThan>[0-9]*\.\.</SupportedSoftwareVersionGreaterThan>|<SupportedSoftwareVersionGreaterThan>$SUPP..</SupportedSoftwareVersionGreaterThan>|g" "$f"
done

# Solution GUIDs (project + solution)
sed -i "s|$OLD_SLNPROJ|$NEW_SLNPROJ|g; s|$OLD_SLNSOL|$NEW_SLNSOL|g" "$DSTDIR/CheckupAddin$DST.sln"

echo "DONE: $DSTDIR"
