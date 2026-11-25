using WpfVisualTreeMcp.Automation.Infrastructure;
using System.IO.Pipes;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Moq;
using WpfVisualTreeMcp.Server;
using WpfVisualTreeMcp.Server.Services;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class AutomationIdTests
{
    private static NamedPipeBridge Bridge()
    {
        var manager = new Mock<IProcessManager>();
        manager.SetupGet(x => x.CurrentSession).Returns(new InspectionSession
        { ProcessId = Environment.ProcessId, InspectorReady = true });
        return new NamedPipeBridge(NullLogger<NamedPipeBridge>.Instance, manager.Object);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("handle", "save", null)]
    [InlineData("handle", null, "root")]
    [InlineData("", null, null)]
    [InlineData(null, "", null)]
    [InlineData(null, "  ", null)]
    public async Task Invalid_target_is_rejected_without_IPC(string? handle, string? id, string? root)
    {
        var act = () => Bridge().ResolveElementHandleAsync(handle, id, root);
        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Existing_handle_does_not_require_new_inspector_capabilities()
    {
        (await Bridge().ResolveElementHandleAsync("original", null)).Should().Be("original");
    }

    [Fact]
    public async Task Click_resolves_id_before_sending_the_original_click_options()
    {
        var bridge = new Mock<IIpcBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.ResolveElementHandleAsync(null, "save", "dialog")).ReturnsAsync("resolved");
        bridge.Setup(x => x.ClickElementAsync("resolved", false, "double", 10, 20))
            .ReturnsAsync(new ClickResult { Method = "Physical" });
        var tools = new WpfTools(new Mock<IProcessManager>().Object, bridge.Object);
        await tools.WpfClickElement(automation_id: "save", root_handle: "dialog", click_type: "double", x: 10, y: 20);
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Failed_resolution_never_sends_a_click()
    {
        var bridge = new Mock<IIpcBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.ResolveElementHandleAsync(null, "save", null)).ThrowsAsync(new McpException("Ambiguous AutomationId"));
        var tools = new WpfTools(new Mock<IProcessManager>().Object, bridge.Object);
        var act = () => tools.WpfClickElement(automation_id: "save");
        await act.Should().ThrowAsync<McpException>().WithMessage("*Ambiguous*");
        bridge.Verify(x => x.ClickElementAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<double?>()), Times.Never);
    }

    [Theory]
    [InlineData("find")]
    [InlineData("deep")]
    [InlineData("wait")]
    [InlineData("resolve")]
    [InlineData("replay")]
    public async Task Old_inspector_cannot_silently_ignore_an_id(string operation)
    {
        using var pipe = Pipe();
        var response = Reply(pipe, data =>
        {
            data.type.Should().Be("GetCapabilities");
            return new GetCapabilitiesResponse { CoordinatePointerInput = true };
        });
        var bridge = Bridge();
        var act = async () =>
        {
            switch (operation)
            {
                case "find": await bridge.FindElementsAsync(null, null, null, null, null, automationId: "save"); break;
                case "deep": await bridge.FindElementsDeepAsync(null, null, null, automationId: "save"); break;
                case "wait": await bridge.WaitForElementAsync(null, null, null, null, "exists", 100, 50, "save"); break;
                case "resolve": await bridge.ResolveElementHandleAsync(null, "save"); break;
                case "replay": await bridge.ReplayAsync(new RecordedWorkflow { Steps = new()
                { new WorkflowStep { Action = "FindElements", Arguments = WorkflowCodec.Arguments(new FindElementsRequest { AutomationId = "save" }) } } }); break;
            }
        };
        await act.Should().ThrowAsync<McpException>().WithMessage("*older Inspector*restart*");
        await response;
    }

    [Fact]
    public async Task Resolution_roundtrips_exact_id_and_scope_over_IPC()
    {
        using var pipe = Pipe();
        var responses = Task.Run(async () =>
        {
            await Reply(pipe, _ => new GetCapabilitiesResponse { AutomationIdTargeting = true });
            pipe.Disconnect();
            await Reply(pipe, envelope =>
            {
                envelope.type.Should().Be("ResolveAutomationId");
                var request = IpcSerializer.DeserializeRequestData<ResolveAutomationIdRequest>(envelope.data)!;
                request.AutomationId.Should().Be("Ordine.保存");
                request.RootHandle.Should().Be("dialog");
                return new ResolveAutomationIdResponse { ElementHandle = "resolved" };
            });
        });
        (await Bridge().ResolveElementHandleAsync(null, "Ordine.保存", "dialog")).Should().Be("resolved");
        await responses;
    }

    [Theory]
    [InlineData("FindElements")]
    [InlineData("FindElementsDeep")]
    [InlineData("WaitForElement")]
    public void Filters_survive_workflow_export_and_replay(string action)
    {
        IpcRequest request = action switch
        {
            "FindElements" => new FindElementsRequest { AutomationId = "save" },
            "FindElementsDeep" => new FindElementsDeepRequest { AutomationId = "save" },
            _ => new WaitForElementRequest { AutomationId = "save", Condition = "enabled" }
        };
        var step = new WorkflowStep { Action = action, Arguments = WorkflowCodec.Arguments(request) };
        WorkflowCodec.Validate(new RecordedWorkflow { Steps = new() { step } });
        var replay = WorkflowCodec.ReadRequest(action, step.Arguments);
        IpcSerializer.SerializeRequest(replay).Should().Contain("\"automationId\":\"save\"");
    }

    [Fact]
    public async Task Old_inspector_cannot_fall_back_to_foreground_for_background_input()
    {
        using var pipe = Pipe();
        var response = Reply(pipe, data =>
        {
            data.type.Should().Be("GetCapabilities");
            return new GetCapabilitiesResponse();
        });
        var act = () => Bridge().SetInputModeAsync("background");
        await act.Should().ThrowAsync<McpException>().WithMessage("*set-input-mode*");
        await response;
    }

    [Fact]
    public async Task Coordinate_click_probes_capabilities_once_per_operation()
    {
        using var pipe = Pipe();
        var responses = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                await Reply(pipe, envelope =>
                {
                    envelope.type.Should().Be("GetCapabilities");
                    return new GetCapabilitiesResponse { CoordinatePointerInput = true };
                });
                pipe.Disconnect();
                await Reply(pipe, envelope =>
                {
                    envelope.type.Should().Be("ClickElement");
                    var request = IpcSerializer.DeserializeRequestData<ClickElementRequest>(envelope.data)!;
                    envelope.data.TryGetProperty("inputMode", out _).Should().BeFalse();
                    request.X.Should().Be(10);
                    request.Y.Should().Be(20);
                    return new ClickElementResponse { Method = "Background" };
                });
                pipe.Disconnect();
            }
        });
        var bridge = Bridge();
        for (var i = 0; i < 2; i++)
            (await bridge.ClickElementAsync("target", false, x: 10, y: 20))
                .Method.Should().Be("Background");
        await responses;
    }

    [Fact]
    public async Task Replay_without_mode_switch_or_other_new_actions_needs_no_capability_probe()
    {
        using var pipe = Pipe();
        var response = Reply(pipe, envelope =>
        {
            envelope.type.Should().Be("Replay");
            return new ReplayResponse { Result = new ReplayResult { Success = true, CompletedSteps = 1 } };
        });
        var workflow = new RecordedWorkflow { Steps = new() {
            new WorkflowStep { Action = "FindElements", Arguments = WorkflowCodec.Arguments(new FindElementsRequest { TypeName = "Button" }) }
        } };
        (await Bridge().ReplayAsync(workflow)).CompletedSteps.Should().Be(1);
        await response;
    }

    [Fact]
    public async Task Replay_checks_all_required_capabilities_with_one_probe()
    {
        using var pipe = Pipe();
        var responses = Task.Run(async () =>
        {
            await Reply(pipe, envelope =>
            {
                envelope.type.Should().Be("GetCapabilities");
                return new GetCapabilitiesResponse
                {
                    CoordinatePointerInput = true, AutomationIdTargeting = true,
                    InputModeSwitch = true, AdvancedAutomation = true
                };
            });
            pipe.Disconnect();
            await Reply(pipe, envelope =>
            {
                envelope.type.Should().Be("Replay");
                return new ReplayResponse { Result = new ReplayResult { Success = true, CompletedSteps = 4 } };
            });
        });
        (await Bridge().ReplayAsync(CapabilityWorkflow())).CompletedSteps.Should().Be(4);
        await responses;
    }

    [Theory]
    [InlineData(false, false, false, false, "*coordinate clicks*")]
    [InlineData(true, false, false, false, "*AutomationId targeting*")]
    [InlineData(true, true, false, false, "*set-input-mode*")]
    [InlineData(true, true, true, false, "*Inspector 0.19*")]
    public async Task Replay_preserves_capability_rejection_order(bool pointer, bool automationId, bool background, bool advanced, string error)
    {
        using var pipe = Pipe();
        var response = Reply(pipe, envelope =>
        {
            envelope.type.Should().Be("GetCapabilities");
            return new GetCapabilitiesResponse
            {
                CoordinatePointerInput = pointer, AutomationIdTargeting = automationId,
                InputModeSwitch = background, AdvancedAutomation = advanced
            };
        });
        var act = () => Bridge().ReplayAsync(CapabilityWorkflow());
        await act.Should().ThrowAsync<McpException>().WithMessage(error);
        await response;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_switch_capability_rejects_both_live_and_replay_requests(bool replay)
    {
        using var pipe = Pipe();
        var response = Reply(pipe, envelope =>
        {
            envelope.type.Should().Be("GetCapabilities");
            return new GetCapabilitiesResponse();
        });
        var command = new SetInputModeRequest { Mode = InputModes.Foreground };
        var workflow = new RecordedWorkflow { Steps = new() {
            new WorkflowStep { Action = command.RequestType, Arguments = WorkflowCodec.Arguments(command) } } };
        Func<Task> act = () => replay ? (Task)Bridge().ReplayAsync(workflow) : Bridge().SetInputModeAsync(command.Mode);
        await act.Should().ThrowAsync<McpException>().WithMessage("*set-input-mode*");
        await response;
    }

    private static RecordedWorkflow CapabilityWorkflow() => new()
    {
        Steps = new()
        {
            new WorkflowStep
            {
                Action = "ClickElement", Target = new ElementSelector { TypeName = "Button", AutomationId = "save" },
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { x = 10, y = 20 })
            },
            new WorkflowStep { Action = "SetInputMode", Arguments = WorkflowCodec.Arguments(new SetInputModeRequest { Mode = InputModes.Background }) },
            new WorkflowStep
            {
                Action = "FindElements", Arguments = WorkflowCodec.Arguments(new FindElementsRequest { AutomationId = "save" })
            },
            new WorkflowStep
            {
                Action = "ControlOperation", Target = new ElementSelector { TypeName = "CheckBox", AutomationId = "enabled" },
                Arguments = WorkflowCodec.Arguments(new ControlOperationRequest { Operation = "check", Checked = true })
            }
        }
    };

    private static NamedPipeServerStream Pipe() => new($"wpf_inspector_{Environment.ProcessId}",
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task Reply(NamedPipeServerStream pipe,
        Func<(string type, System.Text.Json.JsonElement data), IpcResponse> response)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(timeout.Token);
        var request = IpcSerializer.DeserializeRequest(line!)!.Value;
        await writer.WriteLineAsync(IpcSerializer.SerializeResponse(response(request)));
    }
}
