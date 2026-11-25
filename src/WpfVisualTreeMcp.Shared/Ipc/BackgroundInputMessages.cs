namespace WpfVisualTreeMcp.Shared.Ipc;

public sealed class SetInputModeRequest : IpcRequest
{
    public override string RequestType => "SetInputMode";
    public string Mode { get; set; } = "";
}

public sealed class SetInputModeResponse : IpcResponse
{
    public string Mode { get; set; } = "";
}
