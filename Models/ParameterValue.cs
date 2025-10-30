using System;
using Newtonsoft.Json.Linq;

namespace ViewTracker.Models
{
    /// <summary>
    /// Represents a parameter value with type metadata for accurate comparison.
    /// This replaces the old approach of storing raw values without type information.
    /// </summary>
    public class ParameterValue
    {
        /// <summary>
        /// The storage type of the parameter (String, Integer, Double, ElementId)
        /// </summary>
        public string StorageType { get; set; }

        /// <summary>
        /// The raw value for comparison (strongly typed)
        /// - String: the actual string
        /// - Integer: the integer value (even if it has display text)
        /// - Double: the double value
        /// - ElementId: the display name string
        /// </summary>
        public object RawValue { get; set; }

        /// <summary>
        /// The display-friendly value for UI (always a string)
        /// </summary>
        public string DisplayValue { get; set; }

        /// <summary>
        /// Whether this is a type parameter (vs instance parameter)
        /// </summary>
        public bool IsTypeParameter { get; set; }

        /// <summary>
        /// Creates a ParameterValue from a Revit Parameter
        /// </summary>
        public static ParameterValue FromRevitParameter(Autodesk.Revit.DB.Parameter param)
        {
            if (param == null)
                return null;

            var paramValue = new ParameterValue
            {
                StorageType = param.StorageType.ToString(),
                IsTypeParameter = param.Element is Autodesk.Revit.DB.ElementType
            };

            switch (param.StorageType)
            {
                case Autodesk.Revit.DB.StorageType.String:
                    var stringVal = param.AsString() ?? "";
                    paramValue.RawValue = stringVal;
                    paramValue.DisplayValue = stringVal;
                    break;

                case Autodesk.Revit.DB.StorageType.Integer:
                    // Check if parameter has a value (not gray/unset)
                    // For shared parameters, also check if AsValueString is empty
                    var valueString = param.AsValueString();
                    if (param.HasValue && !string.IsNullOrEmpty(valueString))
                    {
                        var intVal = param.AsInteger();
                        var displayText = valueString;

                        paramValue.RawValue = intVal;  // Store the integer
                        paramValue.DisplayValue = !string.IsNullOrEmpty(displayText)
                            ? displayText  // Use enum display text if available
                            : intVal.ToString();
                    }
                    else
                    {
                        // Parameter is unset (gray state for booleans/integers)
                        paramValue.RawValue = null;
                        paramValue.DisplayValue = "";  // Empty string for better Excel export/import
                    }
                    break;

                case Autodesk.Revit.DB.StorageType.Double:
                    var doubleVal = param.AsDouble();
                    paramValue.RawValue = doubleVal;
                    // Format with minimum 2 decimal places to show differences clearly
                    try
                    {
                        var doc = param.Element.Document;
                        var units = doc.GetUnits();
                        var dataType = param.Definition.GetDataType();

                        // Use FormatValueOptions to ensure units are shown
                        var formatOptions = new Autodesk.Revit.DB.FormatValueOptions();
                        formatOptions.AppendUnitSymbol = true;

                        paramValue.DisplayValue = Autodesk.Revit.DB.UnitFormatUtils.Format(
                            units,
                            dataType,
                            doubleVal,
                            false,
                            formatOptions);
                    }
                    catch
                    {
                        // Fallback if formatting fails
                        paramValue.DisplayValue = param.AsValueString() ?? doubleVal.ToString("F2");
                    }
                    break;

                case Autodesk.Revit.DB.StorageType.ElementId:
                    // BUGFIX: Always store the numeric ElementId value, not the display text
                    // This allows proper restoration of key schedule references
                    var elemId = param.AsElementId();
                    var elemIdValue = elemId.Value;
                    var elemDisplayText = param.AsValueString();

                    // Store the numeric ID for restoration
                    paramValue.RawValue = elemIdValue;
                    // Store the display text for comparison UI
                    paramValue.DisplayValue = !string.IsNullOrEmpty(elemDisplayText)
                        ? elemDisplayText
                        : elemIdValue.ToString();
                    break;
            }

            return paramValue;
        }

