using System;
using System.Collections.Generic;
using System.Linq;
using WinResizer.Configuration;

namespace WinResizer.Base;

public static class IgnoredWindowMatcher
{
    public static bool IsIgnoredBeforeTitle(
        IEnumerable<IgnoredWindowRule>? rules,
        string processName,
        Func<string?> getClassName)
    {
        return IsIgnoredCore(rules, processName, getClassName, null, false);
    }

    public static bool IsIgnoredForAutoResize(
        IEnumerable<IgnoredWindowRule>? rules,
        string processName,
        Func<string?> getClassName,
        Func<string?> getTitle)
    {
        return IsIgnoredCore(rules, processName, getClassName, getTitle() ?? string.Empty, true);
    }

    public static bool IsIgnoredForAutoResize(
        IEnumerable<IgnoredWindowRule>? rules,
        string processName,
        Func<string?> getClassName,
        string? windowTitle)
    {
        return IsIgnoredCore(rules, processName, getClassName, windowTitle, true);
    }

    private static bool IsIgnoredCore(
        IEnumerable<IgnoredWindowRule>? rules,
        string processName,
        Func<string?> getClassName,
        string? windowTitle,
        bool titleReady)
    {
        if (rules is null || string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var processRules = rules
            .Where(rule => rule is not null
                && rule.IsUsable()
                && rule.Process.Equals(processName, StringComparison.OrdinalIgnoreCase)
                && (titleReady || rule.TitleMatch == IgnoredWindowTitleMatch.Any))
            .ToList();

        if (processRules.Count == 0)
        {
            return false;
        }

        string? className = null;
        var classLoaded = false;

        foreach (var rule in processRules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Class))
            {
                if (!classLoaded)
                {
                    className = getClassName() ?? string.Empty;
                    classLoaded = true;
                }

                if (!rule.Class.Equals(className, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (rule.TitleMatch != IgnoredWindowTitleMatch.Any)
            {
                if (!titleReady)
                {
                    continue;
                }

                if (!MatchesTitle(rule, windowTitle ?? string.Empty))
                {
                    continue;
                }
            }

            return true;
        }

        return false;
    }

    private static bool MatchesTitle(IgnoredWindowRule rule, string title)
    {
        if (string.IsNullOrWhiteSpace(rule.Title))
        {
            return false;
        }

        return rule.TitleMatch switch
        {
            IgnoredWindowTitleMatch.Exact => title.Equals(rule.Title, StringComparison.OrdinalIgnoreCase),
            IgnoredWindowTitleMatch.Contains => title.IndexOf(rule.Title, StringComparison.OrdinalIgnoreCase) >= 0,
            IgnoredWindowTitleMatch.StartsWith => title.StartsWith(rule.Title, StringComparison.OrdinalIgnoreCase),
            IgnoredWindowTitleMatch.EndsWith => title.EndsWith(rule.Title, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
