using System.IO;

namespace CheckupAddIn.Services
{
    /// <summary>Where an iLogic rule assigned to a Rule Button comes from.</summary>
    public enum RuleSource
    {
        /// <summary>Empty Rule Button — no rule assigned yet.</summary>
        None,
        /// <summary>External Rule from Inventor's External Rule Directories (iLogic Configuration).</summary>
        External,
        /// <summary>Rule file from the add-in's own <c>Rules\</c> folder next to the DLL ("Checkup" group).</summary>
        AddIn,
        /// <summary>Document Rule stored inside the active document.</summary>
        Document,
    }

    /// <summary>
    /// Field Key format for Rule Rows (TDD §10.4). The whole rule identity lives in the key, so
    /// presets store Rule Rows like any other Row:
    ///   SPECIAL:RULE:                      — empty Rule Button
    ///   SPECIAL:RULE:EXT:&lt;relative path&gt;   — External Rule (path relative to its rule directory)
    ///   SPECIAL:RULE:ADDIN:&lt;relative path&gt; — rule from the add-in's Rules\ folder
    ///   SPECIAL:RULE:DOC:&lt;rule name&gt;       — Document Rule of the active document
    /// The source tag keeps identical file names in different places distinct.
    /// </summary>
    public static class RuleKey
    {
        public const string Prefix      = "SPECIAL:RULE:";
        public const string ExtTag      = "EXT:";
        public const string AddInTag    = "ADDIN:";
        public const string DocTag      = "DOC:";

        /// <summary>File extensions iLogic accepts for External Rules (D15).</summary>
        public static readonly string[] RuleFileExtensions = { ".iLogicVb", ".vb", ".txt" };

        public static bool IsRuleKey(string fieldKey) =>
            fieldKey != null && fieldKey.StartsWith(Prefix, StringComparison.Ordinal);

        public static bool IsRuleFile(string path)
        {
            string ext = Path.GetExtension(path ?? "");
            return RuleFileExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Builds a Field Key. <paramref name="name"/> = relative path (External / AddIn) or rule name (Document).</summary>
        public static string Format(RuleSource source, string name)
        {
            name = NormalizeRelativePath(source, name);
            return source switch
            {
                RuleSource.External => Prefix + ExtTag   + name,
                RuleSource.AddIn    => Prefix + AddInTag + name,
                RuleSource.Document => Prefix + DocTag   + name,
                _                   => Prefix,
            };
        }

        /// <summary>
        /// Splits a Field Key into source + name. Returns false for non-rule keys. An empty
        /// Rule Button, an unknown tag or a tag without a name all parse as <see cref="RuleSource.None"/>.
        /// </summary>
        public static bool TryParse(string fieldKey, out RuleSource source, out string name)
        {
            source = RuleSource.None;
            name   = "";
            if (!IsRuleKey(fieldKey)) return false;

            string rest = fieldKey.Substring(Prefix.Length);
            if (rest.StartsWith(ExtTag, StringComparison.Ordinal))
            { source = RuleSource.External; name = rest.Substring(ExtTag.Length); }
            else if (rest.StartsWith(AddInTag, StringComparison.Ordinal))
            { source = RuleSource.AddIn;    name = rest.Substring(AddInTag.Length); }
            else if (rest.StartsWith(DocTag, StringComparison.Ordinal))
            { source = RuleSource.Document; name = rest.Substring(DocTag.Length); }

            if (string.IsNullOrEmpty(name)) source = RuleSource.None;
            return true;
        }

        /// <summary>Button label: file name without folder for file rules, rule name for Document Rules.</summary>
        public static string DisplayName(RuleSource source, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (source == RuleSource.Document) return name;
            int slash = name.LastIndexOf('\\');
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }

        // Relative paths are stored with backslashes and without a leading separator so the same
        // rule always produces the same key, regardless of how the path was assembled.
        private static string NormalizeRelativePath(RuleSource source, string name)
        {
            name ??= "";
            if (source == RuleSource.External || source == RuleSource.AddIn)
                name = name.Replace('/', '\\').TrimStart('\\');
            return name;
        }
    }
}
