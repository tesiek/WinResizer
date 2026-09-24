using System;

namespace WinResizer.Configuration;

public enum IgnoredWindowTitleMatch
{
    Any = 0,
    Exact = 1,
    Contains = 2,
    StartsWith = 3,
    EndsWith = 4,
}

public class IgnoredWindowRule
{
    public bool Active { get; set; } = true;

    public string Process { get; set; } = string.Empty;

    public string Class { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public IgnoredWindowTitleMatch TitleMatch { get; set; } = IgnoredWindowTitleMatch.Any;

    public bool IsUsable()
    {
        return Active
            && !string.IsNullOrWhiteSpace(Process)
            && (TitleMatch == IgnoredWindowTitleMatch.Any || !string.IsNullOrWhiteSpace(Title));
    }
}