        /// <summary>
        /// Creates a ParameterValue from a JSON object (used when deserializing from Supabase)
        /// </summary>
        public static ParameterValue FromJsonObject(object jsonObj)
        {
            if (jsonObj == null)
                return null;

            // If it's already a ParameterValue, return it
            if (jsonObj is ParameterValue pv)
                return pv;

            // If it's a JObject (Newtonsoft.Json), convert it
            if (jsonObj is JObject jObj)
            {
                var paramValue = new ParameterValue
                {
                    StorageType = jObj["StorageType"]?.ToString(),
                    DisplayValue = jObj["DisplayValue"]?.ToString(),
                    IsTypeParameter = jObj["IsTypeParameter"]?.Value<bool>() ?? false
                };

                // Convert RawValue based on StorageType
                var rawValueToken = jObj["RawValue"];
                if (rawValueToken != null && rawValueToken.Type != JTokenType.Null)
                {
                    switch (paramValue.StorageType)
                    {
                        case "String":
                            paramValue.RawValue = rawValueToken.Value<string>() ?? "";
                            break;
                        case "Integer":
                            paramValue.RawValue = rawValueToken.Value<int>();
                            break;
                        case "Double":
                            paramValue.RawValue = rawValueToken.Value<double>();
                            break;
                        case "ElementId":
                            // BUGFIX: Try to parse as long, but handle string values for backwards compatibility
                            if (rawValueToken.Type == JTokenType.Integer)
                            {
                                paramValue.RawValue = rawValueToken.Value<long>();
                            }
                            else if (rawValueToken.Type == JTokenType.String)
                            {
                                var strValue = rawValueToken.Value<string>();
                                // Try to parse as long, fallback to -1 if it's a display string
                                if (long.TryParse(strValue, out long parsedId))
                                {
                                    paramValue.RawValue = parsedId;
                                }
                                else
                                {
                                    // It's a display string (e.g. "485 - NIVEAU 1"), store as -1 and use DisplayValue for comparison
                                    paramValue.RawValue = -1;
                                    paramValue.DisplayValue = strValue;
                                }
                            }
                            else
                            {
                                paramValue.RawValue = -1; // Default to "no value"
                            }
                            break;
                        default:
                            paramValue.RawValue = rawValueToken.ToString();
                            break;
                    }
                }
                else
                {
                    // RawValue is null in the JSON (unset parameter)
                    paramValue.RawValue = null;
                }

                return paramValue;
            }

            // Fallback: log warning and return null
            System.Diagnostics.Debug.WriteLine($"WARNING: Unable to convert JSON object of type {jsonObj.GetType().Name} to ParameterValue");
            return null;
        }

        /// <summary>
        /// Compares two ParameterValues for equality
        /// </summary>
        public bool IsEqualTo(ParameterValue other, double doubleTolerance = 0.001)
        {
            if (other == null)
                return false;

            // Type mismatch is always different
            if (StorageType != other.StorageType)
                return false;

            switch (StorageType)
            {
                case "String":
                    return (string)RawValue == (string)other.RawValue;

                case "Integer":
                    // Handle null values (unset booleans/integers)
                    if (RawValue == null && other.RawValue == null)
                        return true;
                    if (RawValue == null || other.RawValue == null)
                        return false;
                    return Convert.ToInt32(RawValue) == Convert.ToInt32(other.RawValue);

                case "Double":
                    // Handle null values
                    if (RawValue == null && other.RawValue == null)
                        return true;
                    if (RawValue == null || other.RawValue == null)
                        return false;
                    var thisDouble = Convert.ToDouble(RawValue);
                    var otherDouble = Convert.ToDouble(other.RawValue);
                    return Math.Abs(thisDouble - otherDouble) <= doubleTolerance;

                case "ElementId":
                    // Normalize -1 and 0 as equivalent (both mean "no value")
                    // Handle both numeric and string values (for backwards compatibility)
                    long thisId = -1;
                    long otherId = -1;

                    if (RawValue != null)
                    {
                        if (RawValue is long l1)
                            thisId = l1;
                        else if (RawValue is int i1)
                            thisId = i1;
                        else if (long.TryParse(RawValue.ToString(), out long parsed1))
                            thisId = parsed1;
                        // If parsing fails (display string like "485 - NIVEAU 1"), compare as DisplayValue
                        else
                            return (DisplayValue ?? "") == (other.DisplayValue ?? "");
                    }

                    if (other.RawValue != null)
                    {
                        if (other.RawValue is long l2)
                            otherId = l2;
                        else if (other.RawValue is int i2)
                            otherId = i2;
                        else if (long.TryParse(other.RawValue.ToString(), out long parsed2))
                            otherId = parsed2;
                        // If parsing fails, compare as DisplayValue
                        else
                            return (DisplayValue ?? "") == (other.DisplayValue ?? "");
                    }

                    if ((thisId == -1 || thisId == 0) && (otherId == -1 || otherId == 0))
                        return true;
                    return thisId == otherId;

                default:
                    return RawValue?.ToString() == other.RawValue?.ToString();
            }
        }
    }
}
