using System;
using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Reads iProperties (standard + user defined) and parameters from Inventor documents.
    /// </summary>
    public class PropertyReader
    {
        /// <summary>
        /// Not-found / not-applicable <b>display</b> sentinel, shown in the value column.
        /// Returned only by the <b>value</b> readers. Expression readers return <c>""</c> instead
        /// (an expression result feeds content heuristics like <see cref="IsParameterFormula"/>,
        /// which must never see a display string — "n/a" contains '/' and would read as division).
        /// </summary>
        public const string NotAvailable = "n/a";

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
            if (doc == null) return NotAvailable;

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
                if (ps == null) return NotAvailable;

                var p = ps[propName];
                if (p?.Value == null) return "";
                return p.Value.ToString();
            }
            catch
            {
                return NotAvailable;
            }
        }

        /// <summary>
        /// Reads a standard iProperty using multiple candidate names for both
        /// the property set and the property name (multi-language safe).
        /// </summary>
        public string ReadStandardProperty(Document doc, string[] setCandidates, string[] propCandidates)
        {
            if (doc == null) return NotAvailable;

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
            return NotAvailable;
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

        // ── Formula (expression) detection ──────────────────────────────────────
        // Text iProperties can be driven by an Inventor expression (e.g. "=<Width> x <Height>").
        // Property.Value returns the *evaluated* text; Property.Expression returns the formula,
        // which starts with '=' when one is present. These readers surface the formula so the UI
        // can show the fx toggle. They return "" when the property holds a literal / has no
        // expression / is not found — never "n/a", so the absence of a formula is unambiguous.

        /// <summary>Returns the expression behind a user-defined iProperty, or "" when it is a literal.</summary>
        public string ReadUserDefinedExpression(Document doc, string propName)
        {
            if (doc == null || string.IsNullOrEmpty(propName)) return "";
            try
            {
                PropertySet ps = null;
                foreach (var setName in UserDefinedSetCandidates)
                {
                    try { ps = doc.PropertySets[setName]; } catch { }
                    if (ps != null) break;
                }
                if (ps == null) return "";
                var p = ps[propName];
                return p != null ? FormulaOrEmpty(ReadExpression(p)) : "";
            }
            catch { return ""; }
        }

        /// <summary>Returns the expression behind a standard iProperty (set + name candidates), or "".</summary>
        public string ReadStandardExpression(Document doc, string[] setCandidates, string[] propCandidates)
        {
            if (doc == null) return "";
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
                        if (p != null) return FormulaOrEmpty(ReadExpression(p));
                    }
                    catch { }
                }
            }
            return "";
        }

        /// <summary>Returns the expression behind a standard iProperty found by name across all
        /// non-user-defined sets (short-form IPROP| keys), or "".</summary>
        public string ReadStandardExpressionByName(Document doc, string propName)
        {
            if (doc == null || string.IsNullOrEmpty(propName)) return "";
            try
            {
                foreach (PropertySet ps in doc.PropertySets)
                {
                    string setName = "";
                    try { setName = ps.DisplayName ?? ps.Name ?? ""; } catch { continue; }
                    string lower = setName.ToLowerInvariant();
                    if (lower.Contains("user defined") || lower.Contains("benutzerdefiniert") || lower.Contains("custom"))
                        continue;
                    try
                    {
                        var p = ps[propName];
                        if (p != null) return FormulaOrEmpty(ReadExpression(p));
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>Late-bound read of Property.Expression; "" if unsupported, empty, or it throws.</summary>
        private static string ReadExpression(Property p)
        {
            try
            {
                object o = Microsoft.VisualBasic.Interaction.CallByName(
                    p, "Expression", Microsoft.VisualBasic.CallType.Get);
                return o != null ? o.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>Keeps the string only when it is an Inventor formula (starts with '='); else "".</summary>
        private static string FormulaOrEmpty(string expr) =>
            (!string.IsNullOrEmpty(expr) && expr.TrimStart().StartsWith("=", StringComparison.Ordinal)) ? expr : "";

        /// <summary>
        /// Reads a document-level value (Material, Appearance, Units, Precision, etc.)
        /// </summary>
        public string ReadDocumentValue(Document doc, string tag)
        {
            if (doc == null) return NotAvailable;

            try
            {
                if (string.Equals(tag, "Material", StringComparison.Ordinal) && doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    var part = (PartDocument)doc;
                    var compDef = part.ComponentDefinition;
                    var mat = compDef.Material;
                    return mat != null ? mat.Name : NotAvailable;
                }

                if (string.Equals(tag, "Appearance", StringComparison.Ordinal) && doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    var part = (PartDocument)doc;
                    try
                    {
                        var activeAppearance = part.ActiveAppearance;
                        string appearance = activeAppearance != null ? activeAppearance.DisplayName : NotAvailable;
                        // Inventor returns "LibraryName: AppearanceName" — strip the library prefix.
                        if (appearance.IndexOf(':') >= 0)
                            appearance = appearance.Substring(appearance.IndexOf(':') + 1).Trim();
                        return appearance;
                    }
                    catch { return NotAvailable; }
                }

                var uom = doc.UnitsOfMeasure;

                return tag switch
                {
                    "UnitsLength" => UnitAbbreviation(uom.LengthUnits),
                    "UnitsAngle" => uom.AngleUnits.ToString(),
                    "UnitsTime" => uom.TimeUnits.ToString(),
                    "UnitsMass" => uom.MassUnits.ToString(),
                    "LinearPrecision" => uom.LengthDisplayPrecision.ToString(),
                    "AngularPrecision" => uom.AngleDisplayPrecision.ToString(),
                    "ModelingDimDisplay" => NotAvailable,
                    "DefaultBOMStructure" => NotAvailable,
                    _ => NotAvailable
                };
            }
            catch
            {
                return NotAvailable;
            }
        }

        /// <summary>Maps an Inventor length-unit enum to its display abbreviation (mm/cm/m/in/ft).</summary>
        private static string UnitAbbreviation(UnitsTypeEnum unit)
        {
            return unit switch
            {
                UnitsTypeEnum.kMillimeterLengthUnits => "mm",
                UnitsTypeEnum.kCentimeterLengthUnits => "cm",
                UnitsTypeEnum.kMeterLengthUnits => "m",
                UnitsTypeEnum.kInchLengthUnits => "in",
                UnitsTypeEnum.kFootLengthUnits => "ft",
                _ => "?"
            };
        }

        /// <summary>
        /// Reads a parameter expression (user or model parameter) by name.
        /// Supports both PartDocument and AssemblyDocument.
        /// Returns <c>""</c> when the parameter is not found — NEVER the <see cref="NotAvailable"/>
        /// display sentinel: this result feeds <see cref="IsParameterFormula"/> (the fx heuristic),
        /// which must never see a display string (matches the UDEF/IPROP expression readers).
        /// </summary>
        public string ReadParameterExpression(Document doc, string paramName)
        {
            if (doc == null) return "";

            Parameters parameters = GetParameters(doc);
            if (parameters == null) return "";

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

            return "";
        }

        /// <summary>
        /// Reads the *evaluated* value of a parameter, formatted in the document's units
        /// (e.g. "120 mm"). This is the read-state display for PARAM rows; the equation
        /// (from <see cref="ReadParameterExpression"/>) is shown only in the fx state.
        /// </summary>
        public string ReadParameterValue(Document doc, string paramName)
        {
            if (doc == null) return NotAvailable;

            Parameters parameters = GetParameters(doc);
            if (parameters == null) return NotAvailable;

            try
            {
                var up = parameters.UserParameters[paramName];
                object val = up.Value;
                if (val is string s) return string.IsNullOrEmpty(s) ? NotAvailable : s;
                return FormatParameterValue(doc, Convert.ToDouble(val), up.get_Units());
            }
            catch { }

            try
            {
                var mp = parameters.ModelParameters[paramName];
                object val = mp.Value;
                if (val is string s) return string.IsNullOrEmpty(s) ? NotAvailable : s;
                return FormatParameterValue(doc, Convert.ToDouble(val), mp.get_Units());
            }
            catch { }

            return NotAvailable;
        }

        private static Parameters GetParameters(Document doc)
        {
            try
            {
                if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                    return ((PartDocument)doc).ComponentDefinition.Parameters;
                if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                    return ((AssemblyDocument)doc).ComponentDefinition.Parameters;
            }
            catch { }
            return null;
        }

        // Parameter.Value is in internal database units (cm/radians). GetStringFromValue converts
        // it to the parameter's own units honouring document precision; we append the unit token
        // when the formatted result is a bare number so length/angle rows read like "120 mm".
        private static string FormatParameterValue(Document doc, double value, object units)
        {
            // Parameter.Units is a COM dual-accessor (get_Units) returning the unit token as text.
            string unitStr = units != null ? units.ToString() : "";

            string text;
            try
            {
                var uom = doc.UnitsOfMeasure;            // COM two-dot rule: keep the accessor in a local
                text = uom.GetStringFromValue(value, units);
            }
            catch { text = value.ToString(System.Globalization.CultureInfo.InvariantCulture); }

            if (!string.IsNullOrEmpty(unitStr)
                && !unitStr.Equals("ul", StringComparison.OrdinalIgnoreCase)   // 'ul' = unitless
                && !HasLetter(text))
                text = text.TrimEnd() + " " + unitStr;

            return text;
        }

        private static bool HasLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (char.IsLetter(c)) return true;
            return false;
        }

        /// <summary>
        /// True when a parameter expression is genuinely formula-driven (references another
        /// parameter or contains arithmetic) rather than a plain literal like "120 mm".
        /// Drives whether a PARAM row offers the fx toggle.
        /// </summary>
        public static bool IsParameterFormula(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;
            string t = expression.Trim();

            // Defense-in-depth: the display sentinel is not an expression. ReadParameterExpression
            // already returns "" for a missing parameter, but guard here too — the '/' below would
            // otherwise read NotAvailable ("n/a") as division and misclassify it as formula-driven.
            if (string.Equals(t, NotAvailable, StringComparison.OrdinalIgnoreCase)) return false;

            // Any arithmetic operator or function call → formula.
            if (t.IndexOfAny(new[] { '+', '-', '*', '/', '^', '(', ')' }) >= 0) return true;

            string[] tokens = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            char f = tokens[0][0];
            bool firstNumeric = char.IsDigit(f) || f == '.';
            if (!firstNumeric) return true;        // bare parameter reference, e.g. "d3"
            return tokens.Length > 2;              // "120 mm" = literal; more tokens → formula
        }
    }
}