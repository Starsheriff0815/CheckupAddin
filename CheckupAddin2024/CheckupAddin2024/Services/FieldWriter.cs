using System;
using System.Collections.Generic;
using System.Linq;
using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Writes edited values back to Inventor documents.
    /// Returns null on success, or an error string on failure.
    /// </summary>
    public class FieldWriter
    {
        private readonly Inventor.Application _app;

        public FieldWriter(Inventor.Application app)
        {
            _app = app;
        }

        private static bool IsUserDefinedSet(string setName) =>
            FieldCatalogBuilder.IsUserDefinedSet(setName);

        public string WriteFieldValue(Document doc, string fieldKey, string newValue)
        {
            if (doc == null) return "No document.";
            if (string.IsNullOrWhiteSpace(fieldKey)) return "No field key.";

            try
            {
                if (fieldKey.StartsWith("UDEF:"))
                    return WriteUserDefinedProperty(doc, fieldKey.Substring("UDEF:".Length), newValue);

                if (fieldKey.StartsWith("IPROP|"))
                    return WriteStandardProperty(doc, fieldKey, newValue);

                if (fieldKey.StartsWith("PARAM:User:"))
                    return WriteParameter(doc, fieldKey.Substring("PARAM:User:".Length), newValue);

                if (fieldKey.StartsWith("PARAM:Model:"))
                    return WriteParameter(doc, fieldKey.Substring("PARAM:Model:".Length), newValue);

                if (fieldKey.StartsWith("DOC:"))
                    return WriteDocumentValue(doc, fieldKey.Substring("DOC:".Length), newValue);

                return "Field type is not writable.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private string WriteUserDefinedProperty(Document doc, string propName, string value)
        {
            // Enumerate exactly like PropertyReader: match by DisplayName/Name, not by fixed string index.
            // doc.PropertySets["name"] may resolve by internal COM name (differs from DisplayName),
            // causing a mismatch with the set found during catalog build.
            Property target = null;
            string foundSet = null;
            try
            {
                foreach (PropertySet ps in doc.PropertySets)
                {
                    try
                    {
                        string sn = ps.DisplayName ?? ps.Name ?? "";
                        if (!IsUserDefinedSet(sn)) continue;
                        try
                        {
                            var p = ps[propName];
                            if (p != null) { target = p; foundSet = sn; break; }
                        }
                        catch { }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { return $"Could not enumerate property sets: {ex.Message}"; }

            if (target == null)
                return $"User Defined property '{propName}' not found in any user-defined property set.";

            try
            {
                string before = target.Value?.ToString() ?? "(null)";
                target.Value = value;
                string after = target.Value?.ToString() ?? "(null)";
                if (!string.Equals(after, value, StringComparison.Ordinal))
                    return $"Type mismatch or read-only: set='{value}', read-back='{after}' (was '{before}'). Set '{foundSet}'.";
                return null;
            }
            catch (Exception ex) { return $"Set '{foundSet}', prop '{propName}': {ex.Message}"; }
        }

        private string WriteStandardProperty(Document doc, string fieldKey, string value)
        {
            var parts = fieldKey.Split('|');
            if (parts.Length < 2) return "Invalid field key.";
            string setHint  = parts.Length >= 3 ? parts[1] : "";
            string propName = parts.Length >= 3 ? parts[2] : parts[1];
            try
            {
                PropertySet ps = null;
                if (!string.IsNullOrEmpty(setHint))
                {
                    foreach (var candidate in FieldCatalogBuilder.GetSetNameCandidates(setHint))
                    {
                        try { ps = doc.PropertySets[candidate]; } catch { }
                        if (ps != null) break;
                    }
                }
                if (ps == null)
                {
                    foreach (PropertySet candidate in doc.PropertySets)
                    {
                        try
                        {
                            string sn = candidate.DisplayName ?? candidate.Name ?? "";
                            if (IsUserDefinedSet(sn)) continue;
                            Property dummy = candidate[propName];
                            if (dummy != null) { ps = candidate; break; }
                        }
                        catch { }
                    }
                }
                if (ps == null) return "Property '" + propName + "' not found in any property set.";
                ps[propName].Value = value;
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private string WriteParameter(Document doc, string paramName, string value)
        {
            string expr = value.Replace(",", ".");

            Parameters prms = null;
            if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                prms = ((PartDocument)doc).ComponentDefinition.Parameters;
            else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                prms = ((AssemblyDocument)doc).ComponentDefinition.Parameters;
            else
                return $"Parameter write not supported for document type {doc.DocumentType}.";

            // Set the expression on whichever collection owns the parameter.
            // Do NOT call Update() inside these try blocks — a failed lookup leaves
            // COM in a partially dirty state, and a second Update() in the fallback crashes Inventor.
            string setErr = TrySetExpression(prms, paramName, expr);
            if (setErr != null) return setErr;

            // Update exactly once, after a confirmed successful expression assignment.
            try
            {
                if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                    ((PartDocument)doc).Update();
                else
                    ((AssemblyDocument)doc).Update();
                return null;
            }
            catch (Exception ex) { return $"Expression set OK but Update failed: {ex.Message}"; }
        }

        private static string TrySetExpression(Parameters prms, string paramName, string expr)
        {
            // UserParameter and ModelParameter are separate COM types — no common base Parameter assignable here.
            UserParameter userParam = null;
            try { userParam = prms.UserParameters[paramName]; } catch { }
            if (userParam != null)
            {
                string currentExpr = "";
                try { currentExpr = userParam.Expression ?? ""; } catch { }

                // Case A: Breite.Expression = "Höhe" — Breite mirrors another parameter.
                // Redirect to that parameter so dependents see the change.
                string referenced = ResolveParamReference(prms, currentExpr);
                if (referenced != null) return TrySetExpression(prms, referenced, expr);

                // Case B: d25.Expression = "Breite" — d25 is driven by Breite.
                // Set this parameter directly; doc.Update() in WriteParameter propagates through the chain.
                // Auto-append the unit so "150" becomes "150 mm" when the current expression is "100 mm".
                string finalExpr = NormalizeExpr(expr, currentExpr);
                try { userParam.Expression = finalExpr; return null; }
                catch (Exception ex) { return $"Cannot set '{paramName}': {ex.Message}"; }
            }

            ModelParameter modelParam = null;
            try { modelParam = prms.ModelParameters[paramName]; } catch { }
            if (modelParam != null)
            {
                string currentExpr = "";
                try { currentExpr = modelParam.Expression ?? ""; } catch { }

                string referenced = ResolveParamReference(prms, currentExpr);
                if (referenced != null) return TrySetExpression(prms, referenced, expr);

                string finalExpr = NormalizeExpr(expr, currentExpr);
                try { modelParam.Expression = finalExpr; return null; }
                catch (Exception ex) { return $"Cannot set '{paramName}': {ex.Message}"; }
            }

            return $"Parameter '{paramName}' not found in user or model parameters.";
        }

        // When the user types a bare number ("150"), extract the unit from the current expression
        // ("100 mm") and append it so Inventor gets "150 mm" instead of a dimensionless literal.
        private static string NormalizeExpr(string expr, string currentExpr)
        {
            for (int i = 0; i < expr.Length; i++)
                if (char.IsLetter(expr[i])) return expr;   // already has unit or is a reference

            string unit = ExtractUnit(currentExpr);
            return string.IsNullOrEmpty(unit) ? expr : expr.TrimEnd() + " " + unit;
        }

        // Extracts "mm" from "100 mm", returns null for references, formulas, or bare numbers.
        private static string ExtractUnit(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            string trimmed = expression.Trim();
            // Reject formulas (arithmetic operators)
            if (trimmed.IndexOfAny(new[] { '+', '-', '*', '/', '^', '(', ')' }) >= 0) return null;

            string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return null;

            // First token must be a numeric literal (starts with digit or decimal point)
            char f = parts[0][0];
            if (!char.IsDigit(f) && f != '.') return null;

            // Second token must be a short all-letter unit abbreviation
            string unit = parts[1];
            if (unit.Length == 0 || unit.Length > 6) return null;
            foreach (char c in unit) if (!char.IsLetter(c)) return null;
            return unit;
        }

        private static string ResolveParamReference(Parameters prms, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;

            string trimmed = expression.Trim();
            // Reject formulas — simple references have no arithmetic operators
            if (trimmed.IndexOfAny(new[] { '+', '-', '*', '/', '^', '(', ')' }) >= 0)
                return null;

            // Allow "paramName" or "paramName unit" (e.g. "d25" or "d25 mm")
            string[] tokens = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 2) return null;

            string candidate = tokens[0];
            if (candidate.Length == 0) return null;

            // Numeric literals start with a digit — reject them (and allow non-ASCII names like Höhe)
            if (char.IsDigit(candidate[0])) return null;

            // Confirm the candidate is an actual parameter in this document
            try { if (prms.UserParameters[candidate] != null) return candidate; } catch { }
            try { if (prms.ModelParameters[candidate] != null) return candidate; } catch { }
            return null;
        }

        private string WriteDocumentValue(Document doc, string tag, string value)
        {
            if (doc.DocumentType != DocumentTypeEnum.kPartDocumentObject)
                return "Document values can only be set on Part documents.";

            var part = (PartDocument)doc;

            if (string.Equals(tag, "Material", StringComparison.Ordinal))
            {
                try { return ApplyAsset(part, value, isMaterial: true); }
                catch (Exception ex) { return ex.Message; }
            }

            if (string.Equals(tag, "Appearance", StringComparison.Ordinal))
            {
                try { return ApplyAsset(part, value, isMaterial: false); }
                catch (Exception ex) { return ex.Message; }
            }

            return "This document value is read-only.";
        }

        /// <summary>
        /// Finds the named material/appearance and assigns it to the part.
        ///
        /// Getting the item — four sources tried in order:
        ///   A) dynamic _app.MaterialLibraries  — old API; not in 2026 interop type but DLR falls
        ///      back to IDispatch, so it may still be found in Inventor 2024's COM server.
        ///   B) dynamic AssetLibrary.Materials  — bridge property; same DLR IDispatch fallback.
        ///   C) TryGetCollection "MaterialAssets" — new API Asset objects (confirmed reachable).
        ///
        /// Setting the item — four methods tried in order per target:
        ///   1) dynamic both sides: DLR → IDispatch, NO .NET arg coercion (value passed as VT_DISPATCH).
        ///   2) CallByName Let   (DISPATCH_PROPERTYPUT).
        ///   3) CallByName Set   (DISPATCH_PROPERTYPUTREF).
        ///   4) Type.InvokeMember SetProperty.
        ///
        /// Returns null on success, or a diagnostic error string on failure.
        /// </summary>
        private string ApplyAsset(PartDocument part, string displayName, bool isMaterial)
        {
            string label      = isMaterial ? "Material"          : "Appearance";
            string assetsProp = isMaterial ? "MaterialAssets"    : "AppearanceAssets";
            string setProp    = isMaterial ? "Material"          : "ActiveAppearance";

            var errors = new System.Collections.Generic.List<string>();

            // ── Source A: Application.MaterialLibraries via dynamic ──
            // CallByName uses .NET reflection and fails when the property is absent from the
            // 2026 interop type.  dynamic dispatches through IDispatch as a fallback and may
            // still find MaterialLibraries in Inventor 2024's COM server.
            try
            {
                dynamic dynApp = _app;
                dynamic libs   = isMaterial ? dynApp.MaterialLibraries : dynApp.AppearanceLibraries;
                foreach (dynamic lib in libs)
                {
                    try
                    {
                        dynamic coll = isMaterial ? lib.Materials : lib.Appearances;
                        foreach (dynamic item in coll)
                        {
                            try
                            {
                                string n = item.Name?.ToString() ?? "";
                                if (!string.Equals(n, displayName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                string e = SetItemOnPart(part, setProp, isMaterial, item);
                                if (e == null) return null;
                                errors.Add($"A: {e}");
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { errors.Add($"A.{(isMaterial ? "MaterialLibraries" : "AppearanceLibraries")}: {ex.Message}"); }

            // ── Sources B + C: per AssetLibrary ──
            foreach (AssetLibrary lib in _app.AssetLibraries)
            {
                // Source B: bridge property via dynamic (DLR → IDispatch fallback).
                // AssetLibrary.Materials is absent from the 2026 interop type but may be exposed
                // by IDispatch in Inventor 2024, where it returns proper Material COM objects.
                try
                {
                    dynamic dynLib = lib;
                    dynamic coll   = isMaterial ? dynLib.Materials : dynLib.Appearances;
                    foreach (dynamic item in coll)
                    {
                        try
                        {
                            string n  = ""; try { n  = item.Name?.ToString()        ?? ""; } catch { }
                            string dn = ""; try { dn = item.DisplayName?.ToString() ?? ""; } catch { }
                            if (!string.Equals(n,  displayName, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(dn, displayName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            string e = SetItemOnPart(part, setProp, isMaterial, item);
                            if (e == null) return null;
                            errors.Add($"B.{(isMaterial ? "Materials" : "Appearances")}: {e}");
                            break;
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { errors.Add($"B.dyn.{(isMaterial ? "Materials" : "Appearances")}: {ex.Message}"); }

                // Source C: new Asset collection (works for Appearance; Material setter rejects Asset
                // with TYPEMISMATCH — kept as last resort in case a future Inventor version accepts it).
                try
                {
                    var coll = TryGetCollection(lib, assetsProp);
                    if (coll == null) continue;
                    foreach (var item in coll)
                    {
                        try
                        {
                            if (!string.Equals(ReadDisplayName(item), displayName,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            string e = SetItemOnPart(part, setProp, isMaterial, item);
                            if (e == null) return null;
                            errors.Add($"C.{assetsProp}: {e}");
                            break;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return errors.Count > 0
                ? $"{label} '{displayName}' found but assignment failed: {string.Join(" | ", errors)}"
                : $"{label} '{displayName}' not found.";
        }

        // Tries all setter targets appropriate for the asset type.
        private string SetItemOnPart(PartDocument part, string setProp, bool isMaterial, object item)
        {
            if (isMaterial)
            {
                // PartDocument.ActiveMaterial accepts Asset objects (Inventor 2024+).
                // PartComponentDefinition.Material requires an old-style Material COM type
                // and rejects Asset objects with TYPEMISMATCH.
                string e1 = TrySetOnTarget(part, "ActiveMaterial", item);
                if (e1 == null) return null;
                string e2 = TrySetOnTarget(part.ComponentDefinition, setProp, item);
                if (e2 == null) return null;
                return $"Part.ActiveMaterial:{e1} | CompDef.{setProp}:{e2}";
            }

            string ea = TrySetOnTarget(part.ComponentDefinition, setProp, item);
            if (ea == null) return null;
            string eb = TrySetOnTarget(part, setProp, item);
            if (eb == null) return null;
            return $"CompDef:{ea} | Part:{eb}";
        }

        // Tries four dispatch methods in order. Returns null on first success.
        // Method 1 — dynamic both sides: DLR sends VT_DISPATCH to IDispatch.Invoke with no
        //   .NET type coercion for the argument. Catches cases where the COM server accepts
        //   the raw IDispatch pointer even though .NET reflection would reject the type.
        // Methods 2-4 — CallByName Let/Set and InvokeMember: fallbacks for typed dispatch.
        private static string TrySetOnTarget(object target, string prop, object value)
        {
            // Method 1: pure dynamic dispatch — no .NET arg coercion
            try
            {
                dynamic dT = target;
                dynamic dV = value;
                if      (string.Equals(prop, "Material", StringComparison.Ordinal))         dT.Material         = dV;
                else if (string.Equals(prop, "ActiveMaterial", StringComparison.Ordinal))   dT.ActiveMaterial   = dV;
                else if (string.Equals(prop, "ActiveAppearance", StringComparison.Ordinal)) dT.ActiveAppearance = dV;
                else Microsoft.VisualBasic.Interaction.CallByName(dT, prop,
                         Microsoft.VisualBasic.CallType.Set, dV);
                return null;
            }
            catch (Exception exDyn)
            {
                // Method 2: CallByName Let (DISPATCH_PROPERTYPUT)
                try
                {
                    Microsoft.VisualBasic.Interaction.CallByName(
                        target, prop, Microsoft.VisualBasic.CallType.Let, value);
                    return null;
                }
                catch (Exception exLet)
                {
                    // Method 3: CallByName Set (DISPATCH_PROPERTYPUTREF)
                    try
                    {
                        Microsoft.VisualBasic.Interaction.CallByName(
                            target, prop, Microsoft.VisualBasic.CallType.Set, value);
                        return null;
                    }
                    catch (Exception exSet)
                    {
                        // Method 4: InvokeMember
                        string err = InvokeMemberSet(target, prop, value);
                        if (err == null) return null;
                        return $"dyn:{exDyn.Message} | Let:{exLet.Message} | Set:{exSet.Message} | Inv:{err}";
                    }
                }
            }
        }

        private static System.Collections.IEnumerable TryGetCollection(object source, string prop)
        {
            try
            {
                var obj = Microsoft.VisualBasic.Interaction.CallByName(
                    source, prop, Microsoft.VisualBasic.CallType.Get);
                return obj as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        /// <summary>
        /// Sets a property via Type.InvokeMember, which calls IDispatch directly without the
        /// DLR's interop-metadata type coercion. This allows passing a raw COM object as a
        /// property value even when its .NET wrapper type doesn't match the interop signature.
        /// Returns null on success, or the exception message on failure.
        /// </summary>
        private static string InvokeMemberSet(object target, string propName, object value)
        {
            try
            {
                target.GetType().InvokeMember(
                    propName,
                    System.Reflection.BindingFlags.SetProperty
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance,
                    null, target, new[] { value });
                return null;
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                return tie.InnerException?.Message ?? tie.Message;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// Reads DisplayName then Name, strips leading "N:" numeric prefix, skips GUID-format strings.
        private static string ReadDisplayName(object asset)
        {
            foreach (string prop in new[] { "DisplayName", "Name" })
            {
                try
                {
                    string v = Microsoft.VisualBasic.Interaction.CallByName(
                        asset, prop, Microsoft.VisualBasic.CallType.Get)?.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    int colon = v.IndexOf(':');
                    if (colon > 0 && colon < 4 && int.TryParse(v.Substring(0, colon), out _))
                        v = v.Substring(colon + 1).Trim();

                    if (!LooksLikeGuid(v)) return v;
                }
                catch { }
            }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex _guidRegex =
            new System.Text.RegularExpressions.Regex(
                @"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|" +
                @"^([0-9a-fA-F]{8}-){3}[0-9a-fA-F]{8}$");

        private static bool LooksLikeGuid(string s)
        {
            if (s == null || s.Length < 32) return false;
            return _guidRegex.IsMatch(s);
        }
    }
}
