using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

public partial class InspectorService
{
    private static Task<T> OnDispatcher<T>(Func<T> callback, CancellationToken cancellationToken,
        DispatcherPriority priority = DispatcherPriority.Normal) =>
        Task.Run(() => Application.Current.Dispatcher.Invoke(callback, priority,
            cancellationToken, TimeSpan.FromSeconds(10)), cancellationToken);

    private Task<IpcResponse> ExecuteOperationAsync(string type, JsonElement data, CancellationToken token, bool useSessionMode = true)
    {
        token.ThrowIfCancellationRequested();
        if (type == "SetInputMode") return SetInputModeAsync(IpcSerializer.DeserializeRequestData<SetInputModeRequest>(data)!);
        IpcRequest? parsedRequest = null;
        if (useSessionMode && _background.TryParseBackgroundRequest(type, data, out parsedRequest))
            return ExecuteBackgroundAsync(parsedRequest!, token);
        GuardForegroundOperation(type, data, parsedRequest);
        return type switch
        {
            "Drag" => HandleDragAsync(data, token),
            "TouchGesture" => HandleTouchGestureAsync(data, token),
            "MousePointer" => HandleMousePointerAsync(data, token),
            "WaitForElement" => HandleWaitForElementAsync(data, token),
            _ => OnDispatcher(() => HandleRequest(type, data, token), token)
        };
    }

