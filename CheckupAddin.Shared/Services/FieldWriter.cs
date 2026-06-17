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

        private int _batchDepth = 0;
        private readonly HashSet<Document> _batchDirty = new();

        public IDisposable BeginBatch() => new BatchScope(this);

        private sealed class BatchScope : IDisposable
        {
            private readonly FieldWriter _fw;
            internal BatchScope(FieldWriter fw) { _fw = fw; fw._batchDepth++; }
            public void Dispose()
            {
                if (--_fw._batchDepth > 0) return;
                var docs = new List<Document>(_fw._batchDirty);
                _fw._batchDirty.Clear();
                foreach (var doc in docs) _fw.TryUpdate(doc);
            }
        }

        private void RecordOrUpdate(Document doc)
        {
            if (_batchDepth > 0) _batchDirty.Add(doc);
            else TryUpdate(doc);
        }

        private void TryUpdate(Document doc)
        {
            bool prevSilent = false;
            bool silentSet  = false;
            try { prevSilent = _app.SilentOperation; _app.SilentOperation = true; silentSet = true; } catch { }
            try
            {
                if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                    ((PartDocument)doc).Update();
                else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                    ((AssemblyDocument)doc).Update();
            }
            catch { }
            finally { if (silentSet) { try { _app.SilentOperation = prevSilent; } catch { } } }
        }

        private static bool IsUserDefinedSet(string setName) =>
            FieldCatalogBuilder.IsUserDefinedSet(setName);

        public string WriteFieldValue(Document doc, string fieldKey, string newValue)
        {
            if (doc == null) return "No document.";
            if (string.IsNullOrWhiteSpace(fieldKey)) return "No field key.";

            try
            {
                string err;
                if (fieldKey.StartsWith("UDEF:"))
                    err = WriteUserDefinedProperty(doc, fieldKey["UDEF:".Length..], newValue);
                else if (fieldKey.StartsWith("IPROP|"))
                    err = WriteStandardProperty(doc, fieldKey, newValue);
                else if (fieldKey.StartsWith("PARAM:User:"))
                    err = WriteParameter(doc, fieldKey["PARAM:User:".Length..], newValue);
                else if (fieldKey.StartsWith("PARAM:Model:"))
                    err = WriteParameter(doc, fieldKey["PARAM:Model:".Length..], newValue);
                else if (fieldKey.StartsWith("DOC:"))
                    err = WriteDocumentValue(doc, fieldKey["DOC:".Length..], newValue);
                else
                    return "Field type is not writable.";

                if (err == null) RecordOrUpdate(doc);
                return err;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// True when <paramref name="value"/> is an Inventor iProperty expression — i.e. it
        /// starts with '='. Inventor itself uses the leading '=' to distinguish a formula
        /// (e.g. "=&lt;Width&gt; x &lt;Height&gt;") from a literal text value.
        /// </summary>
        internal static bool LooksLikeFormula(string value) =>
            !string.IsNullOrEmpty(value) && value.TrimStart().StartsWith("=", StringComparison.Ordinal);

        /// <summary>
        /// Writes a value to a text iProperty, preserving Inventor's formula semantics.
        ///
        /// Literal write (no leading '='): sets Property.Value directly — this replaces any
        /// existing expression with the fixed text, exactly as Inventor's own dialog does.
        ///
        /// Formula write (leading '='): the precise API path is not yet confirmed for
        /// Inventor 2026, so we probe two routes and log which one takes (DiagLogger "fx"):
        ///   1) Property.Expression = "=…"   (late-bound; preferred if supported)
        ///   2) Property.Value      = "=…"   (dialog-equivalent fallback — Inventor parses
        ///                                     a leading '=' in Value into an expression)
        /// Returns null on success or an error string.
        /// </summary>
        private static string SetPropertyValueOrFormula(Property prop, string value, bool isFormula, string setName, string propName)
        {
            if (!isFormula)
            {
                prop.Value = value;
                return null;
            }

            // Probe 1: Property.Expression (late-bound to avoid a hard interop dependency).
            try
            {
                Microsoft.VisualBasic.Interaction.CallByName(
                    prop, "Expression", Microsoft.VisualBasic.CallType.Let, value);
                string back = TryReadExpression(prop);
                if (LooksLikeFormula(back))
                {
                    DiagLogger.Log("fx", $"WRITE formula via Property.Expression OK — '{DiagLogger.S(setName)}.{DiagLogger.S(propName)}' = '{DiagLogger.S(value)}' (read-back '{DiagLogger.S(back)}')");
                    return null;
                }
                DiagLogger.Log("fx", $"Property.Expression set on '{DiagLogger.S(setName)}.{DiagLogger.S(propName)}' but read-back '{DiagLogger.S(back)}' is not an expression — trying Value path");
            }
            catch (Exception ex)
            {
                DiagLogger.Log("fx", $"Property.Expression failed on '{DiagLogger.S(setName)}.{DiagLogger.S(propName)}': {DiagLogger.S(ex.Message)} — trying Value path");
            }

            // Probe 2: Property.Value = "=…" (dialog-equivalent).
            try
            {
                prop.Value = value;
                string back = TryReadExpression(prop);
                DiagLogger.Log("fx", $"WRITE formula via Property.Value — '{DiagLogger.S(setName)}.{DiagLogger.S(propName)}' = '{DiagLogger.S(value)}' → Value '{DiagLogger.S(prop.Value?.ToString())}', Expression '{DiagLogger.S(back)}'");
                return null;
            }
            catch (Exception ex)
            {
                DiagLogger.Log("fx", $"Property.Value formula write failed on '{DiagLogger.S(setName)}.{DiagLogger.S(propName)}': {DiagLogger.S(ex.Message)}");
                return $"Could not set formula on '{propName}': {ex.Message}";
            }
        }

        /// <summary>Reads Property.Expression via late binding; returns "" if unsupported or empty.</summary>
        internal static string TryReadExpression(Property prop)
        {
            try
            {
                object o = Microsoft.VisualBasic.Interaction.CallByName(
                    prop, "Expression", Microsoft.VisualBasic.CallType.Get);
                return o?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private string WriteUserDefinedProperty(Document doc, string propName, string value)
        {
            // Inventor's "Export Parameter" feature publishes a UserParameter into the Custom
            // iProperties tab under the same name. The iProperty is read-only (doc.Update()
            // reverts any direct write). Detect this case and redirect to the parameter.
            try
            {
                Parameters prms = null;
                if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                    prms = ((PartDocument)doc).ComponentDefinition.Parameters;
                else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                    prms = ((AssemblyDocument)doc).ComponentDefinition.Parameters;

                if (prms != null)
                {
                    bool hasMatchingParam = false;
                    try { hasMatchingParam = prms.UserParameters[propName] != null; } catch { }
                    if (hasMatchingParam)
                        return WriteParameter(doc, propName, value);
                }
            }
            catch { }

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
                bool isFormula = LooksLikeFormula(value);
                string err = SetPropertyValueOrFormula(target, value, isFormula, foundSet, propName);
                if (err != null) return err;
                // Read-back verification only applies to literal writes — a formula's Value
                // evaluates to a different display string than the "=…" expression we set.
                if (!isFormula)
                {
                    string after = target.Value?.ToString() ?? "(null)";
                    if (!string.Equals(after, value, StringComparison.Ordinal))
                        return $"Type mismatch or read-only: set='{value}', read-back='{after}' (was '{before}'). Set '{foundSet}'.";
                }
                return null;
            }
            catch (Exception ex) { return $"Set '{foundSet}', prop '{propName}': {ex.Message}"; }
        }

        private string WriteStandardProperty(Document doc, string fieldKey, string value)
        {
            var parts = fieldKey.Split('|');
            if (parts.Length < 2) return "Invalid field key.";
            // 3-part key: IPROP|SetName|PropName  — try set name first, fall back to scan
            // 2-part key: IPROP|PropName           — skip set-name lookup, go straight to scan
            string setHint  = parts.Length >= 3 ? parts[1] : "";
            string propName = parts.Length >= 3 ? parts[2] : parts[1];
            try
            {
                PropertySet ps = null;
                if (!string.IsNullOrEmpty(setHint))
                {
                    // Try stored name first, then language-variant candidates (handles German↔English mismatch).
                    foreach (var candidate in FieldCatalogBuilder.GetSetNameCandidates(setHint))
                    {
                        try { ps = doc.PropertySets[candidate]; } catch { }
                        if (ps != null) break;
                    }
                }
                // Last resort: scan all non-user-defined sets for the property name.
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
                if (ps == null) return $"Property '{propName}' not found in any property set.";
                string setName = "";
                try { setName = ps.DisplayName ?? ps.Name ?? "?"; } catch { setName = "?"; }
                return SetPropertyValueOrFormula(ps[propName], value, LooksLikeFormula(value), setName, propName);
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

            // Suppress Inventor's own "Invalid Value" modal so a rejected equation surfaces as our
            // in-place red text / status line instead of a blocking dialog. The COM call still
            // throws on a bad expression — caught by TrySetExpression and returned as an error string.
            // Restored in finally so we never leave Inventor in a silent state.
            bool prevSilent = false;
            bool silentSet  = false;
            try { prevSilent = _app.SilentOperation; _app.SilentOperation = true; silentSet = true; } catch { }

            try
            {
                // Do NOT call Update() here — WriteFieldValue calls RecordOrUpdate on null return.
                // A double Update() inside a batch scope would defeat the storm-prevention design.
                // A failed lookup leaves COM in a partially dirty state; never Update() after a failure.
                return TrySetExpression(prms, paramName, expr);
            }
            finally
            {
                if (silentSet) { try { _app.SilentOperation = prevSilent; } catch { } }
            }
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
            foreach (char c in expr)
                if (char.IsLetter(c)) return expr;   // already has unit or is a reference

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
        /// Getting the item — three sources tried in order:
        ///   A) dynamic _app.MaterialLibraries / AppearanceLibraries (old API — DLR IDispatch fallback).
        ///   B) dynamic AssetLibrary.Materials / Appearances (bridge property — DLR IDispatch fallback).
        ///   C) AssetLibrary.MaterialAssets / AppearanceAssets (new API — confirmed in Inventor 2026).
        ///
        /// Setting the item — four methods tried per target:
        ///   1) dynamic both sides (VT_DISPATCH, no .NET coercion).
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
            displayName = displayName?.Trim() ?? "";
            DiagLogger.Section("asset", $"{label} search: '{displayName}'");

            var errors = new List<string>();

            // ── Source A: Application.MaterialLibraries / AppearanceLibraries via dynamic ──
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
                catch (Exception ex) { errors.Add($"B.dyn: {ex.Message}"); }

                // Source C: new Asset collection (MaterialAssets / AppearanceAssets).
                // Inventor 2026 exposes materials as Asset objects via this property.
                // For Material: PartDocument.ActiveMaterial accepts Asset objects.
                try
                {
                    var coll = TryGetCollection(lib, assetsProp);
                    if (coll == null) continue;
                    foreach (var item in coll)
                    {
                        try
                        {
                            string dn = ReadDisplayName(item);
                            if (!string.Equals(dn, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                            DiagLogger.Log("asset", $"C hit: name='{dn}' type={item?.GetType()?.Name ?? "null"} lib={lib.DisplayName}");
                            string e = SetItemOnPart(part, setProp, isMaterial, item);
                            DiagLogger.Log("asset", $"C set: {e ?? "SUCCESS"}");
                            if (e == null) return null;
                            errors.Add($"C.{assetsProp}: {e}");
                            break;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            string result = errors.Count > 0
                ? $"{label} '{displayName}' found but assignment failed: {string.Join(" | ", errors)}"
                : $"{label} '{displayName}' not found.";
            DiagLogger.Log("asset", $"Result: {result}");
            return result;
        }

        private string SetItemOnPart(PartDocument part, string setProp, bool isMaterial, object item)
        {
            if (isMaterial)
            {
                // PartDocument.ActiveMaterial accepts Asset objects (Inventor 2026).
                // PartComponentDefinition.Material requires old-style Material COM type.
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
        private static string TrySetOnTarget(object target, string prop, object value)
        {
            // Method 1: pure dynamic dispatch — no .NET arg coercion (VT_DISPATCH)
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
        /// Sets a property via Type.InvokeMember (calls IDispatch without DLR type coercion).
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

        // Reads DisplayName then Name, strips "N:" numeric prefix, skips GUID-format strings.
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
                    if (colon > 0 && colon < 4 && int.TryParse(v[..colon], out _))
                        v = v[(colon + 1)..].Trim();

                    if (!LooksLikeGuid(v)) return v;
                }
                catch { }
            }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex _guidRegex =
            new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|" +
                @"^([0-9a-fA-F]{8}-){3}[0-9a-fA-F]{8}$");

        private static bool LooksLikeGuid(string s)
        {
            if (s == null || s.Length < 32) return false;
            return _guidRegex.IsMatch(s);
        }
    }
}
