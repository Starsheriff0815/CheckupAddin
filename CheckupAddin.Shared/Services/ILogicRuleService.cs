using System.Collections;
using System.Reflection;
using CheckupAddIn.Models;
using Inventor;
using Microsoft.VisualBasic;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Discovers and runs iLogic rules for Rule Buttons (TDD §10.4 — "Run iLogic Rule").
    /// </summary>
    /// <remarks>
    /// All iLogic access goes through the iLogic add-in's automation object, late-bound via
    /// <c>Interaction.CallByName</c> (same pattern as the other version-sensitive COM paths) so
    /// no iLogic assembly is referenced and every Inventor release (2024–2027) works unchanged.
    /// Rules are always handed to iLogic with the ACTIVE document — the same as running a rule
    /// from Inventor's iLogic browser (D7). Processing child documents or the selection is the
    /// rule's own job; this service never touches Inventor's selection.
    /// </remarks>
    public sealed class ILogicRuleService
    {
        /// <summary>Client id of Autodesk's iLogic add-in (identical in all Inventor releases).</summary>
        private const string ILogicAddInId = "{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}";

        /// <summary>Sub-folder next to the add-in DLL holding the shipped template rules (D9).</summary>
        public const string AddInRulesFolderName = "Rules";

        private readonly Inventor.Application _app;

        public ILogicRuleService(Inventor.Application app)
        {
            _app = app;
        }

        /// <summary>The add-in's own <c>Rules\</c> folder (next to the DLL).</summary>
        public static string AddInRulesDirectory
        {
            get
            {
                string dir = "";
                try { dir = Path.GetDirectoryName(typeof(ILogicRuleService).Assembly.Location) ?? ""; }
                catch { }
                return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, AddInRulesFolderName);
            }
        }

        // ══════════════════════════════════════════════
        //  iLogic automation (late-bound)
        // ══════════════════════════════════════════════

        /// <summary>Returns the iLogic automation object, or null when iLogic is not loaded.</summary>
        private object GetAutomation()
        {
            try
            {
                ApplicationAddIn addIn = _app?.ApplicationAddIns.ItemById[ILogicAddInId];
                if (addIn == null || !addIn.Activated) return null;
                return addIn.Automation;
            }
            catch { return null; }
        }

        public bool IsILogicAvailable => GetAutomation() != null;

        /// <summary>Inventor's External Rule Directories (iLogic Configuration), in configured order.</summary>
        public IReadOnlyList<string> GetExternalRuleDirectories()
        {
            var result = new List<string>();
            try
            {
                object auto = GetAutomation();
                if (auto == null) return result;
                object options = Interaction.CallByName(auto, "FileOptions", CallType.Get);
                if (Interaction.CallByName(options, "ExternalRuleDirectories", CallType.Get) is IEnumerable dirs)
                {
                    foreach (object d in dirs)
                    {
                        string dir = d as string;
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                            && !result.Any(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)))
                            result.Add(dir);
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Document Rules stored in <paramref name="doc"/>: (name, isActive). Empty on any failure.</summary>
        public IReadOnlyList<(string Name, bool IsActive)> GetDocumentRules(Document doc)
        {
            var result = new List<(string, bool)>();
            if (doc == null) return result;
            try
            {
                object auto = GetAutomation();
                if (auto == null) return result;
                if (Interaction.CallByName(auto, "Rules", CallType.Method, doc) is IEnumerable rules)
                {
                    foreach (object rule in rules)
                    {
                        try
                        {
                            string name = Interaction.CallByName(rule, "Name", CallType.Get) as string;
                            if (string.IsNullOrEmpty(name)) continue;
                            bool active = true;
                            try { active = (bool)Interaction.CallByName(rule, "IsActive", CallType.Get); }
                            catch { }
                            result.Add((name, active));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        // ══════════════════════════════════════════════
        //  Resolution + presence
        // ══════════════════════════════════════════════

        /// <summary>
        /// Full path of a file rule, or null when not present. External: searched across the
        /// External Rule Directories in configured order, first match wins (as iLogic resolves names).
        /// </summary>
        public string ResolveRuleFile(RuleSource source, string relativePath,
                                      IReadOnlyList<string> externalDirs = null)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                if (source == RuleSource.AddIn)
                {
                    string p = ContainedRulePath(AddInRulesDirectory, relativePath);
                    return p != null && File.Exists(p) ? p : null;
                }
                if (source == RuleSource.External)
                {
                    foreach (string dir in externalDirs ?? GetExternalRuleDirectories())
                    {
                        string p = ContainedRulePath(dir, relativePath);
                        if (p != null && File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Full path of <paramref name="relativePath"/> under <paramref name="root"/>, or null when it is
        /// rooted, escapes the root (<c>..\</c>) or is not a rule file. Field Keys arrive from preset files
        /// that may be shared between machines — a Rule Button must never run a file outside the rule folders.
        /// </summary>
        internal static string ContainedRulePath(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                if (Path.IsPathRooted(relativePath)) return null;
                string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                string full     = Path.GetFullPath(Path.Combine(rootFull, relativePath));
                return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && RuleKey.IsRuleFile(full)
                    ? full : null;
            }
            catch { return null; }
        }

        // ══════════════════════════════════════════════
        //  Run
        // ══════════════════════════════════════════════

        /// <summary>
        /// Runs a rule on the active document (synchronous — iLogic runs on Inventor's UI thread).
        /// Returns null on success, otherwise an error text. Errors raised INSIDE the rule are
        /// reported by iLogic's own error dialog, exactly as when run from Inventor.
        /// </summary>
        public string Run(RuleSource source, string name)
        {
            Document doc = null;
            try { doc = _app?.ActiveDocument; } catch { }
            if (doc == null) return LanguageLoader.Get("Msg_NoDocument");

            object auto = GetAutomation();
            if (auto == null) return LanguageLoader.Get("Rule_ILogicUnavailable");

            try
            {
                if (source == RuleSource.Document)
                {
                    Interaction.CallByName(auto, "RunRule", CallType.Method, doc, name);
                    return null;
                }
                string path = ResolveRuleFile(source, name);
                if (path == null) return LanguageLoader.Get("Rule_Missing");
                Interaction.CallByName(auto, "RunExternalRule", CallType.Method, doc, path);
                return null;
            }
            catch (Exception ex)
            {
                // CallByName wraps the real failure in a TargetInvocationException.
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return inner.Message;
            }
        }

        // ══════════════════════════════════════════════
        //  Rule Selector contents
        // ══════════════════════════════════════════════

        /// <summary>
        /// Builds the Rule Selector groups (D6): Document Rules of the active document, then one
        /// group per folder of the External Rule Directories, then the add-in's Rules\ folder.
        /// </summary>
        public List<RuleSelectorGroupVm> BuildSelectorGroups()
        {
            Document doc = null;
            try { doc = _app?.ActiveDocument; } catch { }

            var groups = new List<RuleSelectorGroupVm>();

            var docRules = GetDocumentRules(doc)
                .OrderBy(r => r.Name, FieldCatalogBuilder.NaturalComparer)
                .Select(r => new RuleSelectorItem
                {
                    Key          = RuleKey.Format(RuleSource.Document, r.Name),
                    DisplayName  = r.Name,
                    ToolTip      = r.IsActive ? r.Name : r.Name + " — " + LanguageLoader.Get("Rule_Inactive"),
                    IsSelectable = r.IsActive,
                })
                .ToList();
            if (docRules.Count > 0)
                groups.Add(new RuleSelectorGroupVm { GroupDisplayName = LanguageLoader.Get("Grp_RuleDocument"), AllItems = docRules });

            var roots = GetExternalRuleDirectories()
                .Select(d => (Root: d, Source: RuleSource.External, RootLabel: (string)null))
                .ToList();
            string addInDir = AddInRulesDirectory;
            if (!string.IsNullOrEmpty(addInDir) && Directory.Exists(addInDir))
                roots.Add((addInDir, RuleSource.AddIn, LanguageLoader.Get("Grp_RuleAddIn")));

            groups.AddRange(BuildFileGroups(roots, ListFilesSafe));
            return groups;
        }

        /// <summary>
        /// Pure grouping logic (unit-tested): one group per folder containing rule files, label =
        /// folder name (a root may carry a fixed <c>RootLabel</c>, e.g. "Checkup"); duplicate labels
        /// become <c>Parent\Folder</c>; entries in natural order; roots keep their given order.
        /// </summary>
        internal static List<RuleSelectorGroupVm> BuildFileGroups(
            IEnumerable<(string Root, RuleSource Source, string RootLabel)> roots,
            Func<string, IEnumerable<string>> listFiles)
        {
            var raw = new List<(string Label, string Dir, List<RuleSelectorItem> Items)>();

            foreach (var (root, source, rootLabel) in roots)
            {
                string rootFull = root.TrimEnd('\\', '/');
                var byDir = listFiles(rootFull)
                    .Where(RuleKey.IsRuleFile)
                    .GroupBy(f => Path.GetDirectoryName(f) ?? rootFull, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => RelativeTo(rootFull, g.Key), FieldCatalogBuilder.NaturalComparer);

                foreach (var dirGroup in byDir)
                {
                    bool isRoot = string.Equals(dirGroup.Key.TrimEnd('\\', '/'), rootFull, StringComparison.OrdinalIgnoreCase);
                    string label = isRoot && !string.IsNullOrEmpty(rootLabel)
                        ? rootLabel
                        : Path.GetFileName(dirGroup.Key.TrimEnd('\\', '/'));

                    var items = dirGroup
                        .Select(f => new RuleSelectorItem
                        {
                            Key         = RuleKey.Format(source, RelativeTo(rootFull, f)),
                            DisplayName = Path.GetFileName(f),
                            ToolTip     = f,
                        })
                        .OrderBy(i => i.DisplayName, FieldCatalogBuilder.NaturalComparer)
                        .ToList();

                    raw.Add((label, dirGroup.Key, items));
                }
            }

            // Two groups with the same folder name → show Parent\Folder for both.
            var dupLabels = new HashSet<string>(
                raw.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            return raw.Select(r =>
            {
                string label = r.Label;
                if (dupLabels.Contains(label))
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(r.Dir.TrimEnd('\\', '/')) ?? "");
                    if (!string.IsNullOrEmpty(parent)) label = parent + "\\" + Path.GetFileName(r.Dir.TrimEnd('\\', '/'));
                }
                return new RuleSelectorGroupVm { GroupDisplayName = label, AllItems = r.Items };
            }).ToList();
        }

        private static string RelativeTo(string root, string path)
        {
            string p = path.TrimEnd('\\', '/');
            if (p.Length <= root.Length) return "";
            return p.Substring(root.Length).TrimStart('\\', '/');
        }

        /// <summary>Recursive file listing that skips folders it cannot read.</summary>
        private static IEnumerable<string> ListFilesSafe(string root)
        {
            var result  = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                try { result.AddRange(Directory.GetFiles(dir)); } catch { }
                try { foreach (string sub in Directory.GetDirectories(dir)) pending.Push(sub); } catch { }
            }
            return result;
        }
    }
}
