using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

public partial class InspectorService
{
    private readonly BackgroundInputSession _background = new(window => new BackgroundInput(window));
    private HwndSource? _backgroundSource;
    private EventHandler? _backgroundClosed;

    private Task<IpcResponse> ExecuteBackgroundAsync(IpcRequest request, CancellationToken token)
    {
        if (request is ClickElementRequest clickRequest) clickRequest.ClickType = clickRequest.ClickType?.ToLowerInvariant();
        if (request is SendKeysRequest keysRequest && string.IsNullOrWhiteSpace(keysRequest.ElementHandle))
            throw new ArgumentException("Background send-keys requires an explicit target element.");
        WorkflowCodec.ValidateRequest(request);
        return request switch
        {
            ClickElementRequest click => BackgroundClickAsync(click, token),
            MousePointerRequest pointer => BackgroundPointerAsync(pointer, token),
            DragRequest drag => BackgroundDragAsync(drag, token),
            SetTextRequest text => BackgroundTextAsync(text, token),
            SendKeysRequest keys => BackgroundKeysAsync(keys, token),
            _ => throw new ArgumentException("Unsupported background operation.")
        };
    }

    private UIElement ResolveBackgroundElement(string handle) =>
        _treeWalker.ResolveHandle(handle) as UIElement ?? throw new ArgumentException(StaleHandleError(handle));

    private Task<(MouseDragPlan Plan, UIElement Element)> PrepareBackgroundPointerAsync(string handle, double? x, double? y, CancellationToken token) =>
        OnDispatcher(() =>
        {
            var element = ResolveBackgroundElement(handle);
            return (ControlInteractor.PrepareBackgroundPointer(element, x, y), element);
        }, token);

