using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Provides values for formula evaluation: catalog column refs, Checkup field refs, and LOOKUP.
    /// </summary>
    public sealed class FormulaContext
    {
        public static readonly FormulaContext Empty = new FormulaContext();

        /// <summary>Column values keyed by label or role badge for the currently selected catalog row.</summary>
        public IReadOnlyDictionary<string, string> RowColumns { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolves a Checkup field key to its current display value ({...} syntax).</summary>
        public Func<string, string> GetFieldValue { get; set; } = _ => "";

        /// <summary>Resolves a field key referenced via $[KEY] Expert Mode syntax.</summary>
        public Func<string, string> ResolveFieldValue { get; set; } = _ => "";

        /// <summary>
        /// LOOKUP(key, searchCol, returnCol, catalogName) — finds key in searchCol, returns returnCol.
        /// catalogName="" uses the group's default catalog.
        /// </summary>
        public Func<string, string, string, string, string> Lookup { get; set; } = (a, b, c, d) => "";

        /// <summary>
        /// User-typed input value for formula cards that contain {INPUT}.
        /// When non-null, {INPUT} resolves to this string instead of an empty value.
        /// </summary>
        public string InputValue { get; set; }
    }

    /// <summary>
    /// Recursive-descent formula evaluator. All values are strings.
    /// Numeric coercion happens inside numeric functions.
    /// Errors surface as "#ERROR: ..." or "#PARSE_ERROR: ..." strings.
    ///
    /// Syntax:
    ///   expr       := literal | col_ref | field_ref | func_call
    ///   literal    := "..." | 123 | -4.5
    ///   col_ref    := [ColumnName]   — catalog column from selected row
    ///   field_ref  := {field.key}   — Checkup field value
    ///   func_call  := IDENT(arg, arg, ...)
    ///
    /// Supported functions: CONCATENATE JOIN IF EQ NE LT GT LTE GTE AND OR NOT
    ///   CONTAINS STARTSWITH ENDSWITH FORMAT ROUND LOOKUP LEFT RIGHT MID TRIM
    ///   UPPER LOWER REPLACE NUM STR ABS LEN ISEMPTY DEFAULT
    /// </summary>
    public static class FormulaEngine
    {
        /// <summary>Returns all field keys referenced via $[KEY] Expert Mode syntax in formula.</summary>
        public static IEnumerable<string> GetExpertRefs(string formula)
        {
            if (string.IsNullOrEmpty(formula)) yield break;
            int i = 0;
            while (i < formula.Length)
            {
                int open = formula.IndexOf("$[", i, StringComparison.Ordinal);
                if (open < 0) yield break;
                int close = formula.IndexOf(']', open + 2);
                if (close < 0) yield break;
                string key = formula.Substring(open + 2, close - open - 2).Trim();
                if (!string.IsNullOrEmpty(key)) yield return key;
                i = close + 1;
            }
        }

        /// <summary>True when formula contains at least one $[...] Expert Mode reference.</summary>
        public static bool HasExpertRef(string formula)
        {
            if (string.IsNullOrEmpty(formula)) return false;
            return formula.IndexOf("$[", StringComparison.Ordinal) >= 0;
        }

        public static string Evaluate(string formula, FormulaContext ctx)
        {
            if (string.IsNullOrWhiteSpace(formula)) return "";
            if (ctx == null) ctx = FormulaContext.Empty;
            try
            {
                var tokens = Tokenize(formula);
                if (tokens.Count == 0) return "";
                var parser = new Parser(tokens, ctx);
                string result = parser.ParseExpr();
                if (parser.Pos < tokens.Count)
                    return string.Format("#PARSE_ERROR: unexpected '{0}'", tokens[parser.Pos].Text);
                return result ?? "";
            }
            catch (FormulaException ex) { return "#ERROR: " + ex.Message; }
            catch (Exception ex)        { return "#ERROR: " + ex.Message; }
        }

        // ── Tokenizer ──────────────────────────────────────────────────────────

        private enum TT { Ident, String, Number, ColRef, FieldRef, ExpertRef, LParen, RParen, Comma }

        private sealed class Token
        {
            public TT     Type  { get; }
            public string Text  { get; }
            public int    Start { get; }
            public Token(TT type, string text, int start) { Type = type; Text = text; Start = start; }
        }

        private static List<Token> Tokenize(string src)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '"')
                {
                    int start = i++;
                    var sb = new StringBuilder();
                    while (i < src.Length && src[i] != '"')
                    {
                        if (src[i] == '\\' && i + 1 < src.Length) { i++; sb.Append(Unescape(src[i])); i++; }
                        else sb.Append(src[i++]);
                    }
                    if (i < src.Length) i++;
                    tokens.Add(new Token(TT.String, sb.ToString(), start));
                    continue;
                }
                if (c == '$' && i + 1 < src.Length && src[i + 1] == '[')
                {
                    int start = i; i += 2; // skip $[
                    var sb = new StringBuilder();
                    while (i < src.Length && src[i] != ']') sb.Append(src[i++]);
                    if (i < src.Length) i++;
                    tokens.Add(new Token(TT.ExpertRef, sb.ToString().Trim(), start));
                    continue;
                }
                if (c == '[')
                {
                    int start = i++;
                    var sb = new StringBuilder();
                    while (i < src.Length && src[i] != ']') sb.Append(src[i++]);
                    if (i < src.Length) i++;
                    tokens.Add(new Token(TT.ColRef, sb.ToString(), start));
                    continue;
                }
                if (c == '{')
                {
                    int start = i++;
                    var sb = new StringBuilder();
                    while (i < src.Length && src[i] != '}') sb.Append(src[i++]);
                    if (i < src.Length) i++;
                    tokens.Add(new Token(TT.FieldRef, sb.ToString(), start));
                    continue;
                }
                if (c == '(')  { tokens.Add(new Token(TT.LParen, "(", i++)); continue; }
                if (c == ')')  { tokens.Add(new Token(TT.RParen, ")", i++)); continue; }
                if (c == ',' || c == ';') { tokens.Add(new Token(TT.Comma, ",", i++)); continue; }

                // Negative number: '-' followed by a digit, and not right after ')'
                bool prevIsRParen = tokens.Count > 0 && tokens[tokens.Count - 1].Type == TT.RParen;
                if (char.IsDigit(c) || (c == '-' && i + 1 < src.Length && char.IsDigit(src[i + 1]) && (tokens.Count == 0 || !prevIsRParen)))
                {
                    int start = i;
                    var sb = new StringBuilder();
                    if (c == '-') sb.Append(src[i++]);
                    while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) sb.Append(src[i++]);
                    tokens.Add(new Token(TT.Number, sb.ToString(), start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    var sb = new StringBuilder();
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) sb.Append(src[i++]);
                    tokens.Add(new Token(TT.Ident, sb.ToString(), start));
                    continue;
                }

                i++; // skip unknown char
            }
            return tokens;
        }

        private static char Unescape(char c)
        {
            switch (c)
            {
                case 'n': return '\n';
                case 't': return '\t';
                case 'r': return '\r';
                default:  return c;
            }
        }

        // ── Parser / Evaluator ─────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly List<Token>    _tokens;
            private readonly FormulaContext _ctx;
            public           int            Pos;

            public Parser(List<Token> tokens, FormulaContext ctx) { _tokens = tokens; _ctx = ctx; }

            private Token Peek { get { return Pos < _tokens.Count ? _tokens[Pos] : null; } }

            public string ParseExpr()
            {
                var t = Peek;
                if (t == null) throw new FormulaException("Unexpected end of formula");
                switch (t.Type)
                {
                    case TT.String:   Pos++; return t.Text;
                    case TT.Number:   Pos++; return t.Text;
                    case TT.ColRef:      Pos++; return ResolveCol(t.Text);
                    case TT.FieldRef:   Pos++; return ResolveField(t.Text);
                    case TT.ExpertRef:  Pos++; return ResolveExpertRef(t.Text);
                    case TT.Ident:
                        if (Pos + 1 < _tokens.Count && _tokens[Pos + 1].Type == TT.LParen)
                        {
                            string name = t.Text; Pos++; Pos++; // consume ident + (
                            var args = new List<string>();
                            while (Peek != null && Peek.Type != TT.RParen)
                            {
                                args.Add(ParseExpr());
                                if (Peek != null && Peek.Type == TT.Comma) Pos++;
                            }
                            if (Peek != null && Peek.Type == TT.RParen) Pos++;
                            return CallFunction(name.ToUpperInvariant(), args);
                        }
                        Pos++;
                        return t.Text; // bare identifier → string literal
                    default:
                        throw new FormulaException(string.Format("Unexpected token '{0}'", t.Text));
                }
            }

            private string ResolveCol(string name)
            {
                string v;
                if (_ctx.RowColumns != null && _ctx.RowColumns.TryGetValue(name, out v)) return v ?? "";
                return "";
            }

            private string ResolveField(string key)
            {
                if (key.Equals("INPUT", StringComparison.OrdinalIgnoreCase))
                    return _ctx.InputValue ?? "";
                try { return _ctx.GetFieldValue != null ? (_ctx.GetFieldValue(key) ?? "") : ""; } catch { return ""; }
            }

            private string ResolveExpertRef(string key)
            {
                try { return _ctx.ResolveFieldValue != null ? (_ctx.ResolveFieldValue(key) ?? "") : ""; } catch { return ""; }
            }

            private string CallFunction(string name, List<string> args)
            {
                switch (name)
                {
                    case "CONCATENATE":  return string.Concat(args);
                    case "JOIN":         return args.Count >= 2 ? string.Join(A(args, 0), args.GetRange(1, args.Count - 1)) : "";
                    case "IF":           return B(A(args, 0)) ? A(args, 1) : A(args, 2);
                    case "EQ":           return BS(string.Equals(A(args, 0), A(args, 1), StringComparison.OrdinalIgnoreCase));
                    case "NE":           return BS(!string.Equals(A(args, 0), A(args, 1), StringComparison.OrdinalIgnoreCase));
                    case "LT":           return BS(N(A(args, 0)) <  N(A(args, 1)));
                    case "GT":           return BS(N(A(args, 0)) >  N(A(args, 1)));
                    case "LTE":          return BS(N(A(args, 0)) <= N(A(args, 1)));
                    case "GTE":          return BS(N(A(args, 0)) >= N(A(args, 1)));
                    case "AND":          { bool r = true;  foreach (var a in args) r = r && B(a);  return BS(r); }
                    case "OR":           { bool r = false; foreach (var a in args) r = r || B(a);  return BS(r); }
                    case "NOT":          return BS(!B(A(args, 0)));
                    case "CONTAINS":     return BS(A(args, 0).IndexOf(A(args, 1), StringComparison.OrdinalIgnoreCase) >= 0);
                    case "STARTSWITH":   return BS(A(args, 0).StartsWith(A(args, 1), StringComparison.OrdinalIgnoreCase));
                    case "ENDSWITH":     return BS(A(args, 0).EndsWith(A(args, 1), StringComparison.OrdinalIgnoreCase));
                    case "FORMAT":       return TryFormat(A(args, 0), A(args, 1));
                    case "ROUND":        return TryRound(A(args, 0), A(args, 1));
                    case "LOOKUP":       return DoLookup(A(args, 0), A(args, 1), A(args, 2), args.Count >= 4 ? A(args, 3) : "");
                    case "LEFT":         return Left(A(args, 0), (int)N(A(args, 1)));
                    case "RIGHT":        return Right(A(args, 0), (int)N(A(args, 1)));
                    case "MID":          return Mid(A(args, 0), (int)N(A(args, 1)), args.Count >= 3 ? (int)N(A(args, 2)) : int.MaxValue);
                    case "TRIM":         return A(args, 0).Trim();
                    case "UPPER":        return A(args, 0).ToUpperInvariant();
                    case "LOWER":        return A(args, 0).ToLowerInvariant();
                    case "REPLACE":      return ReplaceIgnoreCase(A(args, 0), A(args, 1), A(args, 2));
                    case "NUM":
                    case "VALUE":        return NS(N(A(args, 0)));
                    case "STR":          return A(args, 0);
                    case "ABS":          return NS(Math.Abs(N(A(args, 0))));
                    case "LEN":          return A(args, 0).Length.ToString(CultureInfo.InvariantCulture);
                    case "ISEMPTY":      return BS(string.IsNullOrEmpty(A(args, 0)));
                    case "DEFAULT":      return string.IsNullOrEmpty(A(args, 0)) ? A(args, 1) : A(args, 0);
                    default:             return string.Format("#ERROR: Unknown function '{0}'", name);
                }
            }

            private static string A(List<string> args, int i) => i < args.Count ? (args[i] ?? "") : "";
            private static bool   B(string s) => !string.IsNullOrEmpty(s) && s != "0"
                                                  && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
            private static string BS(bool b)  => b ? "true" : "false";
            private static double N(string s)  => ParseDouble(s);
            private static string NS(double d) => d.ToString(CultureInfo.InvariantCulture);

            private string DoLookup(string key, string searchCol, string returnCol, string catalogName)
            {
                try { return _ctx.Lookup != null ? (_ctx.Lookup(key, searchCol, returnCol, catalogName) ?? "") : ""; }
                catch { return ""; }
            }

            private static string TryFormat(string value, string format)
            {
                if (string.IsNullOrEmpty(format)) return value;
                double d;
                if (TryParseDouble(value, out d))
                    try { return d.ToString(format, CultureInfo.CurrentCulture); } catch { }
                return value;
            }

            private static string TryRound(string value, string decimalsStr)
            {
                double d;
                if (!TryParseDouble(value, out d)) return value;
                int di;
                int dec = int.TryParse(decimalsStr, out di) ? Math.Max(0, di) : 0;
                return Math.Round(d, dec, MidpointRounding.AwayFromZero).ToString(CultureInfo.CurrentCulture);
            }

            /// <summary>
            /// Parses a double from s, including Inventor parameter expressions like "1,5 mm".
            /// Order: CurrentCulture → InvariantCulture → strip trailing unit suffix, retry both.
            /// </summary>
            private static bool TryParseDouble(string s, out double result)
            {
                result = 0.0;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim();
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture,  out result)) return true;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
                // Strip trailing non-digit unit suffix (e.g. " mm", " cm")
                int end = s.Length - 1;
                while (end >= 0 && !char.IsDigit(s[end])) end--;
                if (end >= 0 && end < s.Length - 1)
                {
                    string num = s.Substring(0, end + 1);
                    if (double.TryParse(num, NumberStyles.Float, CultureInfo.CurrentCulture,  out result)) return true;
                    if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
                }
                return false;
            }

            private static double ParseDouble(string s)
            {
                double d;
                return TryParseDouble(s, out d) ? d : 0.0;
            }

            private static string Left(string s, int n)
            {
                if (n <= 0) return "";
                if (n >= s.Length) return s;
                return s.Substring(0, n);
            }

            private static string Right(string s, int n)
            {
                if (n <= 0) return "";
                if (n >= s.Length) return s;
                return s.Substring(s.Length - n);
            }

            private static string Mid(string s, int start, int len)
            {
                if (start < 0) start = 0;
                if (start >= s.Length) return "";
                int count = Math.Min(len, s.Length - start);
                return count <= 0 ? "" : s.Substring(start, count);
            }

            // Case-insensitive string replace (string.Replace(str, str, StringComparison) not in net48)
            private static string ReplaceIgnoreCase(string src, string oldVal, string newVal)
            {
                if (string.IsNullOrEmpty(oldVal)) return src;
                var sb = new StringBuilder();
                int pos = 0;
                while (pos < src.Length)
                {
                    int idx = src.IndexOf(oldVal, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) { sb.Append(src, pos, src.Length - pos); break; }
                    sb.Append(src, pos, idx - pos);
                    sb.Append(newVal);
                    pos = idx + oldVal.Length;
                }
                return sb.ToString();
            }
        }

        private sealed class FormulaException : Exception
        {
            public FormulaException(string msg) : base(msg) { }
        }
    }
}
