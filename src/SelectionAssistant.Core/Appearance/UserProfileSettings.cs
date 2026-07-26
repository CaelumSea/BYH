namespace SelectionAssistant.Core.Appearance;

/// <summary>
/// User-facing identity used by decorative UI such as the settings phone card.
/// It is deliberately separate from Windows account identity and credentials.
/// </summary>
public sealed record UserProfileSettings
{
    public const int MaximumDisplayNameLength = 32;

    public string DisplayName { get; init; } = GetSystemDisplayName();

    public static UserProfileSettings Default { get; } = new();

    public UserProfileSettings Normalize() => this with
    {
        DisplayName = NormalizeDisplayName(DisplayName),
    };

    public void Validate()
    {
        string value = DisplayName?.Trim() ?? string.Empty;
        if (value.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"Display name must be {MaximumDisplayNameLength} characters or fewer.",
                nameof(DisplayName));
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Display name cannot contain line breaks or control characters.",
                nameof(DisplayName));
        }
    }

    private static string NormalizeDisplayName(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(trimmed) ? GetSystemDisplayName() : trimmed;
    }

    private static string GetSystemDisplayName()
    {
        string userName = Environment.UserName.Trim();
        return string.IsNullOrEmpty(userName) ? "there" : userName;
    }
}
