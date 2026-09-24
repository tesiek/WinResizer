using System;
using System.Collections.Generic;
using System.Linq;

namespace WinResizer.Common.Shortcuts;

public class Hotkeys
{
    public HashSet<string> ModifierKeys { get; set; } = new();

    public string? Key { get; set; }

    public void Clear()
    {
        ModifierKeys.Clear();
        Key = null;
    }

    public string ToKeysString() =>
        string.Join("+", GetAllKeys());

    public bool IsValid() =>
        this.ModifierKeys.Count > 0 && !string.IsNullOrEmpty(this.Key);

    public override bool Equals(object? obj) =>
        obj is Hotkeys other && this.GetAllKeys().SequenceEqual(other.GetAllKeys(), StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(ToKeysString());

    private IEnumerable<string> GetAllKeys()
    {
        var displayModifiers = ModifierKeys
            .Select(CanonicalizeModifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetModifierOrder)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrEmpty(Key))
        {
            displayModifiers.Add(Key!);
        }

        return displayModifiers.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string CanonicalizeModifier(string modifier)
    {
        if (modifier.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            modifier.Equals("LWin", StringComparison.OrdinalIgnoreCase) ||
            modifier.Equals("RWin", StringComparison.OrdinalIgnoreCase))
        {
            return "Win";
        }

        if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
        {
            return "Ctrl";
        }

        if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            return "Alt";
        }

        if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            return "Shift";
        }

        return modifier;
    }

    private static int GetModifierOrder(string value) =>
        value.Equals("Win", StringComparison.OrdinalIgnoreCase) ? 0 :
        value.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? 1 :
        value.Equals("Alt", StringComparison.OrdinalIgnoreCase) ? 2 :
        value.Equals("Shift", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
}
