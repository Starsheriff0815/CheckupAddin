using System.IO;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// T46 — "Run iLogic Rule" (TDD §10.4). Pure logic only: Field Key format, Rule Selector
    /// grouping/sorting, Rule Row display state, and the legacy StylePurge settings guard.
    /// The iLogic COM side (discovery + run) is verified manually in Inventor.
    /// </summary>
    public class RunILogicRuleTests
    {
        // ── Field Key format ──

        [Theory]
        [InlineData(RuleSource.External, @"Purge\MyRule.iLogicVb", @"SPECIAL:RULE:EXT:Purge\MyRule.iLogicVb")]
        [InlineData(RuleSource.AddIn,    "Bereinigen IDW+IPT+IAM.iLogicVb", "SPECIAL:RULE:ADDIN:Bereinigen IDW+IPT+IAM.iLogicVb")]
        [InlineData(RuleSource.Document, "Rule0", "SPECIAL:RULE:DOC:Rule0")]
        [InlineData(RuleSource.None,     "", "SPECIAL:RULE:")]
        public void Format_ThenTryParse_RoundTrips(RuleSource source, string name, string expectedKey)
        {
            string key = RuleKey.Format(source, name);
            Assert.Equal(expectedKey, key);

            Assert.True(RuleKey.TryParse(key, out RuleSource parsedSource, out string parsedName));
            Assert.Equal(source, parsedSource);
            Assert.Equal(name, parsedName);
        }

        // ── Rule file containment (audit fix — keys come from shareable preset files) ──

        [Theory]
        [InlineData(@"MyRule.iLogicVb",        @"C:\Rules\MyRule.iLogicVb")]
        [InlineData(@"Purge\MyRule.vb",        @"C:\Rules\Purge\MyRule.vb")]
        [InlineData(@"Purge\..\MyRule.txt",    @"C:\Rules\MyRule.txt")]
        public void ContainedRulePath_InsideRoot_IsResolved(string relative, string expected)
            => Assert.Equal(expected, ILogicRuleService.ContainedRulePath(@"C:\Rules\", relative), ignoreCase: true);

        [Theory]
        [InlineData(@"..\Evil.iLogicVb")]
        [InlineData(@"Purge\..\..\Evil.iLogicVb")]
        [InlineData(@"C:\Temp\Evil.iLogicVb")]
        [InlineData(@"\\server\share\Evil.iLogicVb")]
        [InlineData(@"\Evil.iLogicVb")]
        [InlineData(@"..\RulesX\Evil.iLogicVb")]   // sibling folder sharing the root's name prefix
        [InlineData(@"MyRule.exe")]
        [InlineData("")]
        [InlineData(null)]
        public void ContainedRulePath_OutsideRootOrNoRuleFile_IsRejected(string relative)
            => Assert.Null(ILogicRuleService.ContainedRulePath(@"C:\Rules", relative));

        [Fact]
        public void SameFileName_InExternalAndAddInFolder_GivesDistinctKeys()
        {
            // D9 workflow: the user copies the shipped template into their own rule folder.
            string ext   = RuleKey.Format(RuleSource.External, "Bereinigen IDW+IPT+IAM.iLogicVb");
            string addIn = RuleKey.Format(RuleSource.AddIn,    "Bereinigen IDW+IPT+IAM.iLogicVb");
            Assert.NotEqual(ext, addIn);
        }

        [Fact]
        public void Format_NormalizesSlashesAndLeadingSeparator()
        {
            Assert.Equal(@"SPECIAL:RULE:EXT:Sub\A.vb", RuleKey.Format(RuleSource.External, "/Sub/A.vb"));
        }

        [Theory]
        [InlineData("SPECIAL:RULE:")]
        [InlineData("SPECIAL:RULE:EXT:")]
        [InlineData("SPECIAL:RULE:UNKNOWN:x")]
        public void EmptyOrUnknown_ParsesAsEmptyRuleButton(string key)
        {
            Assert.True(RuleKey.TryParse(key, out RuleSource source, out _));
            Assert.Equal(RuleSource.None, source);
        }

        [Theory]
        [InlineData("SPECIAL:LOGIC:demo-g01")]
        [InlineData("UDEF:ISO")]
        [InlineData("")]
        [InlineData(null)]
        public void NonRuleKeys_AreNotRuleKeys(string key)
        {
            Assert.False(RuleKey.IsRuleKey(key));
            Assert.False(RuleKey.TryParse(key, out _, out _));
        }

        [Theory]
        [InlineData("a.iLogicVb", true)]
        [InlineData("a.ILOGICVB", true)]
        [InlineData("a.vb", true)]
        [InlineData("a.txt", true)]
        [InlineData("a.ipt", false)]
        [InlineData("a", false)]
        public void IsRuleFile_AcceptsILogicExtensionsOnly(string path, bool expected)
            => Assert.Equal(expected, RuleKey.IsRuleFile(path));

        [Fact]
        public void DisplayName_DropsFolderForFileRules_KeepsDocumentRuleName()
        {
            Assert.Equal("MyRule.iLogicVb", RuleKey.DisplayName(RuleSource.External, @"Purge\MyRule.iLogicVb"));
            Assert.Equal(@"A\B", RuleKey.DisplayName(RuleSource.Document, @"A\B"));
        }

        // ── Rule Selector grouping (D6) ──

        private static List<RuleSelectorGroupVm> Group(Dictionary<string, string[]> files,
            params (string Root, RuleSource Source, string RootLabel)[] roots)
            => ILogicRuleService.BuildFileGroups(roots,
                r => files.TryGetValue(r, out var f) ? f : System.Array.Empty<string>());

        [Fact]
        public void Groups_PerFolder_NaturalOrder_RootLabelForAddIn()
        {
            var files = new Dictionary<string, string[]>
            {
                [@"C:\Rules"] = new[]
                {
                    @"C:\Rules\Rule10.iLogicVb", @"C:\Rules\Rule2.iLogicVb", @"C:\Rules\notes.pdf",
                    @"C:\Rules\Drawing\Title.vb",
                },
                [@"C:\AddIn\Rules"] = new[] { @"C:\AddIn\Rules\Bereinigen IDW+IPT+IAM.iLogicVb" },
            };

            var groups = Group(files,
                (@"C:\Rules", RuleSource.External, null),
                (@"C:\AddIn\Rules", RuleSource.AddIn, "Checkup"));

            Assert.Equal(new[] { "Rules", "Drawing", "Checkup" }, groups.Select(g => g.GroupDisplayName));
            Assert.Equal(new[] { "Rule2.iLogicVb", "Rule10.iLogicVb" }, groups[0].AllItems.Select(i => i.DisplayName));   // natural, .pdf dropped
            Assert.Equal(@"SPECIAL:RULE:EXT:Drawing\Title.vb", groups[1].AllItems[0].Key);
            Assert.Equal("SPECIAL:RULE:ADDIN:Bereinigen IDW+IPT+IAM.iLogicVb", groups[2].AllItems[0].Key);
        }

        [Fact]
        public void DuplicateFolderNames_ShowParentAndFolder()
        {
            var files = new Dictionary<string, string[]>
            {
                [@"C:\A\Rules"] = new[] { @"C:\A\Rules\x.vb" },
                [@"D:\B\Rules"] = new[] { @"D:\B\Rules\y.vb" },
            };

            var groups = Group(files,
                (@"C:\A\Rules", RuleSource.External, null),
                (@"D:\B\Rules", RuleSource.External, null));

            Assert.Equal(new[] { @"A\Rules", @"B\Rules" }, groups.Select(g => g.GroupDisplayName));
        }

        [Fact]
        public void FoldersWithoutRuleFiles_ProduceNoGroup()
        {
            var files = new Dictionary<string, string[]> { [@"C:\Empty"] = new[] { @"C:\Empty\readme.md" } };
            Assert.Empty(Group(files, (@"C:\Empty", RuleSource.External, null)));
        }

        // ── Field Selector entry + Rule Row state ──

        [Fact]
        public void RuleEntry_IsSpecial_AndRecognisedAsRuleEntry()
        {
            var item = new FieldItem(RuleKey.Prefix, "Run iLogic Rule", "Run iLogic Rule", "Grp_Special");
            Assert.True(item.IsSpecialEntry);   // red "S:" tag
            Assert.True(item.IsRuleEntry);
            Assert.False(new FieldItem("SPECIAL:LOGIC:g", "x", "x").IsRuleEntry);
        }

        [Fact]
        public void RuleRow_ShowsRuleButton_InsteadOfValueDisplay()
        {
            var row = new RowModel { FieldKey = RuleKey.Format(RuleSource.AddIn, "a.iLogicVb") };

            Assert.True(row.IsRuleRow);
            Assert.True(row.IsSpecialRow);
            Assert.True(row.IsRuleButtonVisible);
            Assert.False(row.IsNormalDisplayMode);
            Assert.False(row.IsValueMismatchDisplayMode);
        }

        [Fact]
        public void NormalRow_HasNoRuleButton()
        {
            var row = new RowModel { FieldKey = "UDEF:ISO" };
            Assert.False(row.IsRuleRow);
            Assert.False(row.IsRuleButtonVisible);
            Assert.True(row.IsNormalDisplayMode);
        }

        // ── Legacy settings guard (Style Purger removed) ──

        [Fact]
        public void LegacySettings_WithStylePurgeSection_StillLoadPresets()
        {
            string dir = Path.Combine(Path.GetTempPath(), "CheckupT46_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Old deployed file: StylePurge section incl. a single-backslash path (invalid JSON
                // without NormalizeWindowsPaths) — must not break the preset load.
                File.WriteAllText(Path.Combine(dir, "Checkup_Settings.json"),
                    "{ \"StylePurge\": { \"TemplateFilePath\": \"V:\\CAD\\INV\\Templates\\Standard.idw\", \"BorderDefinitions\": [\"A4\"] },\n" +
                    "  \"Presets\": [ { \"Name\": \"Legacy\", \"FieldKeys\": [\"UDEF:ISO\", \"SPECIAL:RULE:\"] } ] }");

                var settings = UserSettings.Load(dir);

                Assert.Single(settings.Presets);
                Assert.Equal("Legacy", settings.Presets[0].Name);
                Assert.Equal(new[] { "UDEF:ISO", "SPECIAL:RULE:" }, settings.Presets[0].FieldKeys);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
