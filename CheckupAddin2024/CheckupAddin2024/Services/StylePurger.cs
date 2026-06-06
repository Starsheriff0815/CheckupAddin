using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Updates styles from the global library and purges unused styles.
    /// Handles IDW, IPT (regular and sheet metal), and IAM documents.
    /// </summary>
    public class StylePurger
    {
        private readonly Inventor.Application         _app;
        private readonly UserSettings.StylePurgeSection _config;

        public StylePurger(Inventor.Application app, UserSettings.StylePurgeSection config)
        {
            _app    = app;
            _config = config;
        }

        public string UpdateAndPurge(Document doc)
        {
            if (doc == null) return "Kein aktives Dokument.";

            return doc.DocumentType switch
            {
                DocumentTypeEnum.kDrawingDocumentObject  => PurgeDrawing((DrawingDocument)doc),
                DocumentTypeEnum.kPartDocumentObject     => PurgePart((PartDocument)doc),
                DocumentTypeEnum.kAssemblyDocumentObject => PurgeAssembly((AssemblyDocument)doc),
                _                                        => "Dokumententyp wird nicht unterstützt."
            };
        }

        // ══════════════════════════════════════════════
        //  IDW
        // ══════════════════════════════════════════════

        private string PurgeDrawing(DrawingDocument doc)
        {
            var warnings = new List<string>();

            // 1. First-pass: update all non-library local styles from global
            foreach (var st in SnapshotStyles(doc))
            {
                if (st.StyleLocation == StyleLocationEnum.kLibraryStyleLocation) continue;
                if (!st.UpToDate)
                    try { st.UpdateFromGlobal(); } catch { }
            }

            // 2. Copy borders / title blocks / sketch symbols from template file
            if (!string.IsNullOrWhiteSpace(_config.TemplateFilePath))
                try { CopyFromTemplate(doc, warnings); }
                catch (Exception ex) { warnings.Add($"Vorlage-Import abgebrochen: {ex.Message}"); }

            // 3. Delete obsolete sketch symbol instances and their definitions
            foreach (var name in _config.SketchedSymbolsToDelete)
            {
                SketchedSymbolDefinition def = null;
                try { def = ((dynamic)doc.SketchedSymbolDefinitions).Item(name); }
                catch { continue; }

                foreach (Sheet sheet in doc.Sheets)
                {
                    var toDelete = new List<SketchedSymbol>();
                    foreach (SketchedSymbol sym in sheet.SketchedSymbols)
                        if (sym.Definition.Name == name) toDelete.Add(sym);
                    foreach (var sym in toDelete)
                        try { sym.Delete(); } catch { }
                }
                try { def.Delete(); } catch { }
            }

            // 4. Fix dimension text alignment
            try
            {
                doc.DrawingSettings.DimensionTextAlignment =
                    DimensionTextAlignmentEnum.kMaintainCenteredTextAlignment;
            }
            catch { }

            // 5. Loop: update out-of-date + purge unused styles until nothing changes
            bool changed;
            do
            {
                changed = false;
                foreach (var st in SnapshotStyles(doc))
                {
                    if (st.StyleLocation == StyleLocationEnum.kLibraryStyleLocation) continue;
                    if (!st.InUse)
                    {
                        try { st.Delete(); changed = true; } catch { }
                    }
                    else if (!st.UpToDate)
                    {
                        try { st.UpdateFromGlobal(); changed = true; } catch { }
                    }
                }
            } while (changed);

            if (doc.RequiresUpdate)
                try { doc.Update2(true); } catch { }

            var sb = new StringBuilder($"IDW Stile bereinigt – {DateTime.Now:HH:mm:ss}");
            foreach (var w in warnings) sb.Append($"  ⚠ {w}");
            return sb.ToString();
        }

        private void CopyFromTemplate(DrawingDocument target, List<string> warnings)
        {
            if (!System.IO.File.Exists(_config.TemplateFilePath))
            {
                warnings.Add($"Vorlagedatei nicht gefunden: {_config.TemplateFilePath}");
                return;
            }

            // dynamic bypasses the _DrawingDocument vs DrawingDocument COM interface mismatch.
            // Use explicit .Item(name) — the default [] indexer does not reliably accept strings
            // on all Inventor COM collection types.
            dynamic source    = null;
            dynamic dynTarget = target;
            try
            {
                source = _app.Documents.Open(_config.TemplateFilePath, false);

                foreach (var name in _config.BorderDefinitions)
                {
                    dynamic item = null;
                    try   { item = source.BorderDefinitions.Item(name); }
                    catch { warnings.Add($"Rahmen nicht in Vorlage: {name}"); continue; }
                    try   { item.CopyTo(dynTarget, true); }
                    catch (Exception ex) { warnings.Add($"Rahmen kopieren fehlgeschlagen: {name} ({ex.Message})"); }
                }

                foreach (var name in _config.TitleBlockDefinitions)
                {
                    dynamic item = null;
                    try   { item = source.TitleBlockDefinitions.Item(name); }
                    catch { warnings.Add($"Schriftfeld nicht in Vorlage: {name}"); continue; }
                    try   { item.CopyTo(dynTarget, true); }
                    catch (Exception ex) { warnings.Add($"Schriftfeld kopieren fehlgeschlagen: {name} ({ex.Message})"); }
                }

                foreach (var name in _config.SketchedSymbolsToCopy)
                {
                    dynamic item = null;
                    try   { item = source.SketchedSymbolDefinitions.Item(name); }
                    catch { warnings.Add($"Skizzensymbol nicht in Vorlage: {name}"); continue; }
                    try   { item.CopyTo(dynTarget, true); }
                    catch (Exception ex) { warnings.Add($"Skizzensymbol kopieren fehlgeschlagen: {name} ({ex.Message})"); }
                }
            }
            finally
            {
                // Close without saving — we only read from the template.
                try { source?.Close(false); } catch { }
            }
        }

        // Snapshot avoids COM enumerator invalidation during deletion
        private static List<Style> SnapshotStyles(DrawingDocument doc)
        {
            var list = new List<Style>();
            foreach (Style st in doc.StylesManager.Styles) list.Add(st);
            return list;
        }

        // ══════════════════════════════════════════════
        //  IPT
        // ══════════════════════════════════════════════

        private static string PurgePart(PartDocument doc)
        {
            bool isSM = doc.ComponentDefinition is SheetMetalComponentDefinition;
            dynamic dyn = doc;

            // Inventor blocks UpdateFromGlobal() on active styles. Switch, update, restore.
            UpdateActiveLightingStyle(doc);
            UpdateActiveRenderStyle(doc);
            if (isSM)
                UpdateActiveSheetMetalStyle((SheetMetalComponentDefinition)doc.ComponentDefinition);

            const int MaxPasses = 8;
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                bool anyChange = false;

                anyChange |= UpdateCollection(doc.LightingStyles);
                anyChange |= UpdateCollection(doc.TextStyles);
                anyChange |= UpdateCollection(doc.RenderStyles);

                anyChange |= PurgeCollection(doc.LightingStyles);
                anyChange |= PurgeCollection(doc.TextStyles);
                try { anyChange |= PurgeAssets(dyn.MaterialAssets); }   catch { }
                try { anyChange |= PurgeAssets(dyn.AppearanceAssets); } catch { }

                if (isSM)
                {
                    var sm = (SheetMetalComponentDefinition)doc.ComponentDefinition;
                    anyChange |= UpdateCollection(sm.SheetMetalStyles);
                    anyChange |= UpdateCollection(sm.UnfoldMethods);
                    anyChange |= PurgeCollection(sm.SheetMetalStyles);
                    anyChange |= PurgeCollection(sm.UnfoldMethods);
                }

                if (!anyChange) break;
            }

            return $"IPT Stile bereinigt{(isSM ? " (Blech)" : "")} – {DateTime.Now:HH:mm:ss}";
        }

        // Inventor holds a lock on the active sheet metal style and silently ignores
        // UpdateFromGlobal() calls on it. Switch to another local style first, update
        // all styles (the formerly-active one is now unlocked), then restore the original.
        private static void UpdateActiveSheetMetalStyle(SheetMetalComponentDefinition sm)
        {
            dynamic dynSm = sm;

            string originalName = null;
            try { originalName = dynSm.ActiveSheetMetalStyle?.Name as string; } catch { return; }
            if (originalName == null) return;

            // Find a non-active, non-library local style to use as a temporary stand-in.
            dynamic tempStyle = null;
            try
            {
                foreach (dynamic style in sm.SheetMetalStyles)
                {
                    try
                    {
                        bool isLib = false;
                        try { isLib = (StyleLocationEnum)style.StyleLocation
                                        == StyleLocationEnum.kLibraryStyleLocation; } catch { }
                        if (!isLib && (string)style.Name != originalName)
                        {
                            tempStyle = style;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (tempStyle == null) return; // Only one local style — cannot switch, skip.

            try
            {
                tempStyle.Activate();

                // Update both SheetMetalStyles and UnfoldMethods while the active rule is switched —
                // UnfoldMethods are referenced by SM styles and carry the same active lock.
                var smSnapshot = new List<dynamic>();
                foreach (dynamic style in sm.SheetMetalStyles) smSnapshot.Add(style);
                foreach (dynamic style in smSnapshot)
                    try { if (!(bool)style.UpToDate) style.UpdateFromGlobal(); } catch { }
                var ufSnapshot = new List<dynamic>();
                foreach (dynamic method in sm.UnfoldMethods) ufSnapshot.Add(method);
                foreach (dynamic method in ufSnapshot)
                    try { if (!(bool)method.UpToDate) method.UpdateFromGlobal(); } catch { }
            }
            catch { }
            finally
            {
                // Always restore the original active style, even if an update failed.
                try
                {
                    foreach (dynamic style in sm.SheetMetalStyles)
                    {
                        try
                        {
                            if ((string)style.Name == originalName)
                            {
                                style.Activate();
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // Inventor locks the active lighting style in the viewport — same mechanism as the SM style.
        // Identify it by InUse=true, switch to another local style, update all, then restore.
        private static void UpdateActiveLightingStyle(dynamic doc)
        {
            string originalName = null;
            try
            {
                foreach (dynamic style in doc.LightingStyles)
                {
                    try
                    {
                        bool isLib = false;
                        try { isLib = (StyleLocationEnum)style.StyleLocation
                                        == StyleLocationEnum.kLibraryStyleLocation; } catch { }
                        if (!isLib && (bool)style.InUse)
                        {
                            originalName = style.Name as string;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (originalName == null) return;

            dynamic tempStyle = null;
            try
            {
                foreach (dynamic style in doc.LightingStyles)
                {
                    try
                    {
                        bool isLib = false;
                        try { isLib = (StyleLocationEnum)style.StyleLocation
                                        == StyleLocationEnum.kLibraryStyleLocation; } catch { }
                        if (!isLib && (string)style.Name != originalName)
                        {
                            tempStyle = style;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (tempStyle == null) return;

            try
            {
                tempStyle.Activate();
                var snapshot = new List<dynamic>();
                foreach (dynamic style in doc.LightingStyles) snapshot.Add(style);
                foreach (dynamic style in snapshot)
                    try { if (!(bool)style.UpToDate) style.UpdateFromGlobal(); } catch { }
            }
            catch { }
            finally
            {
                try
                {
                    foreach (dynamic style in doc.LightingStyles)
                    {
                        try
                        {
                            if ((string)style.Name == originalName)
                            {
                                style.Activate();
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // Inventor locks the active render style — same mechanism as LightingStyle and SheetMetalStyle.
        // Identify it by InUse=true, switch to another local style, update all, then restore.
        private static void UpdateActiveRenderStyle(dynamic doc)
        {
            string originalName = null;
            try
            {
                foreach (dynamic style in doc.RenderStyles)
                {
                    try
                    {
                        bool isLib = false;
                        try { isLib = (StyleLocationEnum)style.StyleLocation
                                        == StyleLocationEnum.kLibraryStyleLocation; } catch { }
                        if (!isLib && (bool)style.InUse)
                        {
                            originalName = style.Name as string;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (originalName == null) return;

            dynamic tempStyle = null;
            try
            {
                foreach (dynamic style in doc.RenderStyles)
                {
                    try
                    {
                        bool isLib = false;
                        try { isLib = (StyleLocationEnum)style.StyleLocation
                                        == StyleLocationEnum.kLibraryStyleLocation; } catch { }
                        if (!isLib && (string)style.Name != originalName)
                        {
                            tempStyle = style;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (tempStyle == null) return;

            try
            {
                tempStyle.Activate();
                var snapshot = new List<dynamic>();
                foreach (dynamic style in doc.RenderStyles) snapshot.Add(style);
                foreach (dynamic style in snapshot)
                    try { if (!(bool)style.UpToDate) style.UpdateFromGlobal(); } catch { }
            }
            catch { }
            finally
            {
                try
                {
                    foreach (dynamic style in doc.RenderStyles)
                    {
                        try
                        {
                            if ((string)style.Name == originalName)
                            {
                                style.Activate();
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // ══════════════════════════════════════════════
        //  IAM
        // ══════════════════════════════════════════════

        private static string PurgeAssembly(AssemblyDocument doc)
        {
            dynamic dyn = doc;

            // Inventor blocks UpdateFromGlobal() on active styles. Switch, update, restore.
            UpdateActiveLightingStyle(doc);
            UpdateActiveRenderStyle(doc);

            const int MaxPasses = 8;
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                bool anyChange = false;

                anyChange |= UpdateCollection(doc.LightingStyles);
                anyChange |= UpdateCollection(doc.TextStyles);
                anyChange |= UpdateCollection(doc.RenderStyles);
                anyChange |= UpdateCollection(doc.Materials);

                anyChange |= PurgeCollection(doc.LightingStyles);
                anyChange |= PurgeCollection(doc.TextStyles);
                try { anyChange |= PurgeAssets(dyn.MaterialAssets); }   catch { }
                try { anyChange |= PurgeAssets(dyn.AppearanceAssets); } catch { }

                if (!anyChange) break;
            }

            return $"IAM Stile bereinigt – {DateTime.Now:HH:mm:ss}";
        }

        // ══════════════════════════════════════════════
        //  Generic helpers (dynamic — works for any COM style collection)
        // ══════════════════════════════════════════════

        private static bool UpdateCollection(dynamic collection)
        {
            bool updated = false;
            try
            {
                // Snapshot first: UpdateFromGlobal() can invalidate the COM enumerator mid-loop.
                var snapshot = new List<dynamic>();
                foreach (dynamic s in collection) snapshot.Add(s);
                foreach (dynamic s in snapshot)
                    try { if (!s.UpToDate) { s.UpdateFromGlobal(); updated = true; } } catch { }
            }
            catch { }
            return updated;
        }

        // Index-based iteration so deleting an item does not invalidate the loop.
        // Returns true if at least one style was deleted.
        private static bool PurgeCollection(dynamic collection)
        {
            bool anyDeleted = false;
            try
            {
                bool deleted;
                do
                {
                    deleted = false;
                    int i = 1;
                    while (i <= collection.Count)
                    {
                        try
                        {
                            dynamic s    = collection[i];
                            bool inUse   = s.InUse;
                            bool isLib   = false;
                            try { isLib  = (StyleLocationEnum)s.StyleLocation
                                            == StyleLocationEnum.kLibraryStyleLocation; }
                            catch { }

                            if (inUse || isLib) { i++; continue; }
                            try { s.Delete(); deleted = true; anyDeleted = true; }
                            catch { i++; }
                        }
                        catch { i++; }
                    }
                } while (deleted);
            }
            catch { }
            return anyDeleted;
        }

        // Returns true if at least one asset was deleted.
        private static bool PurgeAssets(dynamic collection)
        {
            bool anyDeleted = false;
            try
            {
                int i = 1;
                while (i <= collection.Count)
                {
                    try
                    {
                        dynamic asset = collection[i];
                        if (asset.IsUsed) { i++; continue; }
                        try { asset.Delete(); anyDeleted = true; }
                        catch { i++; }
                    }
                    catch { i++; }
                }
            }
            catch { }
            return anyDeleted;
        }
    }
}
