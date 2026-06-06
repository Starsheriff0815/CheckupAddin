using Inventor;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Builds and caches the list of all selectable fields for the row dropdown.
    /// The catalog is rebuilt when the active document path changes; call InvalidateCache()
    /// after writing a value to ensure newly visible parameters appear on next refresh.
    /// </summary>
    /// <remarks>
    /// Field key format — must match FieldWriter.WriteFieldValue and FieldItem.Key conventions:
    ///   SPECIAL:MiterGap / SPECIAL:FlangeDistance        — sheet metal computed values (read via SheetMetalReader)
    ///   SPECIAL:Spezi1 / SPECIAL:Spezi2                  — Spezi Baukasten rows; map to UDEF SPEZIFIK1/2 internally
    ///   SPECIAL:Halbzeug                                  — pseudo-key; expands to HalbzeugName + HalbzeugIdent pair
    ///   SPECIAL:HalbzeugName / SPECIAL:HalbzeugIdent      — raw material rows; map to UDEF ROHTEILNAME/ROHTEILIDENT
    ///   DOC:&lt;tag&gt;                                        — PartDocument properties (Material, Appearance, units…)
    ///   IPROP|&lt;setName&gt;|&lt;propName&gt;                        — standard PropertySet (set looked up by exact COM name)
    ///   UDEF:&lt;propName&gt;                                  — user-defined iProperties (any language variant of the set name)
    ///   PARAM:User:&lt;name&gt;                                — UserParameter
    ///   PARAM:Model:&lt;name&gt;                               — ModelParameter
    /// </remarks>
    public class FieldCatalogBuilder
    {
        private readonly Inventor.Application _app;
        private readonly CapabilityStore      _capStore;

        private readonly PropertyReader _propReader = new();

        // Cache: keyed by document full path — avoids rebuilding on every refresh call for the same document.
        private string _cachedDocPath = "";
        private List<FieldItem> _cachedCatalog;

        public FieldCatalogBuilder(Inventor.Application app = null, CapabilityStore capStore = null)
        {
            _app      = app;
            _capStore = capStore;
        }

        /// <summary>Returned by ResolveFieldValue when a SPECIAL:LOGIC: chain forms a closed loop. DoRefreshCore renders this as a user-visible warning.</summary>
        public const string CycleSentinel          = "#CYCLE";

        public const string FIELD_MITER_GAP       = "SPECIAL:MiterGap";
        public const string FIELD_FLANGE_DISTANCE  = "SPECIAL:FlangeDistance";
        public const string FIELD_HALBZEUG         = "SPECIAL:Halbzeug";
        public const string FIELD_HALBZEUG_NAME    = "SPECIAL:HalbzeugName";
        public const string FIELD_HALBZEUG_IDENT   = "SPECIAL:HalbzeugIdent";

        // Group constants are language keys — GroupNameConverter translates them for display.
        internal const string GRP_NONE        = "";
        private  const string GRP_SHEET_METAL = "Grp_SheetMetal";
        internal const string GRP_SPECIAL     = "Grp_Special";
        private const string GRP_DOCUMENT    = "Grp_Document";
        private const string GRP_IPROP       = "Grp_iProperties";
        private const string GRP_IPROP_CUST  = "Grp_iPropertiesCustom";
        private const string GRP_PARAM_USER  = "Grp_ParamUser";
        private const string GRP_PARAM_MODEL = "Grp_ParamModel";

        // Inventor localises standard property-set DisplayNames. This table maps each known
        // non-English display name to its canonical English form so IPROP keys are stable
        // across language installs. Every entry appears twice (DE→EN and EN→DE) so that
        // GetSetNameCandidates can try both directions when resolving a stored key.
        private static readonly Dictionary<string, string> _setNameNorm =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Design Tracking - Eigenschaften"] = "Design Tracking Properties",
                ["Design Tracking Properties"]      = "Design Tracking - Eigenschaften",
                ["Inventor-Zusammenfassung"]         = "Inventor Summary Information",
                ["Inventor Summary Information"]    = "Inventor-Zusammenfassung",
                ["Dokument-Zusammenfassung"]         = "Document Summary Information",
                ["Document Summary Information"]    = "Dokument-Zusammenfassung",
            };

        /// <summary>Returns the canonical (English) set name for storage in IPROP keys.
        /// Maps known German display names to their English equivalents; unknown names pass through.</summary>
        private static string NormalizeSetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            // Map German → English (e.g. "Design Tracking - Eigenschaften" → "Design Tracking Properties")
            if (_setNameNorm.TryGetValue(name, out string alt) &&
                string.Compare(name, alt, StringComparison.OrdinalIgnoreCase) > 0)
                return alt; // alt is alphabetically earlier → assume it is the English canonical form
            // Heuristic: if the name contains " - " it is likely a localised variant; try the alias
            if (name.Contains(" - ") && _setNameNorm.TryGetValue(name, out string mapped))
                return mapped;
            return name;
        }

        /// <summary>Returns the stored set name plus any known language-variant alias,
        /// so ReadStandardProperty / PropertySets[] can be tried in both languages.</summary>
        internal static string[] GetSetNameCandidates(string storedName)
        {
            if (string.IsNullOrEmpty(storedName)) return new[] { storedName ?? "" };
            return _setNameNorm.TryGetValue(storedName, out string alt)
                ? new[] { storedName, alt }
                : new[] { storedName };
        }

        /// <summary>
        /// Returns the cached catalog if the document path is unchanged, otherwise rebuilds.
        /// </summary>
        public List<FieldItem> GetCatalog(Document doc)
        {
            string docPath = "";
            try { if (doc != null) docPath = doc.FullFileName; } catch { }

            if (docPath != "" && docPath == _cachedDocPath && _cachedCatalog?.Count > 0)
                return _cachedCatalog;

            _cachedCatalog = BuildCatalog(doc);
            _cachedDocPath = docPath;
            return _cachedCatalog;
        }

        /// <summary>Forces a full rebuild on the next GetCatalog call (call after writing a value).</summary>
        public void InvalidateCache()
        {
            _cachedCatalog = null;
            _cachedDocPath = "";
        }

        private List<FieldItem> BuildCatalog(Document doc)
        {
            var items = new List<FieldItem>();

            items.Add(new FieldItem("", LanguageLoader.Get("Field_None"), "", GRP_NONE, false));

            // ── Document values — build AllowedValues lists for asset-backed fields ──
            // Enumerated once per catalog build (triggered by document change, not every refresh).
            var materialNames   = BuildAssetNameList(doc, forMaterial: true);
            var appearanceNames = BuildAssetNameList(doc, forMaterial: false);

            (string tag, string labelKey, bool writable)[] docFields =
            {
                ("Material",           "Field_Material",            true),
                ("Appearance",         "Field_Appearance",          true),
                ("UnitsLength",        "Field_UnitsLength",         false),
                ("UnitsAngle",         "Field_UnitsAngle",          false),
                ("UnitsTime",          "Field_UnitsTime",           false),
                ("UnitsMass",          "Field_UnitsMass",           false),
                ("LinearPrecision",    "Field_LinearPrecision",     false),
                ("AngularPrecision",   "Field_AngularPrecision",    false),
                ("ModelingDimDisplay", "Field_ModelingDimDisplay",  false),
                ("DefaultBOMStructure","Field_DefaultBOMStructure", false),
            };

            foreach (var (tag, labelKey, writable) in docFields)
            {
                string lbl = LanguageLoader.Get(labelKey);
                IReadOnlyList<string> allowed =
                    string.Equals(tag, "Material", StringComparison.Ordinal)   ? materialNames   :
                    string.Equals(tag, "Appearance", StringComparison.Ordinal) ? appearanceNames :
                    null;
                items.Add(new FieldItem($"DOC:{tag}", lbl, lbl, GRP_DOCUMENT, writable,
                    allowedValues: allowed));
            }

            if (doc == null) return items;

            // ── iProperties — enumerate all PropertySets ──
            // Use DisplayName (not internal COM name) to detect user-defined sets;
            // the key for IPROP uses the COM name so FieldWriter can look up the same set.
            try
            {
                foreach (PropertySet ps in doc.PropertySets)
                {
                    string setName = ps.DisplayName ?? ps.Name ?? "Unknown";
                    bool isUserDef = IsUserDefinedSet(setName);

                    foreach (Property prop in ps)
                    {
                        try
                        {
                            string propName = prop.Name;
                            if (string.IsNullOrWhiteSpace(propName)) continue;

                            if (isUserDef)
                            {
                                // UDEF key uses only the property name — FieldWriter searches all user-defined sets.
                                string key = $"UDEF:{propName}";
                                if (!items.Any(x => x.Key == key))
                                    items.Add(new FieldItem(key, propName, propName,
                                        GRP_IPROP_CUST, isWritable: true));
                            }
                            else
                            {
                                // Normalise to the English canonical set name so the key is stable
                                // across German/English Inventor installations.
                                string keySetName = NormalizeSetName(setName);
                                string key = $"IPROP|{keySetName}|{propName}";
                                if (!items.Any(x => x.Key == key))
                                    items.Add(new FieldItem(key, propName, propName,
                                        GRP_IPROP, isWritable: true));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // ── Parameters ──
            if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
            {
                var part = (PartDocument)doc;
                var compDef = part.ComponentDefinition;
                AddParameters(items, compDef.Parameters);
            }
            else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
            {
                var asm = (AssemblyDocument)doc;
                var compDef = asm.ComponentDefinition;
                AddParameters(items, compDef.Parameters);
            }

            // ── Logic Sets: each Card Group with a TargetFieldKey gets a SPECIAL:LOGIC:{group.Id} entry ──
            // These appear in the Special group.
            // S: prefix is rendered in red by XAML via IsSpecialEntry; DropText holds only the label text.
            if (_capStore != null)
            {
                foreach (var cs in _capStore.CapabilitySets)
                {
                    foreach (var group in cs.Groups)
                    {
                        if (string.IsNullOrEmpty(group.TargetFieldKey)) continue;
                        // Only expose in dropdown when at least one Card/BasicLogic is enabled
                        if (!group.Cards.Any(c => c.Enabled)) continue;
                        string key = $"SPECIAL:LOGIC:{group.Id}";
                        if (items.Any(x => x.Key == key)) continue;
                        var targetItem = items.FirstOrDefault(x => x.Key == group.TargetFieldKey);
                        string targetLabel = targetItem != null ? targetItem.DropText : cs.Name;
                        // Append group name when a CapabilitySet has multiple groups
                        if (cs.Groups.Count > 1 && !string.IsNullOrEmpty(group.Name))
                            targetLabel = targetLabel + " · " + group.Name;
                        string dropText = group.IsExpert ? "⚡ " + targetLabel : targetLabel;
                        items.Add(new FieldItem(key, dropText, targetLabel, GRP_SPECIAL, isWritable: true));
                    }
                }
            }

            return items.OrderBy(x => GroupOrder(x.GroupName))
                        .ThenBy(x => x.DropText, NaturalComparer)
                        .ToList();
        }

        private static readonly IComparer<string> NaturalComparer =
            Comparer<string>.Create((a, b) =>
            {
                if (a == null && b == null) return 0;
                if (a == null) return -1;
                if (b == null) return 1;
                int ia = 0, ib = 0;
                while (ia < a.Length && ib < b.Length)
                {
                    bool aDigit = char.IsDigit(a[ia]);
                    bool bDigit = char.IsDigit(b[ib]);
                    if (aDigit && bDigit)
                    {
                        int sa = ia, sb = ib;
                        while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                        while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                        int na = int.Parse(a[sa..ia]);
                        int nb = int.Parse(b[sb..ib]);
                        int c = na.CompareTo(nb);
                        if (c != 0) return c;
                    }
                    else if (!aDigit && !bDigit)
                    {
                        int sa = ia, sb = ib;
                        while (ia < a.Length && !char.IsDigit(a[ia])) ia++;
                        while (ib < b.Length && !char.IsDigit(b[ib])) ib++;
                        int c = string.Compare(a[sa..ia], b[sb..ib],
                                               StringComparison.CurrentCultureIgnoreCase);
                        if (c != 0) return c;
                    }
                    else return aDigit ? 1 : -1;
                }
                return a.Length.CompareTo(b.Length);
            });

        private static int GroupOrder(string g) => g switch
        {
            ""                      => 0,   // (none) — action items
            "Grp_Special"           => 1,   // Logic groups + computed values → always at top
            "Grp_iPropertiesCustom" => 2,
            "Grp_ParamUser"         => 3,
            "Grp_SheetMetal"        => 4,
            "Grp_iProperties"       => 5,
            "Grp_Document"          => 6,
            "Grp_ParamModel"        => 7,
            _                       => 8,
        };

        private static void AddParameters(List<FieldItem> items, Parameters parameters)
        {
            var seenKeys = new HashSet<string>(items.Select(x => x.Key), StringComparer.Ordinal);

            // User parameters first (typically the ones named by the designer).
            try
            {
                foreach (UserParameter up in parameters.UserParameters)
                {
                    try
                    {
                        string key = $"PARAM:User:{up.Name}";
                        if (seenKeys.Add(key))
                        {
                            var allowed = GetParamAllowedValues(up);
                            items.Add(new FieldItem(key, up.Name, up.Name,
                                GRP_PARAM_USER, isWritable: true, allowedValues: allowed));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (ModelParameter mp in parameters.ModelParameters)
                {
                    try
                    {
                        string key = $"PARAM:Model:{mp.Name}";
                        if (seenKeys.Add(key))
                            items.Add(new FieldItem(key, mp.Name, mp.Name,
                                GRP_PARAM_MODEL, isWritable: true));
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the key-value list from a user parameter's ExpressionList via late binding.
        /// Returns null if the parameter has no list or the API call fails.
        /// </summary>
        private static IReadOnlyList<string> GetParamAllowedValues(UserParameter up)
        {
            try
            {
                dynamic exprList = ((dynamic)up).ExpressionList;
                if (exprList == null) return null;

                var raw = exprList.GetExpressionList() as System.Array;
                if (raw == null || raw.Length == 0) return null;

                var result = new List<string>(raw.Length);
                foreach (var item in raw)
                {
                    string s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                }
                return result.Count > 0 ? result : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Enumerates asset names (materials or appearances) from the active library.
        /// Returns a sorted deduplicated list, or null if nothing was found.
        /// </summary>
        private IReadOnlyList<string> BuildAssetNameList(Document doc, bool forMaterial)
        {
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();

            // Property names tried in order — Inventor 2021+ uses "MaterialAssets"/"AppearanceAssets".
            string[] props = forMaterial
                ? new[] { "MaterialAssets",  "Materials"   }
                : new[] { "AppearanceAssets", "Appearances" };

            // 1. Try the document object directly (gives materials already in this file).
            if (doc != null)
            {
                foreach (string prop in props)
                    if (TryEnumerateAssets(doc, prop, seen, names)) break;
            }

            // 2. Walk the active library — mirrors Inventor's material/appearance picker.
            if (_app != null)
            {
                try
                {
                    AssetLibrary activeLib = forMaterial
                        ? _app.ActiveMaterialLibrary
                        : _app.ActiveAppearanceLibrary;
                    if (activeLib != null)
                    {
                        foreach (string prop in props)
                            if (TryEnumerateAssets(activeLib, prop, seen, names)) break;
                    }
                }
                catch { }
            }

            if (names.Count == 0) return null;
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Calls <paramref name="property"/> on <paramref name="source"/> via late binding,
        /// iterates the result, and appends each item's DisplayName/Name to <paramref name="names"/>.
        /// Returns true if at least one name was found.
        /// </summary>
        private static bool TryEnumerateAssets(object source, string property,
            HashSet<string> seen, List<string> names)
        {
            try
            {
                var collection = Microsoft.VisualBasic.Interaction.CallByName(
                    source, property, Microsoft.VisualBasic.CallType.Get);
                if (collection == null) return false;
                if (collection is not System.Collections.IEnumerable enumerable) return false;

                bool found = false;
                foreach (var item in enumerable)
                {
                    try
                    {
                        // Inventor library assets expose user-visible name as "DisplayName";
                        // document-level assets may use "Name". Try DisplayName first.
                        string name = null;
                        foreach (string prop in new[] { "DisplayName", "Name" })
                        {
                            try
                            {
                                string v = Microsoft.VisualBasic.Interaction.CallByName(
                                    item, prop, Microsoft.VisualBasic.CallType.Get)?.ToString();
                                if (!string.IsNullOrWhiteSpace(v) && !LooksLikeGuid(v))
                                {
                                    name = v;
                                    break;
                                }
                            }
                            catch { }
                        }

                        // Strip "N:" numeric prefix returned by some COM collections.
                        if (name != null)
                        {
                            int colon = name.IndexOf(':');
                            if (colon > 0 && colon < 4 && int.TryParse(name[..colon], out _))
                                name = name[(colon + 1)..].Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                            names.Add(name);
                        found = true;
                    }
                    catch { }
                }
                return found;
            }
            catch { return false; }
        }

        private static readonly System.Text.RegularExpressions.Regex _guidRegex =
            new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|" +
                @"^([0-9a-fA-F]{8}-){3}[0-9a-fA-F]{8}$");

        private static bool LooksLikeGuid(string s)
        {
            if (s == null || s.Length < 32) return false;
            return _guidRegex.IsMatch(s);
        }

        /// <summary>
        /// Returns true for property sets that represent the "user defined" / custom iProperties set.
        /// Checked in multiple languages because Inventor localises the set's DisplayName.
        /// </summary>
        internal static bool IsUserDefinedSet(string setName)
        {
            if (string.IsNullOrEmpty(setName)) return false;
            string lower = setName.ToLowerInvariant();
            return lower.Contains("user defined")
                || lower.Contains("benutzerdefiniert")
                || lower.Contains("custom");
        }

        /// <summary>
        /// Resolves a field key to its current display value from the document.
        /// SPECIAL: keys are intentionally not resolved here — DoRefreshCore handles those separately
        /// because they require SheetMetalReader and may add/remove flange-distance rows.
        /// </summary>
        public string ResolveFieldValue(string fieldKey, Document doc)
            => ResolveFieldValueWithCycleGuard(fieldKey, doc, null);

        private string ResolveFieldValueWithCycleGuard(string fieldKey, Document doc, HashSet<string> visitedLogicGroups)
        {
            if (string.IsNullOrWhiteSpace(fieldKey)) return "";
            if (fieldKey == FIELD_MITER_GAP || fieldKey == FIELD_FLANGE_DISTANCE) return "";
            if (fieldKey == FIELD_HALBZEUG) return "";

            if (fieldKey == FIELD_HALBZEUG_NAME)
                return _propReader.ReadUserDefinedProperty(doc, "ROHTEILNAME");
            if (fieldKey == FIELD_HALBZEUG_IDENT)
                return _propReader.ReadUserDefinedProperty(doc, "ROHTEILIDENT");

            // Logic Set row: pass through to the group's configured TargetFieldKey.
            // CYCLE GUARD (V1 safeguard): a Group whose TargetFieldKey points to its own SPECIAL:LOGIC:
            // alias (or any chain A→B→…→A) would otherwise recurse infinitely → stack overflow → Inventor crash.
            if (fieldKey.StartsWith("SPECIAL:LOGIC:"))
            {
                string groupId = fieldKey["SPECIAL:LOGIC:".Length..];
                if (visitedLogicGroups == null) visitedLogicGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visitedLogicGroups.Add(groupId))
                {
                    Services.DiagLogger.Log("resolve", $"SKIP: SPECIAL:LOGIC:{groupId} cycle detected (visited chain: {string.Join(" → ", visitedLogicGroups)})");
                    return CycleSentinel; // DoRefreshCore renders this as a user-visible warning
                }
                var found = _capStore?.FindGroup(groupId);
                if (found == null || string.IsNullOrEmpty(found.Value.Group.TargetFieldKey)) return "";
                return ResolveFieldValueWithCycleGuard(found.Value.Group.TargetFieldKey, doc, visitedLogicGroups);
            }

            if (fieldKey.StartsWith("UDEF:"))
                return _propReader.ReadUserDefinedProperty(doc, fieldKey["UDEF:".Length..]);

            if (fieldKey.StartsWith("IPROP|"))
            {
                var parts = fieldKey.Split('|');
                // 3-part: IPROP|<setName>|<propName>  (internal catalog key format)
                // Try all language candidates for the set name; fall back to searching all sets by property
                // name so stored German keys resolve in English Inventor and vice-versa.
                if (parts.Length >= 3)
                {
                    string val = _propReader.ReadStandardProperty(doc, GetSetNameCandidates(parts[1]), new[] { parts[2] });
                    return val != "n/a" ? val : _propReader.ReadStandardPropertyByName(doc, parts[2]);
                }
                // 2-part: IPROP|<propName>  (short-form used in formula references and capability files)
                if (parts.Length == 2)
                    return _propReader.ReadStandardPropertyByName(doc, parts[1]);
                return "";
            }

            if (fieldKey.StartsWith("DOC:"))
                return _propReader.ReadDocumentValue(doc, fieldKey["DOC:".Length..]);

            if (fieldKey.StartsWith("PARAM:User:") || fieldKey.StartsWith("PARAM:Model:"))
            {
                int second = fieldKey.IndexOf(':', fieldKey.IndexOf(':') + 1);
                if (second < 0) return "n/a";
                return _propReader.ReadParameterExpression(doc, fieldKey[(second + 1)..]);
            }

            return "n/a";
        }
    }
}
