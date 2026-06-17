namespace CheckupAddIn.Models
{
    public sealed class SpeziAutoCompleteItem
    {
        public string Short { get; }
        public string Long  { get; }

        public SpeziAutoCompleteItem(string shortVal, string longVal)
        {
            Short = shortVal ?? "";
            Long  = longVal  ?? "";
        }
    }
}
