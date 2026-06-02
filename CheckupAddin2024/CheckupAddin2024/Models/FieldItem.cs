using System;
using System.Collections.Generic;

namespace CheckupAddIn.Models
{
    /// <summary>
    /// Immutable descriptor for one selectable field in the row dropdown.
    /// Instances are created by FieldCatalogBuilder and shared across all rows.
    /// </summary>
    /// <remarks>
    /// Field key prefixes (used throughout the codebase):
    ///   SPECIAL:MiterGap / SPECIAL:FlangeDistance  — computed sheet metal values
    ///   DOC:&lt;tag&gt;                                  — document-level values (Material, Appearance, …)
    ///   IPROP|&lt;setName&gt;|&lt;propName&gt;                  — standard iProperties (set identified by internal COM name)
    ///   UDEF:&lt;propName&gt;                            — user-defined iProperties (custom set, any language)
    ///   PARAM:User:&lt;name&gt;                          — Inventor user parameters
    ///   PARAM:Model:&lt;name&gt;                         — Inventor model (sketch/feature) parameters
    /// </remarks>
    public class FieldItem
    {
        /// <summary>Stable identifier used for read/write routing — see prefix conventions above.</summary>
        public string Key         { get; }

        /// <summary>Text shown inside the dropdown list.</summary>
        public string DropText    { get; }

        /// <summary>Text shown in the row label column after a field is selected.</summary>
        public string RowLabel    { get; }

        /// <summary>Group header in the dropdown (maps to GroupedFieldCatalog grouping).</summary>
        public string GroupName   { get; }

        /// <summary>True if FieldWriter can write this field back to Inventor.</summary>
        public bool   IsWritable  { get; }

        /// <summary>
        /// True for synthetic "Add Row" / "Remove Row" entries at the top of the dropdown.
        /// These trigger commands in CheckupWindow code-behind and are never stored as field keys.
        /// </summary>
        public bool   IsActionItem { get; }

        /// <summary>
        /// Optional fixed set of valid values for this field.
        /// Non-empty for DOC:Material, DOC:Appearance, and key-value user parameters.
        /// Empty for free-text fields (most iProperties, numeric parameters).
        /// </summary>
        public IReadOnlyList<string> AllowedValues { get; }

        public bool IsSpecialEntry => Key.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal);

        public FieldItem(string key, string dropText, string rowLabel,
                         string groupName = "", bool isWritable = false, bool isActionItem = false,
                         IReadOnlyList<string> allowedValues = null)
        {
            Key           = key          ?? "";
            DropText      = dropText     ?? "";
            RowLabel      = rowLabel     ?? "";
            GroupName     = groupName    ?? "";
            IsWritable    = isWritable;
            IsActionItem  = isActionItem;
            AllowedValues = allowedValues ?? System.Array.Empty<string>();
        }

        public override string ToString() => DropText;
    }
}
