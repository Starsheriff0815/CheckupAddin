using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// T48 — Window placement + Reset coverage (TDD §10.6). Pure rules only: which registry value names
    /// Reset clears, and the stored placement format. Win32 placement/centering and the monitor check
    /// are verified manually in Inventor.
    /// </summary>
    public class WindowPlacementTests
    {
        // ── Reset coverage (D1) ──

        [Theory]
        [InlineData("WindowWidth")]
        [InlineData("WindowHeight")]
        [InlineData("WindowPlacement")]
        [InlineData("FieldSelectorPopupWidth")]
        [InlineData("FieldSelectorPopupHeight")]
        [InlineData("CatalogBuilderWidth")]
        [InlineData("CatalogBuilderHeight")]
        [InlineData("CatalogBuilderPlacement")]
        [InlineData("CatalogPickerWidth")]
        [InlineData("CatalogPickerHeight")]
        [InlineData("InfoDialog_MainAddin_Width")]
        [InlineData("InfoDialog_RoleHelp_Height")]      // pre-T48 bug: never reset
        [InlineData("InfoDialog_CardHelp_Width")]       // pre-T48 bug: never reset
        [InlineData("InfoDialog_PresetPicker_Width")]
        [InlineData("InfoDialog_PresetConflict_Height")]
        [InlineData("InfoDialog_DeletePreset_Width")]
        [InlineData("LogicDropdown_spezi-g1_H")]
        [InlineData("LogicDropdown_spezi-g1_Cols")]
        public void IsWindowSizeValueName_SizeAndPlacementValues_AreCleared(string name)
            => Assert.True(UiStateStore.IsWindowSizeValueName(name));

        [Theory]
        [InlineData("ActivePresetId")]
        [InlineData("CatalogPickerTab_demo")]
        [InlineData("CatBuilderActiveTab")]
        [InlineData("CatBuilderBasicLogicsOpen")]
        [InlineData("CatBuilderCardPanelOpen")]
        [InlineData("LastCatalogId")]
        [InlineData("LastCapabilitySetId")]
        [InlineData("FieldSelPinnedFields")]
        [InlineData("GroupCollapsed_g1")]
        [InlineData("CardCollapsed_g1_0")]
        [InlineData("FileNameViewMode")]
        [InlineData("")]
        [InlineData(null)]
        public void IsWindowSizeValueName_OtherUiState_IsKept(string name)
            => Assert.False(UiStateStore.IsWindowSizeValueName(name));

        // ── Stored placement format "l,t,r,b,max" ──

        [Theory]
        [InlineData("100,50,750,950,0")]
        [InlineData("-1920,0,-420,1100,1")]   // monitor left of the primary
        [InlineData(" 100 , 50 , 750 , 950 , 0 ")]
        public void IsWellFormed_ValidPlacement(string data)
            => Assert.True(WindowPlacement.IsWellFormed(data));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("100,50,750,950")]          // missing maximized flag
        [InlineData("100,50,750,950,0,1")]      // too many parts
        [InlineData("a,50,750,950,0")]
        [InlineData("100,50,150,950,0")]        // 50 px wide
        [InlineData("100,50,750,60,0")]         // 10 px high
        [InlineData("750,50,100,950,0")]        // inverted
        public void IsWellFormed_InvalidPlacement(string data)
            => Assert.False(WindowPlacement.IsWellFormed(data));

        [Fact]
        public void IsMaximized_ReadsFlag()
        {
            Assert.True(WindowPlacement.IsMaximized("0,0,1500,1100,1"));
            Assert.False(WindowPlacement.IsMaximized("0,0,1500,1100,0"));
            Assert.False(WindowPlacement.IsMaximized("garbage"));
        }
    }
}