    private async Task<IpcResponse> HandleMousePointerAsync(JsonElement data, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _inputShutdown.Token);
        token = linked.Token;
        var request = IpcSerializer.DeserializeRequestData<MousePointerRequest>(data)
            ?? throw new ArgumentException("Mouse pointer request required.");
        var options = request.Options ?? throw new ArgumentException("Pointer options required.");
        options.Validate();
        var plan = await OnDispatcher(() =>
        {
            var element = _treeWalker.ResolveHandle(options.ElementHandle)
                ?? throw new InvalidOperationException(StaleHandleError(options.ElementHandle));
            if (element is not UIElement ui) throw new ArgumentException("Pointer target must be a UIElement.");
            return ControlInteractor.PreparePointer(ui, options.X, options.Y);
        }, token).ConfigureAwait(false);
        var input = new NativeMouseInput();
        token.ThrowIfCancellationRequested();
        input.ValidateStart(plan);
        input.Move(plan.StartX, plan.StartY);
        // Let MouseEnter/focus/tooltip handlers run before any wheel event.
        await Task.Delay(40, token).ConfigureAwait(false);
        input.ValidateStart(plan);
        if (options.Action == "scroll") input.Wheel(options.Delta, options.Horizontal);
        if (options.HoverMs > 0) await Task.Delay(options.HoverMs, token).ConfigureAwait(false);
        input.VerifyForeground();
        return new MousePointerResponse
        {
            RequestId = request.RequestId,
            Result = new MousePointerResult
            {
                Method = options.Action == "scroll" ? "Physical.Wheel" : "Physical.Move",
                ElementType = plan.ElementType, X = plan.StartX, Y = plan.StartY,
                Delta = options.Delta, Horizontal = options.Horizontal
            }
        };
    }

    private IpcResponse HandleRecord(JsonElement data)
    {
        var request = IpcSerializer.DeserializeRequestData<RecordRequest>(data)!;
        return new RecordResponse { RequestId = request.RequestId, State = _recorder.Control(request.Action, request.Name, _background.Mode) };
    }

    private IpcResponse HandleAssertElement(JsonElement data)
    {
        var request = IpcSerializer.DeserializeRequestData<AssertElementRequest>(data)!;
        WorkflowCodec.ValidateRequest(request);
        var element = _treeWalker.ResolveHandle(request.ElementHandle)
            ?? throw new InvalidOperationException(StaleHandleError(request.ElementHandle));
        var property = _propertyReader.GetDependencyProperties(element).FirstOrDefault(p => p.Name == request.PropertyName)
            ?? throw new ArgumentException("Unknown dependency property: " + request.PropertyName);
        var value = element.GetValue(property);
        var actual = value == null ? "{null}" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        bool matches = string.Equals(actual, request.ExpectedValue, StringComparison.Ordinal);
        return new AssertElementResponse
        {
            RequestId = request.RequestId, Success = matches, ActualValue = actual,
            Error = matches ? null : $"Assertion failed for {request.PropertyName}: expected '{request.ExpectedValue}', actual '{actual}'."
        };
    }

    private async Task<IpcResponse> HandleReplayAsync(JsonElement data)
    {
        var request = IpcSerializer.DeserializeRequestData<ReplayRequest>(data)!;
        WorkflowCodec.Validate(request.Workflow);
        if (request.TimeoutMs < 1000 || request.TimeoutMs > 120000)
            throw new ArgumentException("Replay timeout must be between 1000 and 120000 ms.");
        if (_recorder.Active) throw new InvalidOperationException("Stop recording before replaying a workflow.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_inputShutdown.Token);
        timeout.CancelAfter(request.TimeoutMs);
        var result = await ReplayStepsAsync(request.Workflow, timeout.Token).ConfigureAwait(false);
        return new ReplayResponse { RequestId = request.RequestId, Result = result };
    }
    private async Task<ReplayResult> ReplayStepsAsync(RecordedWorkflow workflow, CancellationToken token)
    {
        var result = new ReplayResult();
        try
        {
            // A recording started in background begins with an explicit switch.
            bool usesModeSwitch = workflow.HasInputModeSwitch();
            if (usesModeSwitch && workflow.Steps[0].Action != "SetInputMode")
            {
                token.ThrowIfCancellationRequested();
                await SetInputModeAsync(new SetInputModeRequest { Mode = InputModes.Foreground }).ConfigureAwait(false);
            }
            foreach (var step in workflow.Steps)
            {
                await ReplayStepAsync(step, usesModeSwitch, token).ConfigureAwait(false);
                result.CompletedSteps++;
            }
            result.Success = true;
        }
        catch (Exception error)
        {
            result.FailedStep = result.CompletedSteps + 1;
            result.Error = error is OperationCanceledException
                ? "Replay deadline expired or Inspector is shutting down." : error.Message;
            try { await ReleaseBackgroundAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { result.Error += " Background cleanup failed: " + cleanup.Message; }
        }
        return result;
    }

    private async Task ReplayStepAsync(WorkflowStep step, bool usesModeSwitch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var operation = WorkflowCodec.ReadRequest(step.Action, step.Arguments);
        await OnDispatcher(() => BindReplayTargets(operation, step), token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(IpcSerializer.Serialize<object>(operation));
        var response = await ExecuteReplayOperationAsync(operation.RequestType, document.RootElement, usesModeSwitch, token).ConfigureAwait(false);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Step failed.");
        if (response is WaitForElementResponse wait && !wait.Matched)
            throw new InvalidOperationException("Recorded wait condition was not satisfied before its timeout.");
        // Drain queued input/layout callbacks before resolving the next selector.
        await OnDispatcher(() => true, token, DispatcherPriority.Background).ConfigureAwait(false);
    }

    private bool BindReplayTargets(IpcRequest operation, WorkflowStep step)
    {
        string? Resolve(ElementSelector? selector) => selector == null ? null :
            _treeWalker.GetOrCreateHandle(_recorder.Selectors.Resolve(selector)!);
        WorkflowCodec.BindHandles(operation, Resolve(step.Target), Resolve(step.Destination), Resolve(step.Root));
        return true;
    }

    private async Task<IpcResponse> ExecuteReplayOperationAsync(string type, JsonElement data, bool useSessionMode, CancellationToken token)
    {
        var response = await ExecuteOperationAsync(type, data, token, useSessionMode).ConfigureAwait(false);
        while (response is ControlOperationResponse control && control.NotReady)
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            response = await ExecuteOperationAsync(type, data, token, useSessionMode).ConfigureAwait(false);
        }
        return response;
    }

}
