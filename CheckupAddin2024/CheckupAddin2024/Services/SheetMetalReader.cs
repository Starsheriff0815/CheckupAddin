using Inventor;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Reads sheet metal specific values: 2nd flange, miter gap, flange distance.
    /// </summary>
    public class SheetMetalReader
    {
        /// <summary>
        /// Finds the 2nd FlangeFeature in the sheet metal feature list.
        /// </summary>
        public FlangeFeature FindSecondFlange(PartDocument part)
        {
            if (part?.ComponentDefinition is not SheetMetalComponentDefinition smDef)
                return null;

            int count = 0;
            foreach (PartFeature feat in smDef.Features)
            {
                if (feat is FlangeFeature flange)
                {
                    count++;
                    if (count == 2) return flange;
                }
            }
            return null;
        }

        /// <summary>
        /// Reads the miter gap value in cm from a FlangeDefinition.
        /// Uses late-bound access to avoid interop version issues.
        /// </summary>
        public double ReadMiterGapCm(FlangeDefinition def)
        {
            // Late-bound: def.MiterGap.Value
            var gapObj = Microsoft.VisualBasic.Interaction.CallByName(
                def, "MiterGap", Microsoft.VisualBasic.CallType.Get);
            return (double)Microsoft.VisualBasic.Interaction.CallByName(
                gapObj, "Value", Microsoft.VisualBasic.CallType.Get);
        }

        /// <summary>
        /// Reads the flange extent distance in cm from a FlangeDefinition.
        /// Uses late-bound access to avoid interop version issues.
        /// </summary>
        public double ReadFlangeDistanceCm(FlangeDefinition def)
        {
            // Late-bound: def.HeightExtent.Distance.Value
            var extentObj = Microsoft.VisualBasic.Interaction.CallByName(
                def, "HeightExtent", Microsoft.VisualBasic.CallType.Get);
            var distParam = Microsoft.VisualBasic.Interaction.CallByName(
                extentObj, "Distance", Microsoft.VisualBasic.CallType.Get);
            return (double)Microsoft.VisualBasic.Interaction.CallByName(
                distParam, "Value", Microsoft.VisualBasic.CallType.Get);
        }

        /// <summary>
        /// Converts a value in cm to the document's display length unit, formatted with 3 decimals.
        /// </summary>
        public string CmToDisplayString(double cm, PartDocument doc)
        {
            var uom = doc.UnitsOfMeasure;
            var lenUnit = uom.LengthUnits;
            double display = uom.ConvertUnits(cm, UnitsTypeEnum.kCentimeterLengthUnits, lenUnit);
            string unitStr = SheetMetalReader.UnitAbbreviation(lenUnit);
            return $"{display:0.000} {unitStr}";
        }

        /// <summary>
        /// Returns the unit abbreviation string for a given length unit enum.
        /// </summary>
        public static string UnitAbbreviation(UnitsTypeEnum unit)
        {
            return unit switch
            {
                UnitsTypeEnum.kMillimeterLengthUnits => "mm",
                UnitsTypeEnum.kCentimeterLengthUnits => "cm",
                UnitsTypeEnum.kMeterLengthUnits => "m",
                UnitsTypeEnum.kInchLengthUnits => "in",
                UnitsTypeEnum.kFootLengthUnits => "ft",
                _ => "?"
            };
        }
    }
}