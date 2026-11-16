using System.IO;
using System.IO.Pipes;
using System.Text;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared.Ipc;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class IpcResponseTests
{
    [Fact]
    public async Task Shutdown_after_response_does_not_discard_the_acknowledgement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = Random.Shared.Next(100000000, int.MaxValue);
        IpcServer server = null!;
        using var cleanup = server = new IpcServer(pid, (_, _) => Task.FromResult<IpcResponse>(new WindowResponse
        {
            RequestId = "close",
            OnSent = () => { server.Stop(); sent.TrySetResult(); }
        }));
        server.Start();
        using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe);
        var request = Encoding.UTF8.GetBytes(IpcSerializer.SerializeRequest(new WindowRequest { Action = "close" }) + "\n");
        await pipe.WriteAsync(request, timeout.Token);
        var json = await reader.ReadLineAsync(timeout.Token);
        await sent.Task.WaitAsync(timeout.Token);
        json.Should().NotBeNullOrEmpty();
        IpcSerializer.DeserializeResponse<WindowResponse>(json!)!.Success.Should().BeTrue();
        json.Should().NotContain("onSent");
    }
}
