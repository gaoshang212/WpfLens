namespace WpfVisualTreeMcp.Shared.Models;

/// <summary>Delivery mode for mouse and keyboard input. UI Automation remains the default for click/text.</summary>
public static class InputModes
{
    public const string Foreground = "foreground";
    public const string Background = "background";

    public static void Validate(string mode)
    {
        if (mode is not (Foreground or Background))
            throw new ArgumentException("Mode must be 'foreground' or 'background'.");
    }
}
