using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Gosf.Input.Automation;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>Persistent Gosf binding. Never execute native input on the target Dispatcher.</summary>
internal sealed class BackgroundInput : IBackgroundInput
{
    private readonly InputSession _session = new();
    public IntPtr Window { get; }

    public BackgroundInput(IntPtr window)
    {
        Window = window;
        try
        {
            VerifyForeground();
        }
        catch { _session.Dispose(); throw; }
    }

    public void Bind() => Check(_session.BindWindow(Window, MouseMode.DirectInput, KeyboardMode.DirectInput), "bind");

    public void VerifyForeground()
    {
        if (!IsWindow(Window) || !IsWindowVisible(Window) || IsIconic(Window))
            throw new InvalidOperationException("Background input requires a visible, non-minimized WPF window.");
    }

    public void ValidateStart(MouseDragPlan plan)
    {
        VerifyForeground();
        if (plan.SourceWindow != Window || plan.TargetWindow != IntPtr.Zero && plan.TargetWindow != Window)
            throw new InvalidOperationException("Background drag must stay within one WPF HWND.");
        ToClient(plan.StartX, plan.StartY);
        ToClient(plan.EndX, plan.EndY);
    }

    private POINT ToClient(int x, int y)
    {
        using var coordinates = new PhysicalInputGuard.PhysicalCoordinates();
        var point = new POINT { X = x, Y = y };
        if (!ScreenToClient(Window, ref point) || !GetClientRect(Window, out var bounds))
            throw new InvalidOperationException("Cannot resolve background client coordinates.");
        if (point.X < 0 || point.Y < 0 || point.X >= bounds.Right || point.Y >= bounds.Bottom ||
            point.X > short.MaxValue || point.Y > short.MaxValue)
            throw new ArgumentException("Background input must stay inside the bound client area.");
        return point;
    }

    public void Move(int x, int y)
    {
        VerifyForeground();
        var point = ToClient(x, y);
        Check(_session.MoveTo(point.X, point.Y), "move");
    }

    public void Press(string button) { VerifyForeground(); Check(_session.MouseDown(ParseButton(button)), "mouse down"); }
    public void Release(string button) => Check(_session.MouseUp(ParseButton(button)), "mouse up");

    public void Click(string kind)
    {
        var button = kind == "right" ? "right" : "left";
        try
        {
            if (kind == "double") Check(_session.DoubleClick(ParseButton(button)), "double click");
            else Press(button);
        }
        finally { Release(button); }
    }

    public void Wheel(int delta, bool horizontal)
    {
        VerifyForeground();
        Check(horizontal ? _session.ScrollHorizontal(delta) : _session.Scroll(delta), "wheel");
    }

    public void SendKeys(byte[] modifiers, byte key)
    {
        var held = new List<byte>();
        try
        {
            foreach (var modifier in modifiers) { held.Add(modifier); Check(_session.KeyDown(modifier), "modifier down"); }
            held.Add(key);
            Check(_session.KeyDown(key), "key down");
        }
        finally { ReleaseKeys(held); }
    }

    private void ReleaseKeys(List<byte> held)
    {
        Exception? failure = null;
        for (int i = held.Count - 1; i >= 0; i--)
        {
            try { Check(_session.KeyUp(held[i]), "key release"); }
            catch (Exception error) { failure = error; }
        }
        if (failure != null) throw failure;
    }

    public void TypeText(string text) => Check(_session.TypeText(text), "text input");

    public void Dispose()
    {
        // A failed unbind must retain the native session so explicit release can retry.
        Check(_session.UnbindWindow(), "unbind");
        _session.Dispose();
    }

    private static MouseButton ParseButton(string button) => button switch
    {
        "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle,
        _ => throw new ArgumentException("Unsupported mouse button.")
    };

    private static void Check(bool success, string action)
    {
        if (!success) throw new InvalidOperationException("Gosf background input failed: " + action + ". OLE drag/drop is unsupported; use foreground drag for OLE.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
