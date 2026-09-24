using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinResizer.Sorting;

public sealed class NaturalStringComparer : IComparer<string>, IComparer
{
    public static NaturalStringComparer Instance { get; } = new NaturalStringComparer();

    private NaturalStringComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x == null)
        {
            return -1;
        }

        if (y == null)
        {
            return 1;
        }

        try
        {
            return Normalize(NativeMethods.StrCmpLogicalW(x, y));
        }
        catch (DllNotFoundException)
        {
            return Normalize(StringComparer.OrdinalIgnoreCase.Compare(x, y));
        }
        catch (EntryPointNotFoundException)
        {
            return Normalize(StringComparer.OrdinalIgnoreCase.Compare(x, y));
        }
    }

    int IComparer.Compare(object? x, object? y) => Compare(x as string, y as string);

    private static int Normalize(int value) => value < 0 ? -1 : value > 0 ? 1 : 0;

    private static class NativeMethods
    {
        [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int StrCmpLogicalW(string? psz1, string? psz2);
    }
}
