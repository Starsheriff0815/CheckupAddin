using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Reads iProperties (standard + user defined) and parameters from Inventor documents.
    /// </summary>
    public class PropertyReader
    {
        // Inventor localises PropertySet names — the same set has different internal names in
        // English vs. German installations. All known variants are tried in order.
        private static readonly string[] UserDefinedSetCandidates =
        {
            "Inventor User Defined Properties",
            "Inventor benutzerdefinierte Eigenschaften",
            "Benutzerdefinierte Eigenschaften",
            "User Defined Properties"
        };

        /// <summary>
        /// Reads a user-defined (custom) iProperty by name.
        /// Tries multiple language candidates for the property set name.
        /// </summary>
        public string ReadUserDefinedProperty(Document doc, string propName)
        {
            if (doc == null) return "n/a";

            try
            {
                PropertySet ps = null;
                foreach (var setName in UserDefinedSetCandidates)
                {
                    try
                    {
                        ps = doc.PropertySets[setName];
                        if (ps != null) break;
                    }
                    catch { }
                }
                if (ps == null) return "n/a";

                var p = ps[propName];
                if (p?.Value == null) return "";
                return p.Value.ToString();
            }
            catch
            {
                return "n/a";
            }
        }

        /// <summary>
        /// Reads a standard iProperty using multiple candidate names for both
        /// the property set and the property name (multi-language safe).
        /// </summary>
        public string ReadStandardProperty(Document doc, string[] setCandidates, string[] propCandidates)
        {
            if (doc == null) return "n/a";

            foreach (var setName in setCandidates)
            {
                PropertySet ps = null;
                try { ps = doc.PropertySets[setName]; } catch { }
                if (ps == null) continue;

                foreach (var propName in propCandidates)
                {
                    try
                    {
                        var p = ps[propName];
                        if (p != null)
                            return p.Value?.ToString() ?? "";
                    }
                    catch { }
                }
            }
            return "n/a";
        }

        /// <summary>
        /// Searches all standard (non-user-defined) property sets for a property with the given name.
        /// Used to resolve short-form formula references like {IPROP|Description} where no set name
        /// is specified. Returns "" when not found (not "n/a") so formulas can distinguish missing
        /// from "no document".
        /// </summary>
        public string ReadStandardPropertyByName(Document doc, string propName)
        {
            if (doc == null || string.IsNullOrEmpty(propName)) return "";
            try
            {
                foreach (PropertySet ps in doc.PropertySets)
                {
                    string setName = "";
                    try { setName = ps.DisplayName ?? ps.Name ?? ""; } catch { continue; }
                    // Skip user-defined sets (accessed via UDEF: not IPROP|).
                    string lower = setName.ToLowerInvariant();
                    if (lower.Contains("user defined") || lower.Contains("benutzerdefiniert") || lower.Contains("custom"))
                        continue;

                    try
                    {
                        var p = ps[propName];
                        if (p != null) return p.Value?.ToString() ?? "";
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Reads a document-level value (Material, Appearance, Units, Precision, etc.)
        /// </summary>
        public string ReadDocumentValue(Document doc, string tag)
        {
            if (doc == null) return "n/a";

            try
            {
                if (string.Equals(tag, "Material", StringComparison.Ordinal) && doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    var part = (PartDocument)doc;
                    var compDef = part.ComponentDefinition;
                    var mat = compDef.Material;
                    return mat?.Name ?? "n/a";
                }

                if (string.Equals(tag, "Appearance", StringComparison.Ordinal) && doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    var part = (PartDocument)doc;
                    try
                    {
                        var activeAppearance = part.ActiveAppearance;
                        string appearance = activeAppearance?.DisplayName ?? "n/a";
                        // Inventor returns "LibraryName: AppearanceName" — strip the library prefix.
                        if (appearance.Contains(':'))
                            appearance = appearance[(appearance.IndexOf(':') + 1)..].Trim();
                        return appearance;
                    }
                    catch { return "n/a"; }
                }

                var uom = doc.UnitsOfMeasure;

                return tag switch
                {
                    "UnitsLength" => SheetMetalReader.UnitAbbreviation(uom.LengthUnits),
                    "UnitsAngle" => uom.AngleUnits.ToString(),
                    "UnitsTime" => uom.TimeUnits.ToString(),
                    "UnitsMass" => uom.MassUnits.ToString(),
                    "LinearPrecision" => uom.LengthDisplayPrecision.ToString(),
                    "AngularPrecision" => uom.AngleDisplayPrecision.ToString(),
                    "ModelingDimDisplay" => "n/a",
                    "DefaultBOMStructure" => "n/a",
                    _ => "n/a"
                };
            }
            catch
            {
                return "n/a";
            }
        }

        /// <summary>
        /// Reads a parameter expression (user or model parameter) by name.
        /// Supports both PartDocument and AssemblyDocument.
        /// </summary>
        public string ReadParameterExpression(Document doc, string paramName)
        {
            if (doc == null) return "n/a";

            Parameters parameters = null;
            try
            {
                if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                    parameters = ((PartDocument)doc).ComponentDefinition.Parameters;
                else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                    parameters = ((AssemblyDocument)doc).ComponentDefinition.Parameters;
            }
            catch { }

            if (parameters == null) return "n/a";

            try
            {
                var up = parameters.UserParameters[paramName];
                return up.Expression;
            }
            catch { }

            try
            {
                var mp = parameters.ModelParameters[paramName];
                return mp.Expression;
            }
            catch { }

            return "n/a";
        }
    }
}