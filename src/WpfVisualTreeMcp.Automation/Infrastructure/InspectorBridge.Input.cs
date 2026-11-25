using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Automation.Infrastructure;

public partial class InspectorBridge
{
    private async Task<TResponse> SendInputRequestAsync<TRequest, TResponse>(int processId, TRequest request)
        where TRequest : IpcRequest where TResponse : IpcResponse, new()
    {
        await EnsureCapabilitiesAsync(processId,
            pointer: request is MousePointerRequest || request is ClickElementRequest click && (click.X.HasValue || click.Y.HasValue));
        return await SendRequestAsync<TRequest, TResponse>(processId, request);
    }

    public async Task<SetInputModeResponse> SetInputModeAsync(string mode)
    {
        InputModes.Validate(mode);
        var pid = EnsureConnected().ProcessId;
        await EnsureCapabilitiesAsync(pid, modeSwitch: true);
        var response = await SendRequestAsync<SetInputModeRequest, SetInputModeResponse>(pid, new SetInputModeRequest { Mode = mode });
        if (!response.Success) throw CreateException(response.Error ?? "Cannot switch input mode.");
        return response;
    }

    internal static bool NeedsForegroundPermission(IpcRequest request)
    {
        if (request is not ReplayRequest replay) return MayActivateWindow(request);

        // The Inspector holds the interaction gate throughout replay. Steps before the
        // first switch use the foreground path, including in recordings without a switch.
        bool foreground = true;
        foreach (var step in replay.Workflow.Steps)
        {
            if (step.Action == "SetInputMode")
                foreground = step.Arguments.GetProperty("mode").GetString() == InputModes.Foreground;
            else if (foreground && InputRequestMode.IsForegroundInputAction(step.Action) &&
                MayActivateWindow(WorkflowCodec.ReadRequest(step.Action, step.Arguments)))
                return true;
        }
        return false;
    }

    private static bool MayActivateWindow(IpcRequest request) =>
        InputRequestMode.UsesMouseOrKeyboard(request) || request is TouchGestureRequest or CaptureScreenshotRequest { Mode: "screen" or "auto" };
}
