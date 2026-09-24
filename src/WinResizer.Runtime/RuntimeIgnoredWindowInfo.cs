using WinResizer.Configuration;

namespace WinResizer.Runtime;

/// <summary>A detached, read-only projection of a global ignored-window rule.</summary>
public sealed class RuntimeIgnoredWindowInfo
{
    internal RuntimeIgnoredWindowInfo(string token, IgnoredWindowRule rule)
    {
        IgnoredWindowToken = token;
        Active = rule.Active;
        Process = rule.Process;
        Class = rule.Class;
        Title = rule.Title;
        TitleMatch = rule.TitleMatch;
    }

    public string IgnoredWindowToken { get; }
    public bool Active { get; }
    public string Process { get; }
    public string Class { get; }
    public string Title { get; }
    public IgnoredWindowTitleMatch TitleMatch { get; }
}
