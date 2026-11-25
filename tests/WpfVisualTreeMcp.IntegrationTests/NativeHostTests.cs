using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class NativeHostTests
{
    [Fact]
    public Task Native_host_accepts_its_child_pixels_but_rejects_covering_windows() => DiagnosticReporterTests.OnSta(() =>
    {
        using var host = new NativeTestHost { Name = "NativeSurface" };
        var window = new Window { Content = host, Width = 300, Height = 250, Left = 50, Top = 50, Topmost = true };
        Window? overlay = null;
        try
        {
            window.Show(); window.UpdateLayout();
            var plan = ControlInteractor.PreparePointer(host, 20, 30);
            PhysicalInputGuard.IsHostedPoint(host.Handle, plan.StartX, plan.StartY).Should().BeTrue();
            var touch = new ControlInteractor().PrepareTouch(host, new TouchGestureOptions { ElementHandle = "test", Gesture = "tap", X = 20, Y = 30 });
            touch.Should().NotBeNull();
            using var tree = JsonDocument.Parse(new TreeWalker().WalkVisualTree(host));
            tree.RootElement.GetProperty("root").GetProperty("nativeContent").GetProperty("windowHandle").GetString().Should().StartWith("0x");
            var capture = () => new ScreenshotCapture().CaptureElement(window);
            capture.Should().Throw<InvalidOperationException>().WithMessage("*mode='auto'*");

            overlay = new Window { Width = 300, Height = 250, Left = 50, Top = 50, Topmost = true, Content = new TextBlock { Text = "Cover" } };
            overlay.Show(); overlay.UpdateLayout();
            PhysicalInputGuard.IsHostedPoint(host.Handle, plan.StartX, plan.StartY).Should().BeFalse();
        }
        finally { overlay?.Close(); window.Close(); }
    });
}
