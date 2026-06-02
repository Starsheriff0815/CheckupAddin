namespace CheckupAddIn.Models
{
    /// <summary>
    /// One entry in the Field Selector Favoriten (sticky) zone.
    /// Wraps a FieldItem with an availability flag for strikethrough rendering.
    /// </summary>
    public sealed class PinnedFieldEntry
    {
        public FieldItem Item        { get; init; }
        /// <summary>False when the field key is not present in the current document's catalog — renders as strikethrough.</summary>
        public bool      IsAvailable { get; init; }

        public string Key          => Item?.Key          ?? "";
        public string DropText    => Item?.DropText    ?? "";
        public bool   IsSpecialEntry => Item?.IsSpecialEntry ?? false;
    }
}
