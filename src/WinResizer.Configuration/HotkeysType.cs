namespace WinResizer.Configuration;

public enum HotkeysType
{
    Save = 1,
    Restore = 2,
    SaveAll = 3,
    RestoreAll = 4,
}

public static class HotkeysTypeExtensions
{
    public static string ToDisplayName(this HotkeysType type)
    {
        var name = type.ToString();
        if (name.Length < 2)
        {
            return name;
        }

        var displayName = new System.Text.StringBuilder(name.Length + 4);
        displayName.Append(name[0]);
        for (var index = 1; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && char.IsLower(name[index - 1]))
            {
                displayName.Append(' ');
            }

            displayName.Append(name[index]);
        }

        return displayName.ToString();
    }
}
