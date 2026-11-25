using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WpfVisualTreeMcp.IntegrationTests;

internal sealed class NativeTestHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var host = CreateWindowEx(0, "button", "Native", 0x50000000, 0, 0, 300, 250, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (host == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        // WindowFromPoint resolves this child, not the HwndHost HWND itself.
        CreateWindowEx(0, "button", "Child", 0x50000000, 0, 0, 300, 250, host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, host);
    }
    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
