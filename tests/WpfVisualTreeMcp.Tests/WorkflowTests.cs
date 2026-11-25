using System.Text.Json;
using FluentAssertions;
using WpfVisualTreeMcp.Automation.Infrastructure;
using WpfVisualTreeMcp.Server;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class WorkflowTests
{
    private static ElementSelector Selector(string name) => new() { TypeName = "System.Windows.Controls.Border", Name = name };

    [Theory]
    [InlineData("ClickElement", "{}", false)]
    [InlineData("ClickElement", "{\"physical\":true}", true)]
    [InlineData("ClickElement", "{\"clickType\":\"right\"}", true)]
    [InlineData("ClickElement", "{\"x\":2,\"y\":3}", true)]
    [InlineData("SetText", "{\"text\":\"hello\"}", false)]
    [InlineData("SetText", "{\"text\":\"hello\",\"physical\":true}", true)]
    [InlineData("SendKeys", "{\"keys\":\"Enter\"}", true)]
    [InlineData("MousePointer", "{\"options\":{\"action\":\"move\"}}", true)]
    [InlineData("Drag", "{\"options\":{\"deltaX\":5}}", true)]
    [InlineData("TouchGesture", "{\"options\":{\"gesture\":\"tap\"}}", true)]
    public void Replay_grants_foreground_permission_only_for_input_in_a_foreground_step(
        string action, string arguments, bool needsForeground)
    {
        using var json = JsonDocument.Parse(arguments);
        var input = new WorkflowStep { Action = action, Target = Selector("Target"), Arguments = json.RootElement.Clone() };
        WorkflowStep Switch(string mode) => new()
        {
            Action = "SetInputMode", Arguments = WorkflowCodec.Arguments(new SetInputModeRequest { Mode = mode })
        };
        bool NeedsGrant(params WorkflowStep[] steps) => InspectorBridge.NeedsForegroundPermission(
            new ReplayRequest { Workflow = new RecordedWorkflow { Steps = steps.ToList() } });

        var background = Switch(InputModes.Background);
        var foreground = Switch(InputModes.Foreground);
        var shortcut = new WorkflowStep { Action = "SendKeys", Target = Selector("Target"),
            Arguments = WorkflowCodec.Arguments(new SendKeysRequest { Keys = "Enter" }) };
        NeedsGrant(input).Should().Be(needsForeground);
        NeedsGrant(input, background).Should().Be(needsForeground);
        NeedsGrant(background, input, foreground).Should().BeFalse();
        NeedsGrant(background, shortcut, foreground, input).Should().Be(needsForeground);
    }

    [Fact]
    public void Drag_recording_preserves_local_parameters_and_replaces_both_session_handles()
    {
        var original = new DragRequest { Options = new DragOptions { ElementHandle = "elem_old", TargetHandle = "elem_old2", StartX = 12, StartY = 14, DurationMs = 600 } };
        var step = new WorkflowStep { Action = "Drag", Target = Selector("Source"), Destination = Selector("Destination"), Arguments = WorkflowCodec.Arguments(original) };
        var workflow = new RecordedWorkflow { Steps = new() { step } };
        WorkflowCodec.Validate(workflow);
        var json = IpcSerializer.Serialize(workflow);
        json.Should().NotContain("elem_old").And.NotContain("elementHandle").And.NotContain("targetHandle");
        var restored = IpcSerializer.Deserialize<RecordedWorkflow>(json)!;
        var request = (DragRequest)WorkflowCodec.ReadRequest(restored.Steps[0].Action, restored.Steps[0].Arguments);
        WorkflowCodec.BindHandles(request, "elem_fresh", "elem_fresh2", null);
        request.Options.Validate();
        request.Options.ElementHandle.Should().Be("elem_fresh");
        request.Options.TargetHandle.Should().Be("elem_fresh2");
        request.Options.StartX.Should().Be(12);
        request.Options.DurationMs.Should().Be(600);
    }

    [Fact]
    public void Touch_export_is_data_only_and_preserves_unicode_and_rotation()
    {
        var request = new TouchGestureRequest { Options = new TouchGestureOptions { ElementHandle = "elem_old", Gesture = "rotate", AngleDegrees = -90, Radius = 40 } };
        var workflow = new RecordedWorkflow { Name = "à漢字", Steps = new() { new WorkflowStep { Action = "TouchGesture", Target = Selector("Surface"), Arguments = WorkflowCodec.Arguments(request) } } };
        var files = WorkflowTestExporter.Export(new RecordingState { Workflow = workflow }, "xunit");
        files.Keys.Should().BeEquivalentTo("workflow.json", "RecordedWorkflow.Tests.csproj", "WorkflowTests.cs", "README.md");
        var restored = IpcSerializer.Deserialize<RecordedWorkflow>(files["workflow.json"])!;
        restored.Name.Should().Be("à漢字");
        var touch = (TouchGestureRequest)WorkflowCodec.ReadRequest("TouchGesture", restored.Steps[0].Arguments);
        touch.Options.AngleDegrees.Should().Be(-90);
        touch.Options.Radius.Should().Be(40);
    }

    [Theory]
    [InlineData("RunShell", "{}")]
    [InlineData("ClickElement", "{\"elementHandle\":\"elem_old\"}")]
    [InlineData("Drag", "{\"options\":null}")]
    [InlineData("WaitForElement", "{\"elementName\":\"Spinner\",\"timeoutMs\":999999}")]
    public void Rejects_invalid_steps_before_execution(string action, string arguments)
    {
        using var json = JsonDocument.Parse(arguments);
        var workflow = new RecordedWorkflow { Steps = new() { new WorkflowStep { Action = action, Target = Selector("Target"), Arguments = json.RootElement.Clone() } } };
        var validate = () => WorkflowCodec.Validate(workflow);
        validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_to_export_active_or_empty_recordings()
    {
        var active = () => WorkflowTestExporter.Export(new RecordingState { Active = true, Workflow = new RecordedWorkflow() }, "xunit");
        active.Should().Throw<InvalidOperationException>().WithMessage("*Stop recording*");
        var empty = () => WorkflowTestExporter.Export(new RecordingState { Workflow = new RecordedWorkflow() }, "xunit");
        empty.Should().Throw<ArgumentException>();
    }
}
