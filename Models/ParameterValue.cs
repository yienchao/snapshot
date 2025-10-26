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
                    var intVal = param.AsInteger();
                    var displayText = param.AsValueString();

                    paramValue.RawValue = intVal;  // Always store the integer
                    paramValue.DisplayValue = !string.IsNullOrEmpty(displayText)
                        ? displayText  // Use enum display text if available
                        : intVal.ToString();
                    break;

                case Autodesk.Revit.DB.StorageType.Double:
                    var doubleVal = param.AsDouble();
                    paramValue.RawValue = doubleVal;
                    paramValue.DisplayValue = param.AsValueString() ?? doubleVal.ToString();
                    break;

                case Autodesk.Revit.DB.StorageType.ElementId:
                    var elemDisplayText = param.AsValueString();
                    if (!string.IsNullOrEmpty(elemDisplayText))
                    {
                        paramValue.RawValue = elemDisplayText;  // Store display name
                        paramValue.DisplayValue = elemDisplayText;
                    }
                    else
                    {
                        var elemIdVal = param.AsElementId().Value;
                        paramValue.RawValue = elemIdVal.ToString();
                        paramValue.DisplayValue = elemIdVal.ToString();
                    }
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
                if (rawValueToken != null)
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
                    return Convert.ToInt32(RawValue) == Convert.ToInt32(other.RawValue);

                case "Double":
                    var thisDouble = Convert.ToDouble(RawValue);
                    var otherDouble = Convert.ToDouble(other.RawValue);
                    return Math.Abs(thisDouble - otherDouble) <= doubleTolerance;

                case "ElementId":
                    return (string)RawValue == (string)other.RawValue;

                default:
                    return RawValue?.ToString() == other.RawValue?.ToString();
            }
        }
    }
}
