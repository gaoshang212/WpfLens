using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

internal sealed partial class ControlInteractor
{
    private const byte VK_RWIN = 0x5C;
    private const byte VK_F4 = 0x73;

    internal static (byte[] Modifiers, byte Key) ParseBackgroundKeys(string keys)
    {
        var combo = KeyComboParser.Parse(keys);
        if (Array.IndexOf(combo.Modifiers, NativeMethods.VK_LWIN) >= 0 ||
            Array.IndexOf(combo.Modifiers, VK_RWIN) >= 0 ||
            Array.IndexOf(combo.Modifiers, NativeMethods.VK_MENU) >= 0 &&
                combo.Key is NativeMethods.VK_TAB or VK_F4 or NativeMethods.VK_ESCAPE ||
            Array.IndexOf(combo.Modifiers, NativeMethods.VK_CONTROL) >= 0 && combo.Key == NativeMethods.VK_ESCAPE)
            throw new ArgumentException("System shortcuts are not supported by background input.");
        return combo;
    }

    internal static MouseDragPlan PrepareBackgroundPointer(UIElement element, double? x, double? y)
    {
        MousePointerOptions.ValidatePoint(x, y);
        var window = ValidateBackgroundElement(element);
        ScrollIntoView(element);
        var screen = element.PointToScreen(LocalPoint(element, x, y));
        EnsureBackgroundHit(element, screen);
        int sx = checked((int)Math.Round(screen.X)), sy = checked((int)Math.Round(screen.Y));
        return new MouseDragPlan(sx, sy, sx, sy, "left", 0, element.GetType().Name, window, window);
    }

    internal static IntPtr ValidateBackgroundElement(UIElement element)
    {
        EnsureInteractable(element, "used for background input");
        var window = Window.GetWindow(element);
        var source = PresentationSource.FromVisual(element) as HwndSource;
        if (window == null || window.WindowState == WindowState.Minimized || element is HwndHost ||
            source == null || source.Handle != new WindowInteropHelper(window).Handle ||
            window.Dispatcher != Application.Current.Dispatcher)
            throw new InvalidOperationException("Background input requires a non-minimized WPF window on the Inspector UI thread; native hosts and separate popup HWNDs are not supported.");
        return source.Handle;
    }

    private static void EnsureBackgroundHit(UIElement element, Point screen)
    {
        var root = PresentationSource.FromVisual(element)?.RootVisual as UIElement;
        var hit = root?.InputHitTest(root.PointFromScreen(screen)) as DependencyObject;
        bool matched = false;
        while (hit != null)
        {
            if (hit is HwndHost) throw new InvalidOperationException("Background input cannot target native hosted content.");
            if (ReferenceEquals(hit, element)) matched = true;
            hit = hit is Visual || hit is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        if (!matched)
            throw new InvalidOperationException("The input point is clipped or covered by another element. Make the input path visible first.");
    }

    internal static MouseDragPlan PrepareBackgroundDrag(UIElement source, UIElement? target, DragOptions options)
    {
        options.Validate();
        var window = ValidateBackgroundElement(source);
        if (target != null && ValidateBackgroundElement(target) != window)
            throw new InvalidOperationException("Background drag must stay within one WPF HWND.");
        if (target != null) ScrollIntoView(target);
        ScrollIntoView(source);
        var local = LocalPoint(source, options.StartX, options.StartY);
        var start = source.PointToScreen(local);
        var end = target != null ? target.PointToScreen(LocalPoint(target, options.TargetX, options.TargetY)) :
            source.PointToScreen(new Point(local.X + options.DeltaX.GetValueOrDefault(), local.Y + options.DeltaY.GetValueOrDefault()));
        EnsureBackgroundHit(source, start);
        if (target != null) EnsureBackgroundHit(target, end);
        if (start == end) throw new ArgumentException("Drag endpoints must differ.");
        return new MouseDragPlan(checked((int)Math.Round(start.X)), checked((int)Math.Round(start.Y)),
            checked((int)Math.Round(end.X)), checked((int)Math.Round(end.Y)), options.Button, options.DurationMs,
            source.GetType().Name, window, window);
    }

    internal static bool FocusBackgroundElement(UIElement element)
    {
        Keyboard.Focus(element);
        return element.IsKeyboardFocused || element.IsKeyboardFocusWithin;
    }

    internal static string ReadBackgroundText(UIElement element, string expected)
    {
        if (element is PasswordBox password)
            return password.Password == expected ? "Password read-back matches (redacted)." : "Password read-back differs (redacted).";
        var value = element.GetType().GetProperty("Text")?.GetValue(element) as string;
        return value == null ? "Text input delivered; this control does not expose a readable Text property." :
            $"Read-back: '{value}' (matches requested text: {value == expected}).";
    }
}
