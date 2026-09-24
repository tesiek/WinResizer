using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WinResizer.Configuration;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Stores the new root preference as a readable string and treats unknown
/// values as System so older or hand-edited files remain usable.
/// </summary>
public sealed class ThemePreferenceJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(ThemePreference);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token.Type == JTokenType.String &&
            Enum.TryParse(token.Value<string>(), true, out ThemePreference textValue) &&
            Enum.IsDefined(typeof(ThemePreference), textValue))
        {
            return textValue;
        }

        if (token.Type == JTokenType.Integer)
        {
            try
            {
                var numericValue = (ThemePreference)Convert.ToInt32(token.Value<long>());
                if (Enum.IsDefined(typeof(ThemePreference), numericValue))
                {
                    return numericValue;
                }
            }
            catch (OverflowException)
            {
                // Unknown numeric values use the safe System default below.
            }
        }

        return ThemePreference.System;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        var preference = value is ThemePreference theme &&
                         Enum.IsDefined(typeof(ThemePreference), theme)
            ? theme
            : ThemePreference.System;
        writer.WriteValue(preference.ToString());
    }
}
