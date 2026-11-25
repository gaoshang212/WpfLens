using FluentAssertions;
using Moq;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class BackgroundInputSessionTests
{
    [Fact]
    public void Separate_commands_share_one_binding_until_explicit_idempotent_end()
    {
        var input = FakeInput(123);
        int created = 0;
        var session = new BackgroundInputSession(_ => { created++; return input.Object; });
        session.Acquire(new IntPtr(123)).Should().BeSameAs(session.Acquire(new IntPtr(123)));
        created.Should().Be(1);
        input.Verify(i => i.Dispose(), Times.Never);
        session.End().Should().BeTrue();
        session.End().Should().BeFalse();
        session.EnsureForegroundAllowed();
        input.Verify(i => i.Dispose(), Times.Once);
    }

    [Fact]
    public void Different_window_and_foreground_input_cannot_repurpose_a_live_binding()
    {
        var input = FakeInput(123);
        var session = new BackgroundInputSession(_ => input.Object);
        session.Acquire(new IntPtr(123));
        ((Action)(() => session.Acquire(new IntPtr(456)))).Should().Throw<InvalidOperationException>();
        ((Action)session.EnsureForegroundAllowed).Should().Throw<InvalidOperationException>();
        session.Current.Should().BeSameAs(input.Object);
        input.Verify(i => i.Dispose(), Times.Never);
    }

    [Fact]
    public void Failed_cleanup_retains_session_and_blocks_input_until_release_retry_succeeds()
    {
        var input = FakeInput(123);
        input.SetupSequence(i => i.Dispose()).Throws<InvalidOperationException>().Pass();
        var session = new BackgroundInputSession(_ => input.Object);
        session.Acquire(new IntPtr(123));
        ((Action)(() => session.End())).Should().Throw<InvalidOperationException>();
        session.Current.Should().NotBeNull();
        ((Action)(() => session.Acquire(new IntPtr(123)))).Should().Throw<InvalidOperationException>();
        session.End().Should().BeTrue();
        session.Current.Should().BeNull();
    }

    [Fact]
    public void Partial_bind_failure_keeps_retryable_session_when_cleanup_also_fails()
    {
        var input = FakeInput(123);
        input.Setup(i => i.Bind()).Throws(new InvalidOperationException("bind failed"));
        input.SetupSequence(i => i.Dispose()).Throws<InvalidOperationException>().Pass();
        var session = new BackgroundInputSession(_ => input.Object);
        ((Action)(() => session.Acquire(new IntPtr(123)))).Should().Throw<AggregateException>();
        session.Current.Should().BeSameAs(input.Object);
        ((Action)(() => session.Acquire(new IntPtr(123)))).Should().Throw<InvalidOperationException>();
        session.End().Should().BeTrue();
        session.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("background")]
    public void Mode_switch_round_trips_as_a_workflow_action_without_a_target(string mode)
    {
        var workflow = new RecordedWorkflow { Steps = new() {
            new WorkflowStep { Action = "SetInputMode", Arguments = WorkflowCodec.Arguments(new SetInputModeRequest { Mode = mode }) }
        } };
        var restored = IpcSerializer.Deserialize<RecordedWorkflow>(IpcSerializer.Serialize(workflow))!;
        WorkflowCodec.Validate(restored);
        restored.SchemaVersion.Should().Be(1);
        var step = restored.Steps.Single();
        step.Target.Should().BeNull();
        WorkflowCodec.ReadRequest(step.Action, step.Arguments).Should().BeOfType<SetInputModeRequest>().Which.Mode.Should().Be(mode);
    }

    [Theory]
    [InlineData("ClickElement", "{}", "foreground")]
    [InlineData("ClickElement", "{\"physical\":true}", "background")]
    [InlineData("ClickElement", "{\"clickType\":\"double\"}", "background")]
    [InlineData("ClickElement", "{\"clickType\":\"RIGHT\"}", "background")]
    [InlineData("ClickElement", "{\"x\":0,\"y\":0}", "background")]
    [InlineData("SetText", "{}", "foreground")]
    [InlineData("SetText", "{\"physical\":true}", "background")]
    [InlineData("SendKeys", "{}", "background")]
    [InlineData("Drag", "{\"options\":{}}", "background")]
    [InlineData("MousePointer", "{\"options\":{}}", "background")]
    public void Switch_routes_mouse_and_keyboard_before_any_window_is_bound(string action, string json, string expected)
    {
        var session = new BackgroundInputSession(_ => throw new Exception("Unexpected bind"));
        session.SetMode(InputModes.Background);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        session.UsesBackground(action, document.RootElement).Should().Be(expected == "background");
        ((Action)session.EnsureForegroundAllowed).Should().Throw<InvalidOperationException>();
        session.SetMode(InputModes.Foreground);
        session.EnsureForegroundAllowed();
        session.UsesBackground(action, document.RootElement).Should().BeFalse();
    }

    [Fact]
    public void Foreground_switch_commits_only_after_binding_cleanup_succeeds()
    {
        var input = FakeInput(123);
        input.SetupSequence(i => i.Dispose()).Throws<InvalidOperationException>().Pass();
        var session = new BackgroundInputSession(_ => input.Object);
        session.SetMode(InputModes.Background);
        session.Acquire(new IntPtr(123));
        Action switchToForeground = () => session.SetMode(InputModes.Foreground);
        switchToForeground.Should().Throw<InvalidOperationException>();
        session.Mode.Should().Be(InputModes.Background);
        ((Action)session.EnsureForegroundAllowed).Should().Throw<InvalidOperationException>();
        switchToForeground();
        session.Current.Should().BeNull();
        session.EnsureForegroundAllowed();
    }

    [Fact]
    public void Releasing_a_binding_after_an_operation_failure_keeps_background_mode()
    {
        var input = FakeInput(123);
        var session = new BackgroundInputSession(_ => input.Object);
        session.SetMode(InputModes.Background);
        session.Acquire(new IntPtr(123));
        session.End();
        session.Current.Should().BeNull();
        session.Mode.Should().Be(InputModes.Background);
        ((Action)session.EnsureForegroundAllowed).Should().Throw<InvalidOperationException>();
        session.Acquire(new IntPtr(123));
        input.Verify(i => i.Bind(), Times.Exactly(2));
    }

    private static Mock<IBackgroundInput> FakeInput(int window)
    {
        var input = new Mock<IBackgroundInput>();
        input.SetupGet(i => i.Window).Returns(new IntPtr(window));
        return input;
    }
}
