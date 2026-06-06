using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Resolves the "best available" document from the current Inventor selection state.
    /// Used before every read and write operation to find the target document without
    /// requiring the user to switch the active document manually.
    /// </summary>
    public class DocumentResolver
    {
        private readonly Inventor.Application _app;

        public DocumentResolver(Inventor.Application app)
        {
            _app = app;
        }

        /// <summary>
        /// Returns the best available document:
        /// - Active IPT → that IPT
        /// - Active assembly with selected component → referenced document
        /// - Active assembly without selection → the assembly itself
        /// - Other — falls back to active document
        /// </summary>
        public Document GetActiveOrSelectedDocument(out string error)
        {
            error = "";

            if (_app.ActiveDocument == null)
            {
                error = "No active document.";
                return null;
            }

            if (_app.ActiveDocument is PartDocument)
                return (Document)_app.ActiveDocument;

            if (_app.ActiveDocument is AssemblyDocument asm)
            {
                var selectSet = asm.SelectSet;
                if (selectSet.Count > 0)
                {
                    object sel = null;
                    try { sel = selectSet[1]; } catch { }

                    var doc = TryResolveDocument(sel);
                    if (doc != null) return doc;
                }

                return (Document)asm;
            }

            return (Document)_app.ActiveDocument;
        }

        /// <summary>
        /// Returns all distinct documents from the current SelectSet.
        /// isMulti is true when 2 or more distinct part documents are selected.
        /// isAssemblyFallback is true when the active document is an assembly but no component
        /// occurrence was found in the SelectSet — the assembly itself was returned as a fallback.
        /// Callers can use isAssemblyFallback to detect a "nothing selected" state in an IAM.
        /// Falls back to GetActiveOrSelectedDocument behaviour for 0 or 1 selections.
        /// </summary>
        public List<Document> GetAllSelectedDocuments(out bool isMulti, out bool isAssemblyFallback)
        {
            isMulti = false;
            isAssemblyFallback = false;
            var result = new List<Document>();

            if (_app.ActiveDocument == null) return result;

            if (_app.ActiveDocument is PartDocument)
            {
                result.Add((Document)_app.ActiveDocument);
                return result;
            }

            if (_app.ActiveDocument is AssemblyDocument asm)
            {
                var selectSet2 = asm.SelectSet;
                if (selectSet2.Count == 0)
                {
                    result.Add((Document)asm);
                    isAssemblyFallback = true;
                    return result;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int count = selectSet2.Count;
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        object sel = selectSet2[i];
                        var doc = TryResolveDocument(sel);

                        if (doc != null)
                        {
                            string path = "";
                            try { path = doc.FullFileName; } catch { path = doc.DisplayName; }
                            if (seen.Add(path)) result.Add(doc);
                        }
                    }
                    catch { }
                }

                if (result.Count == 0)
                {
                    result.Add((Document)asm);
                    isAssemblyFallback = true;
                    return result;
                }

                isMulti = result.Count > 1;
                return result;
            }

            result.Add((Document)_app.ActiveDocument);
            return result;
        }

        // Resolves a SelectSet item to its owner Document.
        // Handles direct ComponentOccurrence selection and indirect selection
        // (face, edge, feature) via late-bound COM property lookup.
        private static Document TryResolveDocument(object sel)
        {
            if (sel == null) return null;

            if (sel is ComponentOccurrence occ)
            {
                var def = occ.Definition;
                return (Document)def.Document;
            }

            try
            {
                var occ2 = (ComponentOccurrence)Microsoft.VisualBasic.Interaction.CallByName(
                    sel, "ContainingOccurrence", Microsoft.VisualBasic.CallType.Get);
                if (occ2 != null)
                {
                    var def2 = occ2.Definition;
                    return (Document)def2.Document;
                }
            }
            catch { }

            try
            {
                var occ3 = (ComponentOccurrence)Microsoft.VisualBasic.Interaction.CallByName(
                    sel, "Occurrence", Microsoft.VisualBasic.CallType.Get);
                if (occ3 != null)
                {
                    var def3 = occ3.Definition;
                    return (Document)def3.Document;
                }
            }
            catch { }

            return null;
        }
    }
}
