using CheckupAddIn.Models;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Characterization tests pinning the CURRENT behavior of RowModel's edit-mode
    /// state machine and value validation. These guard the kept-feature behavior across
    /// the legacy-field removal (MiterGap/Flange/Halbzeug/Spezi1-2). Pure logic only —
    /// no Inventor/COM, no ListCollectionView (so no STA requirement).
    /// </summary>
    public class RowModelCharacterizationTests
    {
        [Fact]
        public void NewRow_IsInDisplayMode_WithFieldSelectorVisible()
        {
            var row = new RowModel();

            Assert.True(row.IsDisplayMode);
            Assert.False(row.IsEditMode);
            Assert.True(row.IsFieldSelectorVisible);
        }

        [Fact]
        public void InlineEditing_SwitchesToEditMode_AndHidesFieldSelector()
        {
            var row = new RowModel { IsInlineEditing = true };

            Assert.False(row.IsDisplayMode);
            Assert.True(row.IsEditMode);
            Assert.False(row.IsFieldSelectorVisible);
        }

        [Theory]
        [InlineData("SPECIAL:LOGIC:demo", true)]
        [InlineData("SPECIAL:MiterGap", true)]   // a leftover legacy key in an old preset is still "special"...
        [InlineData("PARAM:User:Thickness", false)]
        [InlineData("UDEF:ISO", false)]
        [InlineData("", false)]
        public void IsSpecialRow_DetectsSpecialPrefixOnly(string key, bool expected)
        {
            var row = new RowModel { FieldKey = key };

            Assert.Equal(expected, row.IsSpecialRow);
        }

        [Fact]
        public void AllowedValues_DriveHasAllowedValues_AndValidation()
        {
            var row = new RowModel { IsInlineEditing = true };
            Assert.False(row.HasAllowedValues);

            row.AllowedValues = new[] { "A", "B" };
            Assert.True(row.HasAllowedValues);

            row.EditText = "A";
            Assert.True(row.IsEditValueValid);

            row.EditText = "C";
            Assert.False(row.IsEditValueValid);
        }

        [Fact]
        public void FreeTextRow_WithoutAllowedValues_IsAlwaysValid()
        {
            var row = new RowModel { IsInlineEditing = true };

            row.EditText = "anything";
            Assert.True(row.IsEditValueValid);
        }

        /// <summary>
        /// Task #29 graceful-degradation guard: an old preset may still carry a now-removed
        /// SPECIAL: field key (MiterGap/FlangeDistance/Halbzeug*/Spezi1-2). Such a row must
        /// render as a greyed/strikethrough "missing field" — never crash, and never
        /// re-activate a removed edit path (the IsMiterGapRow / IsHalbzeug* properties no
        /// longer exist, so this row falls through to plain display).
        /// </summary>
        [Theory]
        [InlineData("SPECIAL:MiterGap")]
        [InlineData("SPECIAL:FlangeDistance")]
        [InlineData("SPECIAL:Halbzeug")]
        [InlineData("SPECIAL:HalbzeugName")]
        [InlineData("SPECIAL:HalbzeugIdent")]
        [InlineData("SPECIAL:Spezi1")]
        [InlineData("SPECIAL:Spezi2")]
        public void LegacyRemovedSpecialKey_InOldPreset_DegradesGracefully(string legacyKey)
        {
            var row = new RowModel { FieldKey = legacyKey, IsFieldMissing = true };

            Assert.True(row.IsSpecialRow);        // still shows the red "S:" prefix
            Assert.True(row.IsFieldMissing);      // greyed + strikethrough label
            Assert.True(row.IsDisplayMode);       // no removed edit path is re-activated
            Assert.True(row.IsNormalDisplayMode); // plain display, no multi-token/mismatch branch
            Assert.False(row.IsEditMode);
        }
    }
}