    private async Task<IpcResponse> WithBackgroundAsync(MouseDragPlan plan,
        Func<IBackgroundInput, CancellationToken, Task<IpcResponse>> execute, CancellationToken token, bool validateStart = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _inputShutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        token = timeout.Token;
        token.ThrowIfCancellationRequested();
        var input = _background.Acquire(plan.SourceWindow);
        try
        {
            await ObserveBackgroundWindowAsync(input, token).ConfigureAwait(false);
            if (validateStart) input.ValidateStart(plan);
            var response = await execute(input, token).ConfigureAwait(false);
            await DrainBackgroundAsync(token).ConfigureAwait(false);
            return response;
        }
        catch (Exception failure)
        {
            try { await ReleaseBackgroundAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Background operation and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    private async Task<IpcResponse> BackgroundClickAsync(ClickElementRequest request, CancellationToken token)
    {
        var (plan, _) = await PrepareBackgroundPointerAsync(request.ElementHandle, request.X, request.Y, token).ConfigureAwait(false);
        return await WithBackgroundAsync(plan, async (input, cancellation) =>
        {
            input.Move(plan.StartX, plan.StartY);
            await DrainBackgroundAsync(cancellation).ConfigureAwait(false);
            input.Click(request.ClickType ?? "single");
            return new ClickElementResponse { RequestId = request.RequestId, Method = "Background.Click",
                ElementType = plan.ElementType, Detail = "Background click delivered; binding retained until switching to foreground." };
        }, token).ConfigureAwait(false);
    }

    private async Task<IpcResponse> BackgroundPointerAsync(MousePointerRequest request, CancellationToken token)
    {
        var options = request.Options;
        var (plan, _) = await PrepareBackgroundPointerAsync(options.ElementHandle, options.X, options.Y, token).ConfigureAwait(false);
        return await WithBackgroundAsync(plan, async (input, cancellation) =>
        {
            input.Move(plan.StartX, plan.StartY);
            await DrainBackgroundAsync(cancellation).ConfigureAwait(false);
            if (options.Action == "scroll") input.Wheel(options.Delta, options.Horizontal);
            if (options.HoverMs > 0) await Task.Delay(options.HoverMs, cancellation).ConfigureAwait(false);
            return new MousePointerResponse { RequestId = request.RequestId, Result = new MousePointerResult {
                Method = "Background." + options.Action, ElementType = plan.ElementType, X = plan.StartX, Y = plan.StartY,
                Delta = options.Delta, Horizontal = options.Horizontal } };
        }, token).ConfigureAwait(false);
    }

    private async Task<IpcResponse> BackgroundDragAsync(DragRequest request, CancellationToken token)
    {
        var plan = await OnDispatcher(() => ControlInteractor.PrepareBackgroundDrag(
            ResolveBackgroundElement(request.Options.ElementHandle), request.Options.TargetHandle == null ? null :
                ResolveBackgroundElement(request.Options.TargetHandle), request.Options), token).ConfigureAwait(false);
        return await WithBackgroundAsync(plan, async (input, cancellation) =>
        {
            var result = await MouseDragExecutor.RunAsync(plan, input, cancellation).ConfigureAwait(false);
            result.Method = "Background.Drag";
            return new DragResponse { RequestId = request.RequestId, Result = result };
        }, token, validateStart: false).ConfigureAwait(false);
    }

    private async Task<IpcResponse> BackgroundKeysAsync(SendKeysRequest request, CancellationToken token)
    {
        var combo = ControlInteractor.ParseBackgroundKeys(request.Keys);
        var (plan, element) = await PrepareBackgroundPointerAsync(request.ElementHandle!, null, null, token).ConfigureAwait(false);
        return await WithBackgroundAsync(plan, async (input, cancellation) =>
        {
            await FocusBackgroundAsync(input, plan, element, cancellation).ConfigureAwait(false);
            input.SendKeys(combo.Modifiers, combo.Key);
            return new SendKeysResponse { RequestId = request.RequestId, Method = "Background.Keys", ElementType = plan.ElementType,
                Detail = "Background shortcut delivered; verify application state." };
        }, token).ConfigureAwait(false);
    }

    private async Task FocusBackgroundAsync(IBackgroundInput input, MouseDragPlan plan, UIElement element, CancellationToken token)
    {
        input.Move(plan.StartX, plan.StartY);
        await DrainBackgroundAsync(token).ConfigureAwait(false);
        var focused = await OnDispatcher(() => ControlInteractor.FocusBackgroundElement(element), token);
        if (!focused) throw new InvalidOperationException("The target did not accept virtual keyboard focus.");
    }

    private async Task<IpcResponse> BackgroundTextAsync(SetTextRequest request, CancellationToken token)
    {
        if (request.Text.IndexOf('\0') >= 0) throw new ArgumentException("Embedded NUL is not supported.");
        var selectAll = ControlInteractor.ParseBackgroundKeys("Ctrl+A");
        var delete = ControlInteractor.ParseBackgroundKeys("Delete");
        var (plan, element) = await PrepareBackgroundPointerAsync(request.ElementHandle, null, null, token).ConfigureAwait(false);
        return await WithBackgroundAsync(plan, async (input, cancellation) =>
        {
            await FocusBackgroundAsync(input, plan, element, cancellation).ConfigureAwait(false);
            input.SendKeys(selectAll.Modifiers, selectAll.Key);
            input.SendKeys(delete.Modifiers, delete.Key);
            await DrainBackgroundAsync(cancellation).ConfigureAwait(false);
            await TypeBackgroundTextAsync(input, request.Text, cancellation).ConfigureAwait(false);
            var detail = await OnDispatcher(() => ControlInteractor.ReadBackgroundText(element, request.Text), cancellation);
            return new SetTextResponse { RequestId = request.RequestId, Method = "Background.Text", ElementType = plan.ElementType, Detail = detail };
        }, token).ConfigureAwait(false);
    }

    private async Task TypeBackgroundTextAsync(IBackgroundInput input, string text, CancellationToken token)
    {
        for (int offset = 0; offset < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(32, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            input.TypeText(text.Substring(offset, count));
            offset += count;
            await DrainBackgroundAsync(token).ConfigureAwait(false);
        }
    }

    private Task<bool> DrainBackgroundAsync(CancellationToken token) =>
        OnDispatcher(() => true, token, DispatcherPriority.Background);

    private async Task<IpcResponse> SetInputModeAsync(SetInputModeRequest request)
    {
        _background.SetMode(request.Mode);
        if (request.Mode == InputModes.Foreground)
            await ForgetBackgroundWindowAsync().ConfigureAwait(false);
        return new SetInputModeResponse { RequestId = request.RequestId, Mode = request.Mode };
    }

    private async Task ReleaseBackgroundAsync()
    {
        _background.End();
        await ForgetBackgroundWindowAsync().ConfigureAwait(false);
    }

    private async Task ForgetBackgroundWindowAsync()
    {
        if (_backgroundSource == null) return;
        await OnDispatcher(() =>
        {
            _backgroundSource.Disposed -= _backgroundClosed;
            _backgroundSource = null;
            _backgroundClosed = null;
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private Task ObserveBackgroundWindowAsync(IBackgroundInput input, CancellationToken token) =>
        _backgroundSource != null ? Task.CompletedTask : OnDispatcher(() =>
        {
            if (_backgroundSource != null) return true;
            _backgroundSource = HwndSource.FromHwnd(input.Window) ?? throw new InvalidOperationException("Background window disappeared.");
            _backgroundClosed = (_, _) => QueueBackgroundCleanup(input);
            _backgroundSource.Disposed += _backgroundClosed;
            return true;
        }, token);

    private void QueueBackgroundCleanup(IBackgroundInput? expected = null)
    {
        _ = Task.Run(async () =>
        {
            await _interactionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (expected != null && !ReferenceEquals(expected, _background.Current)) return;
                _background.End();
                _backgroundSource = null;
                _backgroundClosed = null;
            }
            catch (Exception error) { DebugLog("Background cleanup failed: " + error.Message); }
            finally { _interactionGate.Release(); }
        });
    }

    private void GuardForegroundOperation(string type, JsonElement data, IpcRequest? parsedRequest)
    {
        if (_background.Mode == InputModes.Background && type is "ClickElement" or "SetText" or "SelectItem")
        {
            // Only UIA/property/routed-event operations may bypass the foreground guard.
            var request = parsedRequest ?? WorkflowCodec.ParseLiveRequest(type, data);
            _background.EnsureCleanupSucceeded();
            if (!InputRequestMode.UsesMouseOrKeyboard(request)) return;
        }
        if (InputRequestMode.IsForegroundInputAction(type) || type == "SelectItem" ||
            type == "Window" && IpcSerializer.DeserializeRequestData<WindowRequest>(data)!.Action != "list" ||
            type == "ControlOperation" && IpcSerializer.DeserializeRequestData<ControlOperationRequest>(data)!.Mutates)
            _background.EnsureForegroundAllowed();
    }

    private (string, int, int) CaptureForegroundScreen(ScreenshotCapture capture, UIElement element, int width, int height)
    {
        // auto has now resolved to screen. Hold the gate through activation and pixel capture.
        if (!_interactionGate.Wait(0)) throw new InvalidOperationException("Another interaction is in progress. Retry after it completes.");
        try
        {
            _background.EnsureForegroundAllowed();
            return capture.CaptureScreen(element, width, height);
        }
        finally { _interactionGate.Release(); }
    }
}
