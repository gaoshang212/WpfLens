using System;
using System.Text.Json;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

internal interface IBackgroundInput : IMouseInput, IDisposable
{
    IntPtr Window { get; }
    void Bind();
    void Click(string kind);
    void Wheel(int delta, bool horizontal);
    void SendKeys(byte[] modifiers, byte key);
    void TypeText(string text);
}

/// <summary>Target-owned state. Call only while holding the existing interaction gate.</summary>
internal sealed class BackgroundInputSession
{
    private readonly Func<IntPtr, IBackgroundInput> _create;
    internal IBackgroundInput? Current { get; private set; }
    internal string Mode { get; private set; } = InputModes.Foreground;
    private bool _cleanupFailed;

    internal BackgroundInputSession(Func<IntPtr, IBackgroundInput> create) => _create = create;

    internal void SetMode(string mode)
    {
        InputModes.Validate(mode);
        if (mode == InputModes.Foreground) End();
        else EnsureCleanupSucceeded();
        Mode = mode;
    }

    internal void EnsureCleanupSucceeded()
    {
        if (_cleanupFailed) throw new InvalidOperationException("Background cleanup failed. Switch to foreground to retry cleanup.");
    }

    internal bool UsesBackground(string action, JsonElement data) =>
        TryParseBackgroundRequest(action, data, out _);

    internal bool TryParseBackgroundRequest(string action, JsonElement data, out IpcRequest? request)
    {
        request = null;
        if (Mode != InputModes.Background || !InputRequestMode.IsBackgroundAction(action)) return false;
        request = WorkflowCodec.ParseLiveRequest(action, data);
        return InputRequestMode.UsesMouseOrKeyboard(request);
    }

    internal IBackgroundInput Acquire(IntPtr window)
    {
        EnsureCleanupSucceeded();
        if (Current != null && Current.Window != window)
            throw new InvalidOperationException("End the existing background session before targeting a different window.");
        if (Current == null) CreateBinding(window);
        return Current!;
    }

    private void CreateBinding(IntPtr window)
    {
        Current = _create(window);
        try { Current.Bind(); }
        catch (Exception failure)
        {
            try { End(); }
            catch (Exception cleanup) { throw new AggregateException("Background bind and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    internal bool End()
    {
        if (Current == null) return false;
        try { Current.Dispose(); }
        catch { _cleanupFailed = true; throw; }
        Current = null;
        _cleanupFailed = false;
        return true;
    }

    internal void EnsureForegroundAllowed()
    {
        if (Mode == InputModes.Background)
            throw new InvalidOperationException("This operation requires foreground mode. Use set-input-mode --mode foreground or wpf_set_input_mode before retrying.");
        if (Current != null)
            throw new InvalidOperationException("End the background session before using foreground or physical input.");
    }
}
