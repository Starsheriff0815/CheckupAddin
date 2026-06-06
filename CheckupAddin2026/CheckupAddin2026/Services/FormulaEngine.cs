using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Evaluation context for a single Basic Logic invocation.
    /// Provides access to live Inventor field values, the user-typed input, and catalog lookups.
    /// </summary>
    public sealed class FormulaContext
    {
        /// <summary>Resolves a field key to its current display value (e.g. PARAM:Width → "120 mm").</summary>
        public Func<string, string> ResolveFieldValue { get; set; }

        /// <summary>The user-typed value entering the Apply path; resolved by `{INPUT}`.</summary>
        public string InputValue { get; set; } = "";

        /// <summary>Catalog lookup callback for LOOKUP(): (key, searchCol, returnCol, catalogName).</summary>
        public Func<string, string, string, string, string> Lookup { get; set; }
    }

    /// <summary>
    /// Minimal recursive-descent evaluator for Basic Logic formulas.
    ///
    /// Reference syntax:
    /// - <c>{field.key}</c>                — resolves to <see cref="FormulaContext.ResolveFieldValue"/>
    /// - <c>{INPUT}</c>                    — resolves to <see cref="FormulaContext.InputValue"/>
    /// - <c>$[field.key]</c>              — V1 Expert Mode: same semantics as {field.key} but marks an
    ///                                       explicit cross-row live read; enables ⚡ visual and topo sort
    /// - <c>"literal"</c>                  — string literal (double-quoted; \" escapes)
    /// - numeric literals                  — parsed in invariant culture
    ///
    /// Functions: CONCATENATE, IF, LOOKUP, FORMAT, ROUND, VALUE, NUM, EQ, NE, LT, GT, LTE, GTE,
    ///   AND, OR, NOT, JOIN, LEFT, RIGHT, MID, TRIM, UPPER, LOWER, REPLACE, ABS, LEN,
    ///   CONTAINS, STARTSWITH, ENDSWITH, ISEMPTY, DEFAULT, STR.
    /// Errors propagate as <see cref="FormulaException"/>; callers may catch and render `#ERROR: …`.
    /// </summary>
    public static class FormulaEngine
    {
        public static string Evaluate(string formula, FormulaContext ctx)
        {
            if (string.IsNullOrEmpty(formula)) return "";
            var parser = new Parser(formula, ctx);
            object result = parser.ParseExpression();
            parser.ExpectEnd();
            return ToStr(result);
        }

        /// <summary>Returns all field keys referenced via <c>$[KEY]</c> Expert Mode syntax in <paramref name="formula"/>.</summary>
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

        /// <summary>True when <paramref name="formula"/> contains at least one <c>$[...]</c> Expert Mode reference.</summary>
        public static bool HasExpertRef(string formula)
        {
            if (string.IsNullOrEmpty(formula)) return false;
            return formula.IndexOf("$[", StringComparison.Ordinal) >= 0;
        }

        // ── Internal value coercion ───────────────────────────────────────────

        private static string ToStr(object o)
        {
            if (o == null) return "";
            if (o is string s) return s;
            if (o is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (o is bool b) return b ? "true" : "false";
            return o.ToString() ?? "";
        }

        private static bool ToBool(object o)
        {
            if (o is bool b) return b;
            if (o is double d) return d != 0;
            string s = ToStr(o);
            if (string.IsNullOrEmpty(s)) return false;
            if (bool.TryParse(s, out bool parsed)) return parsed;
            return !string.IsNullOrEmpty(s);
        }

        private static double ToNum(object o)
        {
            if (o is double d) return d;
            if (o is bool b) return b ? 1 : 0;
            string s = ToStr(o)?.Trim() ?? "";
            if (s.Length == 0) return 0;
            // Strip trailing unit (e.g. "120 mm" → 120)
            int i = 0;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            int numStart = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',')) i++;
            string numPart = s.Substring(0, i).Replace(',', '.');
            if (double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                return v;
            return 0;
        }

        // ── Parser ────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly string _src;
            private readonly FormulaContext _ctx;
            private int _pos;

            public Parser(string src, FormulaContext ctx) { _src = src; _ctx = ctx; _pos = 0; }

            public void ExpectEnd()
            {
                SkipWs();
                if (_pos < _src.Length)
                    throw new FormulaException("Unexpected trailing characters at position " + _pos);
            }

            public object ParseExpression()
            {
                SkipWs();
                if (_pos >= _src.Length) return "";

                char c = _src[_pos];

                if (c == '"') return ParseStringLiteral();
                if (c == '{') return ParseFieldRef();
                if (c == '$' && _pos + 1 < _src.Length && _src[_pos + 1] == '[') return ParseExpertFieldRef();
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.') return ParseNumber();
                if (char.IsLetter(c)) return ParseFunctionCall();

                throw new FormulaException("Unexpected character '" + c + "' at position " + _pos);
            }

            private void SkipWs()
            {
                while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
            }

            private string ParseStringLiteral()
            {
                _pos++; // opening "
                var sb = new StringBuilder();
                while (_pos < _src.Length && _src[_pos] != '"')
                {
                    if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                    {
                        sb.Append(_src[_pos + 1]);
                        _pos += 2;
                        continue;
                    }
                    sb.Append(_src[_pos]);
                    _pos++;
                }
                if (_pos >= _src.Length) throw new FormulaException("Unterminated string literal");
                _pos++; // closing "
                return sb.ToString();
            }

            private string ParseFieldRef()
            {
                _pos++; // opening {
                int start = _pos;
                while (_pos < _src.Length && _src[_pos] != '}') _pos++;
                if (_pos >= _src.Length) throw new FormulaException("Unterminated field reference");
                string key = _src.Substring(start, _pos - start).Trim();
                _pos++; // closing }
                if (string.Equals(key, "INPUT", StringComparison.OrdinalIgnoreCase))
                    return _ctx?.InputValue ?? "";
                return _ctx?.ResolveFieldValue?.Invoke(key) ?? "";
            }

            private string ParseExpertFieldRef()
            {
                _pos += 2; // skip $[
                int start = _pos;
                while (_pos < _src.Length && _src[_pos] != ']') _pos++;
                if (_pos >= _src.Length) throw new FormulaException("Unterminated expert field reference '$['");
                string key = _src.Substring(start, _pos - start).Trim();
                _pos++; // closing ]
                return _ctx?.ResolveFieldValue?.Invoke(key) ?? "";
            }

            private double ParseNumber()
            {
                int start = _pos;
                if (_src[_pos] == '+' || _src[_pos] == '-') _pos++;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                string s = _src.Substring(start, _pos - start);
                if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                    throw new FormulaException("Invalid number '" + s + "'");
                return v;
            }

            private object ParseFunctionCall()
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                string name = _src.Substring(start, _pos - start).ToUpperInvariant();

                SkipWs();
                if (_pos >= _src.Length || _src[_pos] != '(')
                    throw new FormulaException("Expected '(' after function name '" + name + "'");
                _pos++; // (

                var args = new List<object>();
                SkipWs();
                if (_pos < _src.Length && _src[_pos] != ')')
                {
                    args.Add(ParseExpression());
                    SkipWs();
                    while (_pos < _src.Length && _src[_pos] == ',')
                    {
                        _pos++;
                        args.Add(ParseExpression());
                        SkipWs();
                    }
                }
                if (_pos >= _src.Length || _src[_pos] != ')')
                    throw new FormulaException("Expected ')' to close '" + name + "'");
                _pos++; // )

                return Dispatch(name, args);
            }

            private object Dispatch(string name, List<object> args)
            {
                switch (name)
                {
                    case "CONCATENATE":
                    {
                        var sb = new StringBuilder();
                        foreach (var a in args) sb.Append(ToStr(a));
                        return sb.ToString();
                    }
                    case "IF":
                    {
                        if (args.Count < 2) throw new FormulaException("IF requires 2 or 3 arguments");
                        return ToBool(args[0]) ? args[1] : (args.Count > 2 ? args[2] : "");
                    }
                    case "LOOKUP":
                    {
                        if (args.Count < 3) throw new FormulaException("LOOKUP requires at least 3 arguments");
                        string key       = ToStr(args[0]);
                        string searchCol = ToStr(args[1]);
                        string returnCol = ToStr(args[2]);
                        string catName   = args.Count > 3 ? ToStr(args[3]) : "";
                        return _ctx?.Lookup?.Invoke(key, searchCol, returnCol, catName) ?? "";
                    }
                    case "FORMAT":
                    {
                        if (args.Count < 2) throw new FormulaException("FORMAT requires 2 arguments");
                        double v = ToNum(args[0]);
                        string fmt = ToStr(args[1]);
                        try { return v.ToString(fmt, CultureInfo.InvariantCulture); }
                        catch { return ToStr(args[0]); }
                    }
                    case "ROUND":
                    {
                        if (args.Count < 1) throw new FormulaException("ROUND requires 1 or 2 arguments");
                        double v = ToNum(args[0]);
                        int digits = args.Count > 1 ? (int)ToNum(args[1]) : 0;
                        return Math.Round(v, digits, MidpointRounding.AwayFromZero);
                    }
                    case "NUM":
                    case "VALUE":
                    {
                        if (args.Count < 1) throw new FormulaException("VALUE requires 1 argument");
                        return ToNum(args[0]);
                    }
                    case "STR": return ToStr(args.Count > 0 ? args[0] : (object)"");
                    case "EQ":  return ToStr(args[0]) == ToStr(args[1]);
                    case "NE":  return ToStr(args[0]) != ToStr(args[1]);
                    case "LT":  return ToNum(args[0])  < ToNum(args[1]);
                    case "GT":  return ToNum(args[0])  > ToNum(args[1]);
                    case "LTE": return ToNum(args[0]) <= ToNum(args[1]);
                    case "GTE": return ToNum(args[0]) >= ToNum(args[1]);
                    case "AND":
                    {
                        foreach (var a in args) if (!ToBool(a)) return false;
                        return true;
                    }
                    case "OR":
                    {
                        foreach (var a in args) if (ToBool(a)) return true;
                        return false;
                    }
                    case "NOT": return !ToBool(args[0]);
                    case "JOIN":
                    {
                        if (args.Count < 1) throw new FormulaException("JOIN requires at least 1 argument");
                        string sep = ToStr(args[0]);
                        var parts = new List<string>();
                        for (int j = 1; j < args.Count; j++) parts.Add(ToStr(args[j]));
                        return string.Join(sep, parts);
                    }
                    case "LEFT":
                    {
                        if (args.Count < 2) throw new FormulaException("LEFT requires 2 arguments");
                        return StrLeft(ToStr(args[0]), (int)ToNum(args[1]));
                    }
                    case "RIGHT":
                    {
                        if (args.Count < 2) throw new FormulaException("RIGHT requires 2 arguments");
                        return StrRight(ToStr(args[0]), (int)ToNum(args[1]));
                    }
                    case "MID":
                    {
                        if (args.Count < 2) throw new FormulaException("MID requires at least 2 arguments");
                        return StrMid(ToStr(args[0]), (int)ToNum(args[1]), args.Count >= 3 ? (int)ToNum(args[2]) : int.MaxValue);
                    }
                    case "TRIM":    return ToStr(args.Count > 0 ? args[0] : (object)"").Trim();
                    case "UPPER":   return ToStr(args.Count > 0 ? args[0] : (object)"").ToUpperInvariant();
                    case "LOWER":   return ToStr(args.Count > 0 ? args[0] : (object)"").ToLowerInvariant();
                    case "REPLACE":
                    {
                        if (args.Count < 3) throw new FormulaException("REPLACE requires 3 arguments");
                        return StrReplaceIgnoreCase(ToStr(args[0]), ToStr(args[1]), ToStr(args[2]));
                    }
                    case "ABS":
                    {
                        if (args.Count < 1) throw new FormulaException("ABS requires 1 argument");
                        return Math.Abs(ToNum(args[0]));
                    }
                    case "LEN":
                    {
                        if (args.Count < 1) throw new FormulaException("LEN requires 1 argument");
                        return (double)ToStr(args[0]).Length;
                    }
                    case "CONTAINS":
                    {
                        if (args.Count < 2) throw new FormulaException("CONTAINS requires 2 arguments");
                        return ToStr(args[0]).IndexOf(ToStr(args[1]), StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    case "STARTSWITH":
                    {
                        if (args.Count < 2) throw new FormulaException("STARTSWITH requires 2 arguments");
                        return ToStr(args[0]).StartsWith(ToStr(args[1]), StringComparison.OrdinalIgnoreCase);
                    }
                    case "ENDSWITH":
                    {
                        if (args.Count < 2) throw new FormulaException("ENDSWITH requires 2 arguments");
                        return ToStr(args[0]).EndsWith(ToStr(args[1]), StringComparison.OrdinalIgnoreCase);
                    }
                    case "ISEMPTY":
                    {
                        if (args.Count < 1) throw new FormulaException("ISEMPTY requires 1 argument");
                        return string.IsNullOrEmpty(ToStr(args[0]));
                    }
                    case "DEFAULT":
                    {
                        if (args.Count < 2) throw new FormulaException("DEFAULT requires 2 arguments");
                        string dv = ToStr(args[0]);
                        return string.IsNullOrEmpty(dv) ? (object)args[1] : args[0];
                    }
                    default:
                        throw new FormulaException("Unknown function '" + name + "'");
                }
            }
            private static string StrLeft(string s, int n)
            {
                if (n <= 0) return "";
                if (n >= s.Length) return s;
                return s.Substring(0, n);
            }

            private static string StrRight(string s, int n)
            {
                if (n <= 0) return "";
                if (n >= s.Length) return s;
                return s.Substring(s.Length - n);
            }

            private static string StrMid(string s, int start, int len)
            {
                if (start < 0) start = 0;
                if (start >= s.Length) return "";
                int count = Math.Min(len, s.Length - start);
                return count <= 0 ? "" : s.Substring(start, count);
            }

            private static string StrReplaceIgnoreCase(string src, string oldVal, string newVal)
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
    }

    /// <summary>Thrown when a formula cannot be parsed or evaluated. Caller may wrap as `#ERROR: msg`.</summary>
    public class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }
}
