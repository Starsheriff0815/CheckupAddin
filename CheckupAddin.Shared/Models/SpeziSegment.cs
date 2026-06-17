namespace CheckupAddIn.Models
{
    public sealed class SpeziSegment
    {
        public string Token      { get; }
        public bool   IsValid    { get; }
        public string Separator  { get; }

        public SpeziSegment(string text, bool isValid, string separator)
        {
            Token     = text      ?? "";
            IsValid   = isValid;
            Separator = separator ?? "";
        }
    }
}
