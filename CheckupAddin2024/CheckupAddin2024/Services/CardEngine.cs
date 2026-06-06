using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>One distinct tab from the catalog's TAB column, for use in the CatalogPickerWindow tab strip.</summary>
    public sealed class CatalogTabEntry
    {
        public string TabId   { get; }
        public string Label   { get; }
        public string SortKey { get; }
        public CatalogTabEntry(string tabId, string label, string sortKey)
        { TabId = tabId ?? ""; Label = label ?? ""; SortKey = sortKey ?? ""; }
    }

    /// <summary>
    /// Stateless helpers that interpret CapabilityCard lists against a CatalogData.
    /// </summary>
    public static class CardEngine
    {
        // ── Card type identifiers ──────────────────────────────────────────────
        public const string CardTypeDropdown = "Dropdown";
        public const string CardTypeSync     = "Sync";
        public const string CardTypeLink     = "Link";
        /// <summary>Button card: adds a picker button in the Checkup row that opens a CatalogPickerWindow.</summary>
        public const string CardTypeButton   = "Button";
        /// <summary>Search card: live-filter text input; shows a filtered dropdown as the user types.</summary>
        public const string CardTypeSearch   = "Search";
        /// <summary>Basic Logic card: evaluates a text-based formula and writes the result to a target field.</summary>
        public const string CardTypeBasicLogic = "BasicLogic";

        /// <summary>PrefixSuffix card: prepends and/or appends fixed text to the raw field value before display.
        /// In Remove mode the prefix/suffix is stripped instead of added.</summary>
        public const string CardTypePrefixSuffix = "PrefixSuffix";

        /// <summary>PrefixSuffix card: text to prepend to (or strip from the start of) the field value.</summary>
        public const string ParamPrefix = "Prefix";

        /// <summary>PrefixSuffix card: text to append to (or strip from the end of) the field value.</summary>
        public const string ParamSuffix = "Suffix";

        /// <summary>PrefixSuffix card: when "true" the card strips prefix/suffix from stored values instead of adding them.</summary>
        public const string ParamIsRemoveMode = "IsRemoveMode";

        /// <summary>Sort card: sorts a multi-token field value by the catalog's SRT column(s) on Apply.</summary>
        public const string CardTypeSort = "Sort";

        /// <summary>Sort card: separator used to split and rejoin the field value into tokens (default "-").</summary>
        public const string ParamSortTokenSeparator = "SortTokenSeparator";

        /// <summary>Sort card: catalog role badge each token is looked up as (default "PRI").</summary>
        public const string ParamSortLookupRole = "SortLookupRole";

        /// <summary>Sort card: when "true" reverses the sort order (descending instead of ascending).</summary>
        public const string ParamSortInvert = "SortInvert";

        // ── Shared param keys ─────────────────────────────────────────────────
        /// <summary>Sync card: companion field key to write to.</summary>
        public const string ParamCompanionFieldKey = "CompanionFieldKey";

        /// <summary>Sync card: which catalog role value to write to the companion field (default "SEC").</summary>
        public const string ParamCompanionRole = "CompanionRole";

        /// <summary>Link card: field key of the companion row that is locked below this Logic row.</summary>
        public const string ParamLinkPartnerFieldKey = "PartnerFieldKey";

        /// <summary>Dropdown card: which catalog role appears as the secondary text column (default "SEC"; empty = hidden).</summary>
        public const string ParamSecRole = "SecRole";

        /// <summary>Dropdown card: which catalog role appears as the item tooltip (default "AUX"; empty = hidden).</summary>
        public const string ParamTooltipRole = "TooltipRole";

        /// <summary>Search card: comma-separated list of catalog role badges to match against when filtering
        /// (e.g. "PRI,SEC"). Empty = match PRI and SEC by default.</summary>
        public const string ParamSearchRoles = "SearchRoles";

        /// <summary>Formula card: the formula expression string.</summary>
        public const string ParamFormula = "Formula";

        /// <summary>Formula card: target field key override. Empty = use the group's TargetFieldKey.</summary>
        public const string ParamFormulaTargetFieldKey = "FormulaTargetFieldKey";

        /// <summary>Multi-Pick card: adds a picker button that allows selecting multiple catalog items.
        /// The selected PRI values are joined with <see cref="ParamPrimaryTokenSeparator"/> and written
        /// to the group's TargetFieldKey. Optionally writes companion values to <see cref="ParamCompanionFieldKey"/>.</summary>
        public const string CardTypeMultiPick = "MultiPick";

        /// <summary>Multi-Pick card: character(s) used to join selected PRI values into the target field (default "-").</summary>
        public const string ParamPrimaryTokenSeparator = "PrimaryTokenSeparator";

        /// <summary>Multi-Pick card: character(s) used to join companion role values into the companion field (default ", ").</summary>
        public const string ParamCompanionTokenSeparator = "CompanionTokenSeparator";

        /// <summary>Pair Transform card: splits the current field value into tokens, looks up each token by
        /// <see cref="ParamLookupRole"/>, outputs the <see cref="ParamOutputRole"/> value, and writes the
        /// joined result to <see cref="ParamCompanionFieldKey"/>. Fires on inline-edit Apply (not picker).</summary>
        public const string CardTypePairTransform = "PairTransform";

        /// <summary>Pair Transform card: separator used to split the source value into tokens (default "-").</summary>
        public const string ParamSourceTokenSeparator = "SourceTokenSeparator";

        /// <summary>Pair Transform card: catalog role badge each source token is looked up as (default "PRI").</summary>
        public const string ParamLookupRole = "LookupRole";

        /// <summary>Pair Transform card: catalog role badge whose value is output for each matched token (default "SEC").</summary>
        public const string ParamOutputRole = "OutputRole";

        /// <summary>Pair Transform card: separator used to join the output tokens (default ", ").</summary>
        public const string ParamOutputTokenSeparator = "OutputTokenSeparator";

        // ── Display column params (Dropdown / Button cards) ───────────────────
        /// <summary>Maximum number of additional display-only columns configurable per Dropdown or Button card.</summary>
        public const int MaxDisplayColumns = 7;

        /// <summary>Returns the Card.Params key for display column slot <paramref name="n"/> (0-based).</summary>
        public static string DisplayRoleKey(int n) => string.Format("Display_{0}_Role", n);

        // ── Role badge ↔ enum mapping ──────────────────────────────────────────

        /// <summary>Returns the two-or-three-letter badge abbreviation for a catalog column role.</summary>
        public static string RoleBadge(ColumnRole role)
        {
            switch (role)
            {
                case ColumnRole.PrimaryDisplay:   return "PRI";
                case ColumnRole.SecondaryDisplay: return "SEC";
                case ColumnRole.TabId:            return "TAB";
                case ColumnRole.GroupId:          return "GRP";
                case ColumnRole.SortKey:          return "SRT";
                case ColumnRole.GroupSortKey:     return "GST";
                case ColumnRole.TabSortKey:       return "TST";
                case ColumnRole.Auxiliary:        return "AUX";
                default:                          return "";
            }
        }

        private static ColumnRole RoleFromBadge(string badge)
        {
            if (badge == null) return ColumnRole.None;
            switch (badge.ToUpperInvariant())
            {
                case "PRI": return ColumnRole.PrimaryDisplay;
                case "SEC": return ColumnRole.SecondaryDisplay;
                case "TAB": return ColumnRole.TabId;
                case "GRP": return ColumnRole.GroupId;
                case "SRT": return ColumnRole.SortKey;
                case "GST": return ColumnRole.GroupSortKey;
                case "TST": return ColumnRole.TabSortKey;
                case "AUX": return ColumnRole.Auxiliary;
                default:    return ColumnRole.None;
            }
        }

        // ── Basic helpers ─────────────────────────────────────────────────────

        public static bool HasCard(CardGroup group, string cardType)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == cardType);

        /// <summary>Returns true when the group has at least one enabled Basic Logic card.</summary>
        public static bool HasBasicLogicCards(CardGroup group)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == CardTypeBasicLogic);

        /// <summary>
        /// Returns true when any enabled Formula card in the group references {INPUT},
        /// meaning the formula requires user-typed input before it can be evaluated.
        /// </summary>
        public static bool HasInputReference(CardGroup group)
        {
            if (group == null) return false;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeBasicLogic) continue;
                string formula;
                card.Params.TryGetValue(ParamFormula, out formula);
                if (formula != null && formula.IndexOf("{INPUT}", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Evaluates all enabled Basic Logic cards in <paramref name="group"/> against <paramref name="ctx"/>
        /// and yields the resulting (FieldKey, Value) write pairs.
        /// Cards without an explicit FormulaTargetFieldKey fall back to <paramref name="defaultTargetFieldKey"/>.
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value)> EvaluateBasicLogicCards(
            CardGroup group, FormulaContext ctx, string defaultTargetFieldKey)
        {
            if (group == null) yield break;
            if (ctx == null) ctx = FormulaContext.Empty;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeBasicLogic) continue;
                string formula;
                card.Params.TryGetValue(ParamFormula, out formula);
                if (string.IsNullOrWhiteSpace(formula)) continue;
                string targetKey;
                card.Params.TryGetValue(ParamFormulaTargetFieldKey, out targetKey);
                if (string.IsNullOrEmpty(targetKey)) targetKey = defaultTargetFieldKey;
                if (string.IsNullOrEmpty(targetKey)) continue;
                string value = FormulaEngine.Evaluate(formula, ctx);
                yield return (targetKey, value);
            }
        }

        // ── PrefixSuffix card ─────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled PrefixSuffix card.</summary>
        public static bool HasPrefixSuffixCard(CardGroup group)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == CardTypePrefixSuffix);

        public struct PrefixSuffixConfig
        {
            public string Prefix;
            public string Suffix;
            public bool   IsRemoveMode;
        }

        /// <summary>Reads PrefixSuffix params from the first enabled PrefixSuffix card in the group.</summary>
        public static PrefixSuffixConfig GetPrefixSuffixConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypePrefixSuffix) continue;
                    string prefix, suffix, rm;
                    card.Params.TryGetValue(ParamPrefix,       out prefix);
                    card.Params.TryGetValue(ParamSuffix,       out suffix);
                    card.Params.TryGetValue(ParamIsRemoveMode, out rm);
                    return new PrefixSuffixConfig
                    {
                        Prefix       = prefix ?? "",
                        Suffix       = suffix ?? "",
                        IsRemoveMode = string.Equals(rm, "true", StringComparison.OrdinalIgnoreCase),
                    };
                }
            }
            return new PrefixSuffixConfig();
        }

        /// <summary>Applies (or strips) prefix/suffix from <paramref name="value"/>.</summary>
        public static string ApplyPrefixSuffix(string value, string prefix, string suffix, bool isRemoveMode)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            if (isRemoveMode)
            {
                if (!string.IsNullOrEmpty(prefix) && value.StartsWith(prefix, StringComparison.Ordinal))
                    value = value.Substring(prefix.Length);
                if (!string.IsNullOrEmpty(suffix) && value.EndsWith(suffix, StringComparison.Ordinal))
                    value = value.Substring(0, value.Length - suffix.Length);
                return value;
            }
            return (prefix ?? "") + value + (suffix ?? "");
        }

        // ── Sort card ─────────────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Sort card.</summary>
        public static bool HasSortCard(CardGroup group)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == CardTypeSort);

        public struct SortConfig
        {
            public string TokenSep;
            public string LookupRole;
            public bool   IsInvert;
        }

        /// <summary>Reads Sort card parameters from the first enabled Sort card in the group.</summary>
        public static SortConfig GetSortConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypeSort) continue;
                    string sep, lr, inv;
                    card.Params.TryGetValue(ParamSortTokenSeparator, out sep);
                    card.Params.TryGetValue(ParamSortLookupRole,     out lr);
                    card.Params.TryGetValue(ParamSortInvert,         out inv);
                    return new SortConfig
                    {
                        TokenSep   = string.IsNullOrEmpty(sep) ? "-"   : sep,
                        LookupRole = string.IsNullOrEmpty(lr)  ? "PRI" : lr,
                        IsInvert   = string.Equals(inv, "true", StringComparison.OrdinalIgnoreCase),
                    };
                }
            }
            return new SortConfig { TokenSep = "-", LookupRole = "PRI" };
        }

        /// <summary>
        /// Splits <paramref name="sourceValue"/> by <paramref name="tokenSep"/>, looks up each token
        /// by <paramref name="lookupRole"/> in <paramref name="catalog"/>, sorts by SRT column(s),
        /// and rejoins. Unknown tokens are appended after known ones.
        /// Returns the original value when catalog is null or has no SRT columns.
        /// </summary>
        public static string BuildSortedValue(
            string sourceValue, CatalogData catalog, string lookupRole, string tokenSep, bool isInvert)
        {
            if (catalog == null || string.IsNullOrEmpty(sourceValue)) return sourceValue ?? "";
            string sep = tokenSep ?? "-";
            string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lookupRole) ? "PRI" : lookupRole);
            if (lKey == null) return sourceValue;

            var srtKeys = catalog.Columns
                .Where(c => c.Role == ColumnRole.SortKey)
                .OrderBy(c => c.RoleIndex)
                .ThenBy(c => c.Key)
                .Select(c => c.Key)
                .ToList();
            if (srtKeys.Count == 0) return sourceValue;

            string[] tokens = sourceValue.Split(new[] { sep }, StringSplitOptions.None);
            var known   = new List<Tuple<List<string>, int, string>>();
            var unknown = new List<string>();

            for (int ti = 0; ti < tokens.Length; ti++)
            {
                string token = tokens[ti];
                bool found = false;
                for (int ei = 0; ei < catalog.Entries.Count; ei++)
                {
                    var entry = catalog.Entries[ei];
                    string lv;
                    if (!entry.Values.TryGetValue(lKey, out lv) ||
                        !string.Equals(lv, token, StringComparison.OrdinalIgnoreCase)) continue;
                    var srt = new List<string>();
                    foreach (var k in srtKeys)
                    {
                        string sv;
                        srt.Add(entry.Values.TryGetValue(k, out sv) ? sv ?? "" : "");
                    }
                    known.Add(Tuple.Create(srt, ei, token));
                    found = true;
                    break;
                }
                if (!found) unknown.Add(token);
            }

            known.Sort((x, y) =>
            {
                for (int ci = 0; ci < Math.Max(x.Item1.Count, y.Item1.Count); ci++)
                {
                    string xv = ci < x.Item1.Count ? x.Item1[ci] : "";
                    string yv = ci < y.Item1.Count ? y.Item1[ci] : "";
                    int cmp = string.Compare(xv, yv, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return isInvert ? -cmp : cmp;
                }
                int idxCmp = x.Item2.CompareTo(y.Item2);
                return isInvert ? -idxCmp : idxCmp;
            });

            var parts = new List<string>();
            foreach (var k in known) parts.Add(k.Item3);
            parts.AddRange(unknown);
            return string.Join(sep, parts);
        }

        /// <summary>
        /// Resolves a column name-or-role-badge to the internal column key of <paramref name="catalog"/>.
        /// Matches by column label first (case-insensitive), then by role badge.
        /// Returns null when not found.
        /// </summary>
        public static string ResolveColumnKey(CatalogData catalog, string nameOrBadge)
        {
            if (catalog == null || string.IsNullOrEmpty(nameOrBadge)) return null;
            var byLabel = catalog.Columns.FirstOrDefault(c =>
                string.Equals(c.Label, nameOrBadge, StringComparison.OrdinalIgnoreCase));
            if (byLabel != null) return byLabel.Key;
            ColumnRole role;
            int index;
            ParseRoleBadge(nameOrBadge, out role, out index);
            if (role != ColumnRole.None)
            {
                var byRole = catalog.Columns.FirstOrDefault(c => c.Role == role && c.RoleIndex == index)
                          ?? catalog.Columns.FirstOrDefault(c => c.Role == role);
                if (byRole != null) return byRole.Key;
            }
            return null;
        }

        /// <summary>
        /// Searches <paramref name="catalog"/> for an entry where <paramref name="searchColNameOrBadge"/>
        /// equals <paramref name="key"/> (case-insensitive) and returns the corresponding
        /// <paramref name="returnColNameOrBadge"/> value. Returns null when not found.
        /// </summary>
        public static string LookupByColumn(CatalogData catalog, string key,
            string searchColNameOrBadge, string returnColNameOrBadge)
        {
            if (catalog == null) return null;
            string searchKey = ResolveColumnKey(catalog, searchColNameOrBadge);
            string returnKey = ResolveColumnKey(catalog, returnColNameOrBadge);
            if (searchKey == null || returnKey == null) return null;
            foreach (var entry in catalog.Entries)
            {
                string sv;
                if (entry.Values.TryGetValue(searchKey, out sv) &&
                    string.Equals(sv, key, StringComparison.OrdinalIgnoreCase))
                {
                    string rv;
                    entry.Values.TryGetValue(returnKey, out rv);
                    return rv ?? "";
                }
            }
            return null;
        }

        /// <summary>Returns the partner field key from the first enabled Link card in <paramref name="group"/>,
        /// or null if no Link card is present.</summary>
        public static string GetLinkPartnerFieldKey(CardGroup group)
        {
            if (group == null) return null;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeLink) continue;
                string key;
                if (card.Params.TryGetValue(ParamLinkPartnerFieldKey, out key) && !string.IsNullOrEmpty(key))
                    return key;
            }
            return null;
        }

        /// <summary>
        /// Returns the partner field keys for ALL enabled Link cards in <paramref name="group"/>,
        /// in card order. Empty when no Link cards are present.
        /// </summary>
        public static IReadOnlyList<string> GetAllLinkPartnerFieldKeys(CardGroup group)
        {
            if (group == null) return Array.Empty<string>();
            var keys = new List<string>();
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeLink) continue;
                string key;
                if (card.Params.TryGetValue(ParamLinkPartnerFieldKey, out key) && !string.IsNullOrEmpty(key))
                    keys.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// Returns the CatalogId from the first enabled Dropdown, Button, Search, MultiPick,
        /// or PairTransform card in <paramref name="group"/>, or null when no such card is configured.
        /// </summary>
        public static string GetPrimaryCatalogId(CardGroup group)
        {
            if (group == null) return null;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled) continue;
                if (card.Type == CardTypeDropdown    || card.Type == CardTypeButton   ||
                    card.Type == CardTypeSearch      || card.Type == CardTypeMultiPick ||
                    card.Type == CardTypePairTransform || card.Type == CardTypeSort)
                    return string.IsNullOrEmpty(card.CatalogId) ? null : card.CatalogId;
            }
            return null;
        }

        /// <summary>
        /// Looks up the value of <paramref name="roleBadge"/> column for the entry whose PRI value
        /// matches <paramref name="priValue"/>. Supports indexed badges such as "AUX2". Returns null when not found.
        /// </summary>
        public static string LookupRoleValue(CatalogData catalog, string priValue, string roleBadge)
        {
            if (catalog == null || string.IsNullOrEmpty(priValue) || string.IsNullOrEmpty(roleBadge))
                return null;
            string priKey  = GetColumnKey(catalog, ColumnRole.PrimaryDisplay, 1);
            ColumnRole role;
            int roleIdx;
            ParseRoleBadge(roleBadge, out role, out roleIdx);
            string roleKey = role != ColumnRole.None ? GetColumnKey(catalog, role, roleIdx) : null;
            if (priKey == null || roleKey == null) return null;

            foreach (var entry in catalog.Entries)
            {
                string val;
                entry.Values.TryGetValue(priKey, out val);
                if (string.Equals(val, priValue, StringComparison.OrdinalIgnoreCase))
                {
                    string result;
                    entry.Values.TryGetValue(roleKey, out result);
                    return result ?? "";
                }
            }
            return null;
        }

        /// <summary>Convenience overload — looks up the SEC column value for a PRI value.</summary>
        public static string LookupSecValue(CatalogData catalog, string priValue)
            => LookupRoleValue(catalog, priValue, "SEC");

        // ── Dropdown card ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the inline dropdown item list for a Dropdown card.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetDropdownItems(
            CatalogData catalog,
            string secRoleBadge     = "SEC",
            string tooltipRoleBadge = "AUX",
            IReadOnlyList<string> extraDisplayRoles = null,
            IReadOnlyList<string> searchRoles = null)
        {
            if (catalog == null) return Array.Empty<CatalogDropdownItem>();
            string priKey = GetColumnKey(catalog, ColumnRole.PrimaryDisplay, 1);
            if (priKey == null) return Array.Empty<CatalogDropdownItem>();

            string secKey     = GetRoleKey(catalog, secRoleBadge);
            string tooltipKey = GetRoleKey(catalog, tooltipRoleBadge);
            string grpKey     = GetColumnKey(catalog, ColumnRole.GroupId,      1);
            string tstKey     = GetColumnKey(catalog, ColumnRole.TabSortKey,   1);
            string tabKey     = GetColumnKey(catalog, ColumnRole.TabId,        1);
            string gstKey     = GetColumnKey(catalog, ColumnRole.GroupSortKey, 1);
            string srtKey     = GetColumnKey(catalog, ColumnRole.SortKey,      1);

            string[] extraKeys = null;
            if (extraDisplayRoles != null && extraDisplayRoles.Count > 0)
            {
                extraKeys = new string[extraDisplayRoles.Count];
                for (int i = 0; i < extraDisplayRoles.Count; i++)
                    extraKeys[i] = GetRoleKey(catalog, extraDisplayRoles[i]);
            }

            string[] searchKeys = null;
            if (searchRoles != null && searchRoles.Count > 0)
            {
                searchKeys = new string[searchRoles.Count];
                for (int i = 0; i < searchRoles.Count; i++)
                    searchKeys[i] = GetRoleKey(catalog, searchRoles[i]);
            }

            bool hasSort = tstKey != null || tabKey != null || gstKey != null || srtKey != null;

            var staged = new List<StagedItem>(catalog.Entries.Count);
            int orig = 0;
            foreach (var entry in catalog.Entries)
            {
                string pri;
                if (!entry.Values.TryGetValue(priKey, out pri) || string.IsNullOrEmpty(pri))
                { orig++; continue; }

                string sec = null, tip = null, grp = null, tab = null, tst = null, gst = null, srt = null;
                if (secKey     != null) entry.Values.TryGetValue(secKey,     out sec);
                if (tooltipKey != null) entry.Values.TryGetValue(tooltipKey, out tip);
                if (grpKey     != null) entry.Values.TryGetValue(grpKey,     out grp);
                if (tabKey     != null) entry.Values.TryGetValue(tabKey,     out tab);
                if (tstKey     != null) entry.Values.TryGetValue(tstKey,     out tst);
                else                                                                tst = tab;
                if (gstKey     != null) entry.Values.TryGetValue(gstKey,     out gst);
                if (srtKey     != null) entry.Values.TryGetValue(srtKey,     out srt);

                List<string> extras = null;
                if (extraKeys != null)
                {
                    extras = new List<string>(extraKeys.Length);
                    foreach (var ek in extraKeys)
                    {
                        string ev = null;
                        if (ek != null) entry.Values.TryGetValue(ek, out ev);
                        extras.Add(ev ?? "");
                    }
                }

                List<string> searchVals = null;
                if (searchKeys != null)
                {
                    searchVals = new List<string>(searchKeys.Length);
                    foreach (var sk in searchKeys)
                    {
                        string sv = null;
                        if (sk != null) entry.Values.TryGetValue(sk, out sv);
                        searchVals.Add(sv ?? "");
                    }
                }

                staged.Add(new StagedItem(new CatalogDropdownItem(pri, sec, tip, grp, tab, extras, searchVals), tst ?? "", gst ?? "", srt ?? "", orig));
                orig++;
            }

            if (hasSort)
            {
                staged.Sort((a, b) =>
                {
                    int c = CompareValues(a.Tst, b.Tst);
                    if (c != 0) return c;
                    c = CompareValues(a.Gst, b.Gst);
                    if (c != 0) return c;
                    c = CompareValues(a.Srt, b.Srt);
                    if (c != 0) return c;
                    return a.Orig.CompareTo(b.Orig);
                });
            }

            var result = new List<CatalogDropdownItem>(staged.Count);
            foreach (var s in staged) result.Add(s.Item);
            return result;
        }

        private struct StagedItem
        {
            public CatalogDropdownItem Item;
            public string Tst;
            public string Gst;
            public string Srt;
            public int    Orig;
            public StagedItem(CatalogDropdownItem item, string tst, string gst, string srt, int orig)
            { Item = item; Tst = tst; Gst = gst; Srt = srt; Orig = orig; }
        }

        private static int CompareValues(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return 1;
            if (string.IsNullOrEmpty(b)) return -1;
            double da, db;
            if (double.TryParse(a, System.Globalization.NumberStyles.Any,
                    CultureInfo.InvariantCulture, out da) &&
                double.TryParse(b, System.Globalization.NumberStyles.Any,
                    CultureInfo.InvariantCulture, out db))
                return da.CompareTo(db);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads the SecRole / TooltipRole / Display_N_Role params from the first enabled Dropdown card in
        /// <paramref name="group"/> and calls <see cref="GetDropdownItems"/>.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetDropdownItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeDropdown) continue;
                string sec, tip;
                card.Params.TryGetValue(ParamSecRole,     out sec);
                card.Params.TryGetValue(ParamTooltipRole, out tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItems(catalog);
        }

        /// <summary>
        /// Returns the ordered list of distinct tabs from the catalog's TAB column,
        /// sorted by TST (tab sort key). Empty when the catalog has no TAB column.
        /// </summary>
        public static IReadOnlyList<CatalogTabEntry> GetPickerTabs(CatalogData catalog)
        {
            if (catalog == null) return Array.Empty<CatalogTabEntry>();
            string tabKey = GetColumnKey(catalog, ColumnRole.TabId,      1);
            if (tabKey == null) return Array.Empty<CatalogTabEntry>();
            string tstKey = GetColumnKey(catalog, ColumnRole.TabSortKey, 1);

            var seen  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog.Entries)
            {
                string tab;
                if (!entry.Values.TryGetValue(tabKey, out tab) || string.IsNullOrEmpty(tab)) continue;
                if (seen.ContainsKey(tab)) continue;
                seen[tab] = tab;
                string tv;
                string tst = tstKey != null && entry.Values.TryGetValue(tstKey, out tv) ? tv : tab;
                sorts[tab] = tst;
            }

            return seen.Keys
                .OrderBy(t => sorts[t], StringComparer.OrdinalIgnoreCase)
                .Select(t => new CatalogTabEntry(t, t, sorts[t]))
                .ToList();
        }

        /// <summary>
        /// Like <see cref="GetDropdownItemsForCard"/>, but reads params from the first enabled Button card.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetButtonItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeButton) continue;
                string sec, tip;
                card.Params.TryGetValue(ParamSecRole,     out sec);
                card.Params.TryGetValue(ParamTooltipRole, out tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItemsForCard(group, catalog);
        }

        /// <summary>
        /// Builds the item list for a Search card. Reads SecRole/TooltipRole/Display_N_Role/SearchRoles
        /// from the first enabled Search card.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetSearchItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeSearch) continue;
                string sec, tip, searchRolesStr;
                card.Params.TryGetValue(ParamSecRole,     out sec);
                card.Params.TryGetValue(ParamTooltipRole, out tip);
                card.Params.TryGetValue(ParamSearchRoles, out searchRolesStr);
                IReadOnlyList<string> searchRoles = null;
                if (!string.IsNullOrEmpty(searchRolesStr))
                {
                    var parts = searchRolesStr.Split(',');
                    var list  = new List<string>(parts.Length);
                    foreach (var p in parts) { string t = p.Trim(); if (!string.IsNullOrEmpty(t)) list.Add(t); }
                    if (list.Count > 0) searchRoles = list;
                }
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card),
                    searchRoles);
            }
            return GetDropdownItems(catalog);
        }

        // ── Multi-Pick card ───────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Multi-Pick card.</summary>
        public static bool HasMultiPickCard(CardGroup group)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == CardTypeMultiPick);

        /// <summary>Builds the catalog item list for a Multi-Pick card.</summary>
        public static IReadOnlyList<CatalogDropdownItem> GetMultiPickItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeMultiPick) continue;
                string sec, tip;
                card.Params.TryGetValue(ParamSecRole,     out sec);
                card.Params.TryGetValue(ParamTooltipRole, out tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItems(catalog);
        }

        /// <summary>
        /// Reads the Multi-Pick card configuration from the first enabled Multi-Pick card in
        /// <paramref name="group"/>. Returns defaults when no card is found.
        /// </summary>
        public static MultiPickConfig GetMultiPickConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypeMultiPick) continue;
                    string priSep, compField, compRole, compSep;
                    card.Params.TryGetValue(ParamPrimaryTokenSeparator,   out priSep);
                    card.Params.TryGetValue(ParamCompanionFieldKey,        out compField);
                    card.Params.TryGetValue(ParamCompanionRole,            out compRole);
                    card.Params.TryGetValue(ParamCompanionTokenSeparator,  out compSep);
                    return new MultiPickConfig(
                        string.IsNullOrEmpty(priSep)   ? "-"   : priSep,
                        compField ?? "",
                        string.IsNullOrEmpty(compRole) ? "SEC" : compRole,
                        string.IsNullOrEmpty(compSep)  ? ", "  : compSep);
                }
            }
            return new MultiPickConfig("-", "", "SEC", ", ");
        }

        /// <summary>
        /// For each PRI value in <paramref name="priValues"/>, looks up the <paramref name="companionRole"/>
        /// column value in <paramref name="catalog"/> and joins the results with <paramref name="companionSep"/>.
        /// Items with an empty lookup result are skipped.
        /// </summary>
        public static string BuildMultiPickCompanionValue(
            IEnumerable<string> priValues, CatalogData catalog, string companionRole, string companionSep)
        {
            if (priValues == null || catalog == null) return "";
            var parts = new List<string>();
            foreach (var pri in priValues)
            {
                if (string.IsNullOrEmpty(pri)) continue;
                string val = LookupRoleValue(catalog, pri, companionRole);
                if (!string.IsNullOrEmpty(val)) parts.Add(val);
            }
            return string.Join(companionSep ?? "", parts);
        }

        // ── Pair Transform card ───────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Pair Transform card.</summary>
        public static bool HasPairTransformCard(CardGroup group)
            => group != null && group.Cards.Any(c => c.Enabled && c.Type == CardTypePairTransform);

        /// <summary>
        /// Reads Pair Transform card parameters from the first enabled PairTransform card in
        /// <paramref name="group"/>. Returns defaults when no card is found.
        /// </summary>
        public static PairTransformConfig GetPairTransformConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypePairTransform) continue;
                    string srcSep, lookupRole, outputRole, outSep, compField;
                    card.Params.TryGetValue(ParamSourceTokenSeparator, out srcSep);
                    card.Params.TryGetValue(ParamLookupRole,           out lookupRole);
                    card.Params.TryGetValue(ParamOutputRole,           out outputRole);
                    card.Params.TryGetValue(ParamOutputTokenSeparator, out outSep);
                    card.Params.TryGetValue(ParamCompanionFieldKey,    out compField);
                    return new PairTransformConfig(
                        string.IsNullOrEmpty(srcSep)     ? "-"   : srcSep,
                        string.IsNullOrEmpty(lookupRole) ? "PRI" : lookupRole,
                        string.IsNullOrEmpty(outputRole) ? "SEC" : outputRole,
                        string.IsNullOrEmpty(outSep)     ? ", "  : outSep,
                        compField ?? "");
                }
            }
            return new PairTransformConfig("-", "PRI", "SEC", ", ", "");
        }

        /// <summary>
        /// Splits <paramref name="sourceValue"/> by <paramref name="sourceSep"/>, looks up each token
        /// in <paramref name="catalog"/> where the <paramref name="lookupRole"/> column equals the token,
        /// collects the <paramref name="outputRole"/> column value for each match, and joins them with
        /// <paramref name="outputSep"/>. Tokens with no catalog match are omitted from the result.
        /// </summary>
        public static string BuildPairTransformValue(
            string sourceValue, CatalogData catalog,
            string sourceSep, string lookupRole, string outputRole, string outputSep)
        {
            if (catalog == null || string.IsNullOrEmpty(sourceValue)) return "";

            string[] tokens = sourceValue.Split(
                new[] { sourceSep ?? "-" }, StringSplitOptions.RemoveEmptyEntries);

            ColumnRole lRole, oRole;
            int lIdx, oIdx;
            ParseRoleBadge(lookupRole ?? "PRI", out lRole, out lIdx);
            ParseRoleBadge(outputRole ?? "SEC", out oRole, out oIdx);
            string lKey = lRole != ColumnRole.None ? GetColumnKey(catalog, lRole, lIdx) : null;
            string oKey = oRole != ColumnRole.None ? GetColumnKey(catalog, oRole, oIdx) : null;
            DiagLogger.Log("pairtransform", string.Format("BuildPairTransform: lRole={0} lKey={1} oRole={2} oKey={3}", lRole, lKey ?? "(null)", oRole, oKey ?? "(null)"));
            if (lKey == null || oKey == null) return "";

            var parts = new List<string>();
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;
                foreach (var entry in catalog.Entries)
                {
                    string lVal;
                    entry.Values.TryGetValue(lKey, out lVal);
                    if (!string.Equals(lVal, token, StringComparison.OrdinalIgnoreCase)) continue;
                    string oVal;
                    entry.Values.TryGetValue(oKey, out oVal);
                    if (!string.IsNullOrEmpty(oVal)) parts.Add(oVal);
                    break;
                }
            }
            return string.Join(outputSep ?? "", parts);
        }

        // ── Sync card ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all (fieldKey, value) pairs that enabled Sync cards want written when the
        /// primary field changes to <paramref name="priValue"/>.
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value)> GetSyncWrites(
            CardGroup group, CatalogData catalog, string priValue)
        {
            if (group == null || catalog == null) yield break;

            var roleValueCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeSync) continue;
                string compKey;
                if (!card.Params.TryGetValue(ParamCompanionFieldKey, out compKey)) continue;
                if (string.IsNullOrEmpty(compKey)) continue;

                string roleBadge;
                card.Params.TryGetValue(ParamCompanionRole, out roleBadge);
                if (string.IsNullOrEmpty(roleBadge)) roleBadge = "SEC";

                string roleValue;
                if (!roleValueCache.TryGetValue(roleBadge, out roleValue))
                {
                    roleValue = LookupRoleValue(catalog, priValue, roleBadge);
                    roleValueCache[roleBadge] = roleValue;
                }
                if (roleValue != null)
                    yield return (compKey, roleValue);
            }
        }

        // ── Role discovery (for card editor) ──────────────────────────────────

        /// <summary>
        /// Returns the distinct role badge strings for all roles in <paramref name="catalog"/>,
        /// with index suffixes when a role type appears more than once.
        /// </summary>
        public static IReadOnlyList<string> GetCatalogRoles(CatalogData catalog)
        {
            if (catalog == null) return Array.Empty<string>();

            var counts = new Dictionary<ColumnRole, int>();
            foreach (var col in catalog.Columns)
                if (col.Role != ColumnRole.None)
                {
                    int c;
                    counts[col.Role] = counts.TryGetValue(col.Role, out c) ? c + 1 : 1;
                }

            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var col in catalog.Columns)
            {
                if (col.Role == ColumnRole.None) continue;
                string baseBadge = RoleBadge(col.Role);
                if (string.IsNullOrEmpty(baseBadge)) continue;
                int count;
                string badge = counts.TryGetValue(col.Role, out count) && count > 1
                    ? baseBadge + col.RoleIndex.ToString()
                    : baseBadge;
                if (seen.Add(badge))
                    order.Add(badge);
            }
            return order;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static IReadOnlyList<string> ReadDisplayRoles(CapabilityCard card)
        {
            var roles = new List<string>();
            for (int n = 0; n < MaxDisplayColumns; n++)
            {
                string role;
                if (!card.Params.TryGetValue(DisplayRoleKey(n), out role) || string.IsNullOrEmpty(role))
                    break;
                roles.Add(role);
            }
            return roles.Count > 0 ? (IReadOnlyList<string>)roles : Array.Empty<string>();
        }

        private static void ParseRoleBadge(string badge, out ColumnRole role, out int index)
        {
            index = 1;
            if (string.IsNullOrEmpty(badge)) { role = ColumnRole.None; return; }
            int i = badge.Length;
            while (i > 0 && char.IsDigit(badge[i - 1])) i--;
            string basePart = badge.Substring(0, i);
            int parsed;
            if (i < badge.Length && int.TryParse(badge.Substring(i), out parsed) && parsed >= 1)
                index = parsed;
            role = RoleFromBadge(basePart);
        }

        private static string GetRoleKey(CatalogData catalog, string roleBadge)
        {
            if (string.IsNullOrEmpty(roleBadge)) return null;
            ColumnRole role;
            int index;
            ParseRoleBadge(roleBadge, out role, out index);
            return role != ColumnRole.None ? GetColumnKey(catalog, role, index) : null;
        }

        private static string GetColumnKey(CatalogData catalog, ColumnRole role, int roleIndex)
        {
            if (catalog == null) return null;
            foreach (var col in catalog.Columns)
                if (col.Role == role && col.RoleIndex == roleIndex) return col.Key;
            return null;
        }

        private static string GetColumnLabel(CatalogData catalog, ColumnRole role, int roleIndex)
        {
            if (catalog == null) return null;
            foreach (var col in catalog.Columns)
                if (col.Role == role && col.RoleIndex == roleIndex)
                    return !string.IsNullOrEmpty(col.Label) ? col.Label : col.Key;
            return null;
        }

        private static string GetRoleLabel(CatalogData catalog, string roleBadge)
        {
            if (string.IsNullOrEmpty(roleBadge)) return null;
            ColumnRole role;
            int index;
            ParseRoleBadge(roleBadge, out role, out index);
            return role != ColumnRole.None ? GetColumnLabel(catalog, role, index) : null;
        }

        // ── Multi-column Logic Dropdown popup (U1) ────────────────────────────

        /// <summary>
        /// Returns the column specs (label + index into <see cref="CatalogDropdownItem.AllDisplayValues"/>)
        /// for the multi-column Logic Dropdown popup of <paramref name="group"/>.
        /// Order: PRI (always present) → SEC → AUX → Display_N roles (each only if its role resolves to a real catalog column).
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<LogicDropdownColumnSpec> GetLogicDropdownColumnSpecs(
            CardGroup group, CatalogData catalog)
        {
            if (catalog == null) return System.Array.Empty<LogicDropdownColumnSpec>();

            string secRoleBadge     = "SEC";
            string tooltipRoleBadge = "AUX";
            IReadOnlyList<string> extraRoles = null;
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled) continue;
                    if (card.Type != CardTypeDropdown && card.Type != CardTypeSearch &&
                        card.Type != CardTypeButton   && card.Type != CardTypeMultiPick) continue;
                    string s, t;
                    if (card.Params.TryGetValue(ParamSecRole, out s) && !string.IsNullOrEmpty(s))
                        secRoleBadge = s;
                    if (card.Params.TryGetValue(ParamTooltipRole, out t) && !string.IsNullOrEmpty(t))
                        tooltipRoleBadge = t;
                    extraRoles = ReadDisplayRoles(card);
                    break;
                }
            }

            var specs = new System.Collections.Generic.List<LogicDropdownColumnSpec>();
            string priLabel = GetColumnLabel(catalog, ColumnRole.PrimaryDisplay, 1);
            if (priLabel == null) return System.Array.Empty<LogicDropdownColumnSpec>();
            specs.Add(new LogicDropdownColumnSpec(priLabel, 0));

            string secLabel = GetRoleLabel(catalog, secRoleBadge);
            if (!string.IsNullOrEmpty(secLabel)) specs.Add(new LogicDropdownColumnSpec(secLabel, 1));

            string auxLabel = GetRoleLabel(catalog, tooltipRoleBadge);
            if (!string.IsNullOrEmpty(auxLabel)) specs.Add(new LogicDropdownColumnSpec(auxLabel, 2));

            if (extraRoles != null)
            {
                int extraIdx = 3;
                foreach (var r in extraRoles)
                {
                    string l = GetRoleLabel(catalog, r);
                    if (!string.IsNullOrEmpty(l)) specs.Add(new LogicDropdownColumnSpec(l, extraIdx));
                    extraIdx++;
                }
            }

            return specs;
        }

        // ── Multi-token autocomplete ──────────────────────────────────────────

        /// <summary>
        /// Returns the separator and catalog lookup-column key for per-token autocomplete.
        /// For MultiPick groups the lookup column is PRI; for PairTransform groups it is the LookupRole column.
        /// Returns (null, null, null) when no relevant card is found.
        /// </summary>
        public static MultiTokenAutoCompleteConfig GetMultiTokenAutoCompleteConfig(CardGroup group, CatalogData catalog)
        {
            if (group == null || catalog == null) return new MultiTokenAutoCompleteConfig(null, null, null);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled) continue;
                if (card.Type == CardTypeMultiPick)
                {
                    string sep;
                    card.Params.TryGetValue(ParamPrimaryTokenSeparator, out sep);
                    string priKey = GetColumnKey(catalog, ColumnRole.PrimaryDisplay,   1);
                    string secKey = GetColumnKey(catalog, ColumnRole.SecondaryDisplay, 1);
                    return new MultiTokenAutoCompleteConfig(string.IsNullOrEmpty(sep) ? "-" : sep, priKey, secKey);
                }
                if (card.Type == CardTypePairTransform)
                {
                    string sep, lrBadge, orBadge;
                    card.Params.TryGetValue(ParamSourceTokenSeparator, out sep);
                    card.Params.TryGetValue(ParamLookupRole,           out lrBadge);
                    card.Params.TryGetValue(ParamOutputRole,           out orBadge);
                    string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lrBadge) ? "PRI" : lrBadge);
                    string sKey = GetRoleKey(catalog, string.IsNullOrEmpty(orBadge) ? "SEC" : orBadge);
                    return new MultiTokenAutoCompleteConfig(string.IsNullOrEmpty(sep) ? "-" : sep, lKey, sKey);
                }
            }
            return new MultiTokenAutoCompleteConfig(null, null, null);
        }
    }

    // ── Value-type return structs (replaces named tuples for clarity in net48) ──

    public struct MultiPickConfig
    {
        public string PrimarySep;
        public string CompanionFieldKey;
        public string CompanionRole;
        public string CompanionSep;
        public MultiPickConfig(string primarySep, string companionFieldKey, string companionRole, string companionSep)
        { PrimarySep = primarySep; CompanionFieldKey = companionFieldKey; CompanionRole = companionRole; CompanionSep = companionSep; }
    }

    public struct PairTransformConfig
    {
        public string SourceSep;
        public string LookupRole;
        public string OutputRole;
        public string OutputSep;
        public string CompanionFieldKey;
        public PairTransformConfig(string sourceSep, string lookupRole, string outputRole, string outputSep, string companionFieldKey)
        { SourceSep = sourceSep; LookupRole = lookupRole; OutputRole = outputRole; OutputSep = outputSep; CompanionFieldKey = companionFieldKey; }
    }

    public struct MultiTokenAutoCompleteConfig
    {
        public string Separator;
        public string LookupColKey;
        public string SecColKey;
        public MultiTokenAutoCompleteConfig(string separator, string lookupColKey, string secColKey)
        { Separator = separator; LookupColKey = lookupColKey; SecColKey = secColKey; }
    }

    public struct LogicDropdownColumnSpec
    {
        public string Label;
        public int    SourceIndex;
        public LogicDropdownColumnSpec(string label, int sourceIndex)
        { Label = label; SourceIndex = sourceIndex; }
    }
}
