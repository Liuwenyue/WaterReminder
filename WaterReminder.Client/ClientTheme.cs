namespace WaterReminder.Client;

/// <summary>Loads the same light/dark dictionaries the original desktop window uses.</summary>
internal static class ClientTheme
{
    public static void Ensure() => SharedTheme.Ensure();
}
