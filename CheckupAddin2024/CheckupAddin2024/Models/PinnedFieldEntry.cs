namespace CheckupAddIn.Models
{
    public sealed class PinnedFieldEntry
    {
        public FieldItem Item        { get; set; }
        public bool      IsAvailable { get; set; }

        public string Key      => Item?.Key     ?? "";
        public string DropText => Item?.DropText ?? "";
    }
}
