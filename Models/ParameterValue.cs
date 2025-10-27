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
                        paramValue.DisplayValue = "(unset)";
                    }
                    break;

                case Autodesk.Revit.DB.StorageType.Double:
                    var doubleVal = param.AsDouble();
                    paramValue.RawValue = doubleVal;
                    paramValue.DisplayValue = param.AsValueString() ?? doubleVal.ToString();
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
                            paramValue.RawValue = rawValueToken.Value<string>() ?? "";
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
                    // Normalize -1 and empty string as equivalent (both mean "no value")
                    string thisVal = RawValue?.ToString() ?? "";
                    string otherVal = other.RawValue?.ToString() ?? "";
                    if ((thisVal == "-1" || thisVal == "") && (otherVal == "-1" || otherVal == ""))
                        return true;
                    return thisVal == otherVal;

                default:
                    return RawValue?.ToString() == other.RawValue?.ToString();
            }
        }
    }
}
