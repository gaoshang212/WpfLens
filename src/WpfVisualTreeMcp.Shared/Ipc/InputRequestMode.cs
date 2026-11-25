namespace WpfVisualTreeMcp.Shared.Ipc;

/// <summary>Identifies operations that deliver mouse or keyboard input or require foreground mode.</summary>
public static class InputRequestMode
{
    public static bool IsBackgroundAction(string action) =>
        action is "ClickElement" or "SetText" or "SendKeys" or "Drag" or "MousePointer";

    public static bool IsForegroundInputAction(string action) =>
        IsBackgroundAction(action) || action == "TouchGesture";

    public static bool UsesMouseOrKeyboard(IpcRequest request) => request switch
    {
        ClickElementRequest r => r.Physical || r.X.HasValue || r.Y.HasValue ||
            !string.IsNullOrEmpty(r.ClickType) && !string.Equals(r.ClickType, "single", StringComparison.OrdinalIgnoreCase),
        SetTextRequest r => r.Physical,
        SendKeysRequest or MousePointerRequest or DragRequest => true,
        _ => false
    };
}
