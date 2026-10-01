using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// T47 — Preset Buttons visual rework (TDD §10.5). Pure list rules only: load validation + migration,
    /// "+" copy naming, delete / reorder, import ID handling, export upsert, defaults and demo detection.
    /// Registry I/O, the Preset Bar layout (overflow / More) and drag-and-drop are verified manually in Inventor.
    /// </summary>
    public class PresetBarTests
    {
        private static PresetData P(string id, string name, bool isDemo = false, params string[] keys) =>
            new PresetData { Id = id, Name = name, IsDemo = isDemo, FieldKeys = keys.ToList() };

        private static PresetData UntouchedLegacyDemo() =>
            new PresetData { Name = PresetsManager.DemoPresetName, IsDemo = true, FieldKeys = PresetsManager.DemoDefaultFieldKeys.ToList() };

        // ── Load validation (registry data) ──

        [Fact]
        public void NormalizeLoaded_NullOrEmpty_ReturnsNull_SoDefaultsAreUsed()
        {
            Assert.Null(PresetsManager.NormalizeLoaded(null, out _));
            Assert.Null(PresetsManager.NormalizeLoaded(new List<PresetData>(), out _));
        }

        [Fact]
        public void NormalizeLoaded_SinglePresetWithId_IsKeptUnchanged()
        {
            var result = PresetsManager.NormalizeLoaded(new List<PresetData> { P("a", "Part") }, out bool changed);
            Assert.Single(result);
            Assert.Equal("a", result[0].Id);
            Assert.False(changed);
        }

        [Fact]
        public void NormalizeLoaded_TwelvePresets_Accepted_ThirteenTruncatedToTwelve()
        {
            var twelve = Enumerable.Range(1, 12).Select(i => P("id" + i, "P" + i)).ToList();
            var r12 = PresetsManager.NormalizeLoaded(twelve, out bool c12);
            Assert.Equal(12, r12.Count);
            Assert.False(c12);

            var thirteen = Enumerable.Range(1, 13).Select(i => P("id" + i, "P" + i)).ToList();
            var r13 = PresetsManager.NormalizeLoaded(thirteen, out bool c13);
            Assert.Equal(PresetsManager.MaxPresets, r13.Count);
            Assert.Equal("id12", r13[11].Id);   // the FIRST 12 are kept
            Assert.True(c13);
        }

        [Fact]
        public void NormalizeLoaded_EmptyAndDuplicateIds_AreRegenerated_UniqueAndSaved()
        {
            var list = new List<PresetData> { P("x", "A"), P("x", "B"), P("", "C") };
            var result = PresetsManager.NormalizeLoaded(list, out bool changed);
            Assert.True(changed);
            Assert.Equal("x", result[0].Id);
            Assert.Equal(3, result.Select(p => p.Id).Distinct().Count());
            Assert.All(result, p => Assert.False(string.IsNullOrEmpty(p.Id)));
        }

        // ── Migration (D11) ──

        [Fact]
        public void Migration_ThreeUntouchedLegacyDemos_CollapseToOneDemo()
        {
            var legacy = new List<PresetData> { UntouchedLegacyDemo(), UntouchedLegacyDemo(), UntouchedLegacyDemo() };
            var result = PresetsManager.NormalizeLoaded(legacy, out bool changed);
            Assert.True(changed);
            Assert.Single(result);
            Assert.Equal(PresetsManager.DemoPresetName, result[0].Name);
            Assert.False(string.IsNullOrEmpty(result[0].Id));
        }

        [Fact]
        public void Migration_ThreeUntouchedDemos_ByContentOnly_PreIsDemoData_AlsoCollapse()
        {
            var noFlag = new List<PresetData> { UntouchedLegacyDemo(), UntouchedLegacyDemo(), UntouchedLegacyDemo() };
            foreach (var p in noFlag) p.IsDemo = false;
            Assert.Single(PresetsManager.NormalizeLoaded(noFlag, out _));
        }

        [Fact]
        public void Migration_ThreeCustomisedLegacyPresets_AllKept_InOrder_WithNewIds()
        {
            var legacy = new List<PresetData> { UntouchedLegacyDemo(), P("", "Part", false, "IPROP|Description"), P("", "Assembly") };
            var result = PresetsManager.NormalizeLoaded(legacy, out bool changed);
            Assert.True(changed);
            Assert.Equal(new[] { "Demo", "Part", "Assembly" }, result.Select(p => p.Name));
            Assert.Equal(3, result.Select(p => p.Id).Distinct().Count());
        }

        [Fact]
        public void Migration_DoesNotCollapse_ThreeDemosThatAlreadyHaveIds()
        {
            // Post-T47 data: the user deliberately made three demo copies — not legacy, keep them.
            var list = new List<PresetData> { UntouchedLegacyDemo(), UntouchedLegacyDemo(), UntouchedLegacyDemo() };
            list[0].Id = "a"; list[1].Id = "b"; list[2].Id = "c";
            Assert.Equal(3, PresetsManager.NormalizeLoaded(list, out _).Count);
        }

        // ── Defaults / administrator presets (D14) ──

        [Fact]
        public void Defaults_AdminFileWithFivePresetsWithoutIds_GetsStableDeterministicIds()
        {
            var admin = Enumerable.Range(1, 5).Select(i => P("", "Company " + i)).ToList();
            var mgrA = new PresetsManager(admin);
            var mgrB = new PresetsManager(admin);
            var a = mgrA.GetDefaults();
            Assert.Equal(5, a.Count);
            Assert.Equal(new[] { "default-1", "default-2", "default-3", "default-4", "default-5" }, a.Select(p => p.Id));
            Assert.Equal(a.Select(p => p.Id), mgrB.GetDefaults().Select(p => p.Id));   // stable across window opens
        }

        [Fact]
        public void Defaults_MissingSettings_FallBackToOneEmptyDemo()
        {
            var d = new PresetsManager(null).GetDefaults();
            Assert.Single(d);
            Assert.Equal(PresetsManager.DemoPresetName, d[0].Name);
            Assert.Empty(d[0].FieldKeys);
        }

        [Fact]
        public void GetDefaults_CopiesEveryField_IncludingIdAndIsDemo_AsDeepCopy()
        {
            var mgr = new PresetsManager(new List<PresetData> { P("demo", "Demo", true, "IPROP|Description") });
            var first = mgr.GetDefaults();
            Assert.Equal("demo", first[0].Id);
            Assert.True(first[0].IsDemo);

            first[0].FieldKeys.Add("MUTATED");
            Assert.Single(mgr.GetDefaults()[0].FieldKeys);
        }

        // ── Demo detection (§5.1, any count) ──

        [Fact]
        public void IsUntouchedDemo_WorksWithOneAndWithSeveralPresets()
        {
            Assert.True(PresetsManager.IsUntouchedDemo(new List<PresetData> { P("a", "Demo", true) }));
            Assert.True(PresetsManager.IsUntouchedDemo(new List<PresetData> { P("a", "Demo", true), P("b", "Demo (2)", true) }));
            Assert.False(PresetsManager.IsUntouchedDemo(new List<PresetData> { P("a", "Demo", true), P("b", "Part", false) }));
            Assert.False(PresetsManager.IsUntouchedDemo(new List<PresetData>()));
        }

        // ── "+" copy (D4 / D5) ──

        [Theory]
        [InlineData("Demo",     new[] { "Demo" },                           "Demo (2)")]
        [InlineData("Demo",     new[] { "Demo", "Demo (2)", "Demo (4)" },   "Demo (3)")]   // gap filled
        [InlineData("Demo (2)", new[] { "Demo", "Demo (2)" },               "Demo (3)")]   // suffix stripped first
        [InlineData("Part",     new[] { "Part", "part (2)" },               "Part (3)")]   // user label collides (case-insensitive)
        public void MakeCopyName_SmallestFreeNumberFromTwo(string source, string[] existing, string expected)
            => Assert.Equal(expected, PresetsManager.MakeCopyName(source, existing));

        [Fact]
        public void CreateCopy_UsesLiveRows_NewId_UniqueName_InheritsIsDemo()
        {
            var active = P("demo", "Demo", true, "IPROP|Description");
            var copy = PresetsManager.CreateCopy(active, new[] { "IPROP|Description", "DOC:Material" }, new[] { "Demo" });
            Assert.NotEqual("demo", copy.Id);
            Assert.False(string.IsNullOrEmpty(copy.Id));
            Assert.Equal("Demo (2)", copy.Name);
            Assert.Equal(new[] { "IPROP|Description", "DOC:Material" }, copy.FieldKeys);   // live state, not the saved one
            Assert.True(copy.IsDemo);
        }

        [Fact]
        public void MaxPresets_IsTwelve() => Assert.Equal(12, PresetsManager.MaxPresets);

        // ── Delete (D7) ──

        [Theory]
        [InlineData(0, 0)]   // first deleted → new first
        [InlineData(1, 0)]   // → left neighbour
        [InlineData(5, 4)]
        public void IndexAfterDelete_LeftNeighbourOrNewFirst(int deleted, int expected)
            => Assert.Equal(expected, PresetsManager.IndexAfterDelete(deleted));

        // ── Reorder (D9) ──

        private static List<PresetData> Abcd() => new() { P("A", "A"), P("B", "B"), P("C", "C"), P("D", "D") };

        [Theory]
        [InlineData("A", 3, "BCAD")]   // A before D
        [InlineData("A", 4, "BCDA")]   // A to the end (drop onto More, D9 B)
        [InlineData("D", 1, "ADBC")]   // hidden D dragged in front of B
        [InlineData("C", 0, "CABD")]
        public void Move_InsertsBeforeTheElementAtInsertIndex(string id, int insertIndex, string expected)
        {
            var list = Abcd();
            Assert.True(PresetsManager.Move(list, list.FindIndex(p => p.Id == id), insertIndex));
            Assert.Equal(expected, string.Concat(list.Select(p => p.Id)));
        }

        [Theory]
        [InlineData("B", 1)]   // onto its own left edge
        [InlineData("B", 2)]   // onto its own right edge
        public void Move_ToOwnPosition_IsNoChange(string id, int insertIndex)
        {
            var list = Abcd();
            Assert.False(PresetsManager.Move(list, list.FindIndex(p => p.Id == id), insertIndex));
            Assert.Equal("ABCD", string.Concat(list.Select(p => p.Id)));
        }

        // ── Import (D12) ──

        [Fact]
        public void ResolveImportId_KeepsFileId_WhenFree()
            => Assert.Equal("file", PresetsManager.ResolveImportId("target", "file", new[] { "other" }));

        [Fact]
        public void ResolveImportId_TargetKeepsOwnId_WhenFileIdUsedByAnotherPreset()
            => Assert.Equal("target", PresetsManager.ResolveImportId("target", "other", new[] { "other" }));

        [Fact]
        public void ResolveImportId_TargetKeepsOwnId_WhenFileEntryHasNoId()
            => Assert.Equal("target", PresetsManager.ResolveImportId("target", "", new[] { "other" }));

        // ── Multi-import (D15) ──

        [Fact]
        public void FindImportConflict_IdMatchWinsOverNameMatch()
        {
            var existing = new List<PresetData> { P("a", "Part"), P("b", "Assembly") };
            Assert.Equal(1, PresetsManager.FindImportConflict(existing, P("b", "Part")));
        }

        [Fact]
        public void FindImportConflict_NameMatch_CaseInsensitive_WhenIdUnknownOrMissing()
        {
            var existing = new List<PresetData> { P("a", "Part") };
            Assert.Equal(0, PresetsManager.FindImportConflict(existing, P("zzz", "part")));
            Assert.Equal(0, PresetsManager.FindImportConflict(existing, P("", "PART")));
            Assert.Equal(-1, PresetsManager.FindImportConflict(existing, P("zzz", "Sheet")));
        }

        [Fact]
        public void CountGuaranteedNew_CountsOnlyEntriesWithoutConflict()
        {
            var existing = new List<PresetData> { P("a", "Part") };
            var incoming = new[] { P("a", "X"), P("q", "Part"), P("r", "New 1"), P("s", "New 2") };
            Assert.Equal(2, PresetsManager.CountGuaranteedNew(existing, incoming));
        }

        [Fact]
        public void ApplyMultiImport_AddsAtEnd_InOrder_KeepingFreeFileIds()
        {
            var list = new List<PresetData> { P("a", "Part") };
            var items = new List<(PresetData, string)> { (P("f1", "One", false, "K1"), null), (P("f2", "Two"), null) };
            Assert.True(PresetsManager.ApplyMultiImport(list, items, out _));
            Assert.Equal(new[] { "a", "f1", "f2" }, list.Select(p => p.Id));
            Assert.Equal(new[] { "K1" }, list[1].FieldKeys);
            Assert.All(list.Skip(1), p => Assert.False(p.IsDemo));
        }

        [Fact]
        public void ApplyMultiImport_AddAsNew_WithTakenId_GetsNewId_NoDuplicates()
        {
            var list = new List<PresetData> { P("a", "Part") };
            var items = new List<(PresetData, string)> { (P("a", "Part"), null) };   // user chose "Add as new"
            Assert.True(PresetsManager.ApplyMultiImport(list, items, out _));
            Assert.Equal(2, list.Count);
            Assert.Equal(2, list.Select(p => p.Id).Distinct().Count());
        }

        [Fact]
        public void ApplyMultiImport_Overwrite_UpdatesTargetInPlace_KeepsPosition()
        {
            var list = new List<PresetData> { P("a", "Part"), P("b", "Assembly", false, "OLD") };
            var items = new List<(PresetData, string)> { (P("b", "Assembly v2", false, "NEW"), "b") };
            Assert.True(PresetsManager.ApplyMultiImport(list, items, out var renamed));
            Assert.Equal(2, list.Count);
            Assert.Equal("Assembly v2", list[1].Name);
            Assert.Equal(new[] { "NEW" }, list[1].FieldKeys);
            Assert.Equal("b", list[1].Id);
            Assert.Empty(renamed);
        }

        [Fact]
        public void ApplyMultiImport_OverwriteByNameMatch_TakesFreeFileId_AndReportsRename()
        {
            var list = new List<PresetData> { P("a", "Part") };
            var items = new List<(PresetData, string)> { (P("file-id", "Part"), "a") };
            Assert.True(PresetsManager.ApplyMultiImport(list, items, out var renamed));
            Assert.Equal("file-id", list[0].Id);
            Assert.Equal("file-id", renamed["a"]);
        }

        [Fact]
        public void ApplyMultiImport_ExceedingTwelve_ChangesNothing()
        {
            var list = Enumerable.Range(1, 11).Select(i => P("id" + i, "P" + i)).ToList();
            var items = new List<(PresetData, string)> { (P("n1", "New 1"), null), (P("n2", "New 2"), null) };
            Assert.False(PresetsManager.ApplyMultiImport(list, items, out _));
            Assert.Equal(11, list.Count);
        }

        [Fact]
        public void ApplyMultiImport_OverwritesDoNotCountAgainstTheLimit()
        {
            var list = Enumerable.Range(1, 12).Select(i => P("id" + i, "P" + i)).ToList();
            var items = new List<(PresetData, string)> { (P("id3", "P3 updated"), "id3") };
            Assert.True(PresetsManager.ApplyMultiImport(list, items, out _));
            Assert.Equal(12, list.Count);
            Assert.Equal("P3 updated", list[2].Name);
        }

        // ── Export upsert (D13) ──

        [Fact]
        public void Upsert_MatchesById_EvenAfterRename()
        {
            var library = new List<PresetData> { P("id1", "Old name") };
            PresetsManager.UpsertIntoLibrary(library, P("id1", "New name"));
            Assert.Single(library);
            Assert.Equal("New name", library[0].Name);
        }

        [Fact]
        public void Upsert_OldFileEntryWithoutId_MatchedByName_AndGainsId()
        {
            var library = new List<PresetData> { P("", "Part") };
            PresetsManager.UpsertIntoLibrary(library, P("id1", "part"));
            Assert.Single(library);
            Assert.Equal("id1", library[0].Id);
        }

        [Fact]
        public void Upsert_SameNameButDifferentId_IsAppended()
        {
            var library = new List<PresetData> { P("id1", "Part") };
            PresetsManager.UpsertIntoLibrary(library, P("id2", "Part"));
            Assert.Equal(2, library.Count);
        }

        [Fact]
        public void Upsert_WritesACopy_NotTheLivePresetInstance()
        {
            var preset = P("id1", "Part", false, "IPROP|Description");
            var library = new List<PresetData>();
            PresetsManager.UpsertIntoLibrary(library, preset);
            preset.FieldKeys.Add("LATER");
            Assert.Single(library[0].FieldKeys);
        }
    }
}
