using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Gosf.Input.Automation;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.IntegrationTests;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

internal static class Program
{
    private static Window _target = null!;
    private static Process? _coverProcess;
    private static TextBox _text = null!;
    private static Slider _slider = null!;
    private static ScrollViewer _scroll = null!;
    private static int _clicks, _activations, _doubleClicks, _oleAttempts;
    private static bool _dropped, _dragging;
    private static bool _allowDesktopActivity, _backgroundOnly, _passiveCover;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Contains("--cover")) return RunCover(args.Contains("--passive-cover"));
        if (!HasExpectedArchitecture(args)) return 1;
        _allowDesktopActivity = args.Contains("--allow-desktop-activity");
        _backgroundOnly = args.Contains("--background-only");
        _passiveCover = args.Contains("--passive-cover");
        var app = new Application();
        CreateWindows();
        InspectorService.Initialize(Process.GetCurrentProcess().Id);
        int exitCode = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Task.Run(ExerciseAsync); exitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { await CleanupAsync(); app.Shutdown(); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run();
        return exitCode;
    }

    private static bool HasExpectedArchitecture(string[] args)
    {
        bool matches = (!args.Contains("--expect-x86") || IntPtr.Size == 4) &&
            (!args.Contains("--expect-x64") || IntPtr.Size == 8);
        if (!matches) Console.Error.WriteLine("Wrong test process architecture: " + IntPtr.Size * 8);
        return matches;
    }

    private static async Task CleanupAsync()
    {
        try { await Task.Run(() => Send(new SetInputModeRequest { Mode = InputModes.Foreground })); }
        catch (Exception cleanup) { Console.Error.WriteLine("Cleanup: " + cleanup.Message); }
        InspectorService.Instance?.Dispose();
        if (_coverProcess != null && !_coverProcess.HasExited) _coverProcess.Kill();
        _coverProcess?.Dispose();
    }

    private static void CreateWindows()
    {
        _target = new Window { Title = "Input smoke target", Content = CreateControls(), Width = 450, Height = 400,
            Left = 80, Top = 80, ShowActivated = false };
        _target.Activated += (_, _) => _activations++;
        _target.Show(); _target.UpdateLayout();
        var track = (Track)_slider.Template.FindName("PART_Track", _slider);
        Id(track.Thumb, "Thumb");
        StartCover();
        _activations = 0;
    }

    private static StackPanel CreateControls()
    {
        var panel = new StackPanel();
        var button = Id(new Button { Content = "Click", Height = 40 }, "Button");
        button.Click += (_, _) => _clicks++;
        button.MouseDoubleClick += (_, _) => _doubleClicks++;
        panel.Children.Add(button);
        _text = Id(new TextBox { Text = "old", Height = 40 }, "Text"); panel.Children.Add(_text);
        _slider = Id(new Slider { Minimum = 0, Maximum = 100, Value = 20, Height = 40 }, "Slider"); panel.Children.Add(_slider);
        _scroll = Id(new ScrollViewer { Height = 150, Content = new Border { Height = 1200 } }, "Scroll"); panel.Children.Add(_scroll);
        panel.Children.Add(CreateOleControls());
        panel.Children.Add(new NativeTestHost { Height = 20 });
        return panel;
    }

    private static StackPanel CreateOleControls()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = 40 };
        var source = Id(new Border { Width = 150, Height = 35, Background = System.Windows.Media.Brushes.Blue }, "OleSource");
        var target = Id(new Border { Width = 150, Height = 35, Background = System.Windows.Media.Brushes.Green, AllowDrop = true }, "OleTarget");
        source.MouseMove += (_, e) =>
        {
            if (_dragging || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            _dragging = true;
            _oleAttempts++;
            try { DragDrop.DoDragDrop(source, "background-drag", DragDropEffects.Copy); }
            finally { _dragging = false;  }
        };
        target.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        target.Drop += (_, e) => { _dropped = e.Data.GetDataPresent(DataFormats.StringFormat); e.Handled = true; };
        panel.Children.Add(source); panel.Children.Add(target);
        return panel;
    }

    private static void StartCover()
    {
        _coverProcess = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName!, "--cover" + (_passiveCover ? " --passive-cover" : "")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        var ready = Stopwatch.StartNew();
        while (_coverProcess!.MainWindowHandle == IntPtr.Zero)
        {
            if (_coverProcess.HasExited || ready.ElapsedMilliseconds > 5000) throw new InvalidOperationException("Cover did not open.");
            System.Threading.Thread.Sleep(50); _coverProcess.Refresh();
        }
    }

    private static int RunCover(bool passive)
    {
        var app = new Application();
        var text = Id(new TextBox { Text = "Other application keeps keyboard focus." }, "CoverText");
        var window = new Window { Title = "Input smoke cover (separate process)", Width = 500, Height = 450,
            Left = 60, Top = 60, Content = text, ShowActivated = !passive };
        window.Loaded += (_, _) => { if (!passive) { window.Activate(); text.Focus(); } };
        InspectorService.Initialize(Process.GetCurrentProcess().Id);
        return app.Run(window);
    }

    private static T Id<T>(T element, string id) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(element, id);
        return element;
    }

    private static async Task ExerciseAsync()
    {
        var handles = new Dictionary<string, string>();
        foreach (var id in new[] { "Button", "Text", "Thumb", "Scroll", "OleSource", "OleTarget" })
        {
            var resolved = await Send(new ResolveAutomationIdRequest { AutomationId = id });
            handles.Add(id, resolved.GetProperty("elementHandle").GetString()!);
        }
        await Task.Delay(400);
        var foreground = GetForegroundWindow(); GetPhysicalCursorPos(out var cursor);
        Console.WriteLine("Initial foreground: " + DescribeWindow(foreground));
        await Send(new SetInputModeRequest { Mode = InputModes.Background });
        await ExerciseBackground(handles);
        await VerifyBackgroundDesktop(foreground, cursor);
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
        await ExerciseReplayAsync();
        await VerifyBackgroundDesktop(foreground, cursor);
        await ExerciseModeSwitchesAsync(handles, foreground, cursor);
        if (!_backgroundOnly) await ExerciseForeground(handles);
        await VerifyPartialUnbindAsync();
        Console.WriteLine($"PASS: {Environment.Version}, {IntPtr.Size * 8}-bit; {(_backgroundOnly ? "background" : "foreground/background")} controls and cleanup.");
    }

    private static async Task VerifyPartialUnbindAsync()
    {
        var hwnd = await Ui(() => new WindowInteropHelper(_target).Handle);
        using var input = new InputSession();
        Check(input.BindWindow(hwnd, MouseMode.WindowMessage, KeyboardMode.DirectInput), "Mixed-mode bind failed.");
        Check(input.KeyPress(0x87), "F24 failed to initialize background keyboard input.");

        // Hold the dispatcher until the keyboard's cleanup message times out.
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var dispatcher = Ui(() =>
        {
            paused.Set();
            using (_target.Dispatcher.DisableProcessing()) return resume.Wait(TimeSpan.FromSeconds(15));
        });
        try
        {
            Check(paused.Wait(TimeSpan.FromSeconds(5)), "Dispatcher did not pause.");
            Check(!input.UnbindWindow() && input.IsBound, "Expected a retryable partial unbind.");
        }
        finally { resume.Set(); await dispatcher; }

        // Probe the mouse backend without moving the real cursor before trying a click.
        Check(!input.SetMouseTrajectory(TrajectoryMode.Straight, 0, 0, 0, 0, 0), "Partially released mouse is available.");
        Check(!input.Click(MouseButton.Left), "Click succeeded after partial unbind.");
        Check(!input.KeyPress(0x87), "Keyboard input succeeded after partial unbind.");
        Check(input.UnbindWindow() && !input.IsBound, "Unbind retry failed.");
        Check(input.SetMouseTrajectory(TrajectoryMode.Straight, 0, 0, 0, 0, 0), "Default mouse backend was not restored.");
        Console.WriteLine("PASS: partial unbind blocked mouse and keyboard input until cleanup succeeded.");
    }

    private static async Task ExerciseReplayAsync()
    {
        var target = new ElementSelector { AutomationId = "Text", TypeName = typeof(TextBox).FullName! };
        var workflow = new RecordedWorkflow { Steps = new()
        {
            new WorkflowStep { Action = "SetText", Target = target, Arguments = JsonSerializer.SerializeToElement(new { text = "recorded" }) },
            new WorkflowStep { Action = "AssertElement", Target = target, Arguments = JsonSerializer.SerializeToElement(new { propertyName = "Text", expectedValue = "recorded" }) }
        } };
        await Ui(() => { _text.Text = "reset"; return true; });
        var replayed = await Send(new ReplayRequest { Workflow = workflow, TimeoutMs = 10000 });
        Check(replayed.GetProperty("result").GetProperty("success").GetBoolean(), "Version 1 workflow failed: " + replayed);
        Check(await Ui(() => _text.Text == "recorded"), "Replay did not replace text.");
        await VerifyFailedReplayCleanupAsync(workflow);
        await VerifyCoverTextAsync();
    }

    private static async Task VerifyFailedReplayCleanupAsync(RecordedWorkflow workflow)
    {
        workflow.Steps[0].Arguments = JsonSerializer.SerializeToElement(new { text = "before failure", physical = true });
        workflow.Steps[1].Target = new ElementSelector { TypeName = typeof(TextBox).FullName!, AutomationId = "MissingAfterBackgroundAction" };
        workflow.Steps.Insert(0, new WorkflowStep { Action = "SetInputMode", Arguments = JsonSerializer.SerializeToElement(new { mode = "background" }) });
        var replayed = await Send(new ReplayRequest { Workflow = workflow, TimeoutMs = 10000 });
        Check(!replayed.GetProperty("result").GetProperty("success").GetBoolean() && replayed.GetProperty("result").GetProperty("completedSteps").GetInt32() == 2,
            "Replay did not stop at the missing target.");
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
    }

    private static async Task ExerciseModeSwitchesAsync(Dictionary<string, string> handles, IntPtr foreground, POINT cursor)
    {
        await Send(new RecordRequest { Action = "clear" });
        await Send(new SetInputModeRequest { Mode = InputModes.Background });
        var untargeted = await Send(new SendKeysRequest { Keys = "Ctrl+A" }, false);
        Check(!untargeted.GetProperty("success").GetBoolean() && untargeted.GetProperty("error").GetString()!.Contains("explicit target"),
            "A background shortcut needs an explicit target.");
        await Send(new RecordRequest { Action = "start" });
        var screen = new CaptureScreenshotRequest { Mode = "screen", ElementHandle = handles["Text"], MaxWidth = 100 };
        var blocked = await Send(screen, false);
        Check(!blocked.GetProperty("success").GetBoolean() && blocked.GetProperty("error").GetString()!.Contains("foreground mode"),
            "Screen capture must require a switch even before the first background binding.");
        var auto = await Send(new CaptureScreenshotRequest { Mode = "auto", MaxWidth = 100 }, false);
        Check(!auto.GetProperty("success").GetBoolean() && auto.GetProperty("error").GetString()!.Contains("foreground mode"),
            "Auto capture of a visible native host must request foreground before activation.");
        var render = await Send(new CaptureScreenshotRequest { Mode = "auto", ElementHandle = handles["Text"], MaxWidth = 100 });
        Check(render.GetProperty("captureMode").GetString() == "render", "Auto render was unavailable in background.");
        var physical = await Send(new SetTextRequest { ElementHandle = handles["Text"], Text = "switched", Physical = true });
        Check(physical.GetProperty("method").GetString()!.StartsWith("Background."), "Physical text did not use the switch.");
        var direct = await Send(new SetTextRequest { ElementHandle = handles["Text"], Text = "direct" });
        Check(!direct.GetProperty("method").GetString()!.StartsWith("Background."), "Default set-text lost its property path.");
        await VerifyBackgroundDesktop(foreground, cursor);
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
        if (!_backgroundOnly)
        {
            var capture = await Send(screen);
            Check(capture.GetProperty("captureMode").GetString() == "screen", "Foreground screen capture failed.");
        }
        await Send(new SetInputModeRequest { Mode = InputModes.Background });
        var stopped = await Send(new RecordRequest { Action = "stop" });
        var workflow = IpcSerializer.Deserialize<RecordedWorkflow>(stopped.GetProperty("state").GetProperty("workflow").GetRawText())!;
        Check(workflow.SchemaVersion == 1 && workflow.Steps.Select(s => s.Action).SequenceEqual(new[] {
            "SetInputMode", "SetText", "SetText", "SetInputMode", "SetInputMode" }), "Unexpected mode-switch recording.");
        Check(workflow.Steps[1].Arguments.GetProperty("physical").GetBoolean() && !workflow.Steps[1].Arguments.TryGetProperty("inputMode", out _), "Unexpected per-step mode or missing physical flag.");
        Check(workflow.Steps.Where(s => s.Action == "SetInputMode").Select(s => s.Arguments.GetProperty("mode").GetString())
            .SequenceEqual(new[] { "background", "foreground", "background" }), "Wrong recorded switches.");
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
        await Ui(() => { _text.Text = "reset"; return true; });
        var replayed = await Send(new ReplayRequest { Workflow = workflow, TimeoutMs = 10000 });
        Check(replayed.GetProperty("result").GetProperty("completedSteps").GetInt32() == 5, "Switch replay failed: " + replayed);
        Check(await Ui(() => _text.Text == "direct"), "Replay did not restore the expected text.");
        blocked = await Send(screen, false);
        Check(!blocked.GetProperty("success").GetBoolean() && blocked.GetProperty("error").GetString()!.Contains("foreground mode"),
            "Final background switch did not survive replay.");
        await Send(new SetInputModeRequest { Mode = InputModes.Foreground });
    }

    private static async Task VerifyCoverTextAsync()
    {
        var resolved = await Send(new ResolveAutomationIdRequest { AutomationId = "CoverText" }, processId: _coverProcess!.Id);
        await Send(new AssertElementRequest { ElementHandle = resolved.GetProperty("elementHandle").GetString()!,
            PropertyName = "Text", ExpectedValue = "Other application keeps keyboard focus." }, processId: _coverProcess.Id);
    }

    private static async Task VerifyBackgroundDesktop(IntPtr foreground, POINT cursor)
    {
        var targetWindow = await Ui(() => new WindowInteropHelper(_target).Handle);
        var activations = await Ui(() => _activations);
        GetPhysicalCursorPos(out var after);
        Check(GetForegroundWindow() != targetWindow && activations == 0, "Background input activated target.");
        if (_allowDesktopActivity)
        {
            Console.WriteLine("Desktop activity allowed: target stayed inactive; exact cursor/foreground preservation not asserted.");
            return;
        }
        Check(GetForegroundWindow() == foreground, "Desktop foreground changed: " + DescribeWindow(foreground) + " -> " + DescribeWindow(GetForegroundWindow()));
        Check(cursor.X == after.X && cursor.Y == after.Y, $"Desktop cursor changed: ({cursor.X},{cursor.Y}) -> ({after.X},{after.Y}).");
    }

    private static async Task ExerciseBackground(Dictionary<string, string> handles)
    {
        await Send(new MousePointerRequest { Options = new MousePointerOptions {
            ElementHandle = handles["Button"], HoverMs = 100 } });
        await Send(new GetCapabilitiesRequest());
        await Task.Delay(150);
        Check(await Ui(() => ((Button)((StackPanel)_target.Content).Children[0]).IsMouseOver), "Hover was lost between commands.");
        await Send(new ClickElementRequest { ElementHandle = handles["Button"], Physical = true });
        Check(await Ui(() => _clicks == 1), "Background click was not delivered.");
        await Send(new ClickElementRequest { ElementHandle = handles["Button"], ClickType = "double" });
        Check(await Ui(() => _doubleClicks == 1), "Background double click failed.");
        await Ui(() => { _clicks = 1; return true; });
        await ExerciseBackgroundText(handles);
        await ExerciseBackgroundDrag(handles);
    }

    private static async Task ExerciseBackgroundText(Dictionary<string, string> handles)
    {
        await Send(new SetTextRequest { ElementHandle = handles["Text"], Text = "后台🙂", Physical = true });
        Check(await Ui(() => _text.Text == "后台🙂"), "Background Unicode replacement failed.");
        await Send(new SendKeysRequest { ElementHandle = handles["Text"], Keys = "Ctrl+A" });
        Check(await Ui(() => _text.SelectionLength == _text.Text.Length), "Background Ctrl+A failed.");
    }

    private static async Task ExerciseBackgroundDrag(Dictionary<string, string> handles)
    {
        await Send(new DragRequest { Options = new DragOptions { ElementHandle = handles["Thumb"], DeltaX = 80,
            DurationMs = 150 } });
        Check(await Ui(() => _slider.Value > 30 && System.Windows.Input.Mouse.Captured == null), "Background drag/capture cleanup failed.");
        await Send(new MousePointerRequest { Options = new MousePointerOptions { ElementHandle = handles["Scroll"],
            Action = "scroll", Delta = -120 } });
        Check(await Ui(() => _scroll.VerticalOffset > 0), "Background wheel failed.");
        await RejectBackgroundOleAsync(handles);
    }

    private static async Task RejectBackgroundOleAsync(Dictionary<string, string> handles)
    {
        var response = await Send(new DragRequest { Options = new DragOptions { ElementHandle = handles["OleSource"],
            TargetHandle = handles["OleTarget"], DurationMs = 150 } }, false);
        Check(!response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetString()!.Contains("OLE"),
            "Background OLE must fail explicitly without blocking the UI: " + response);
        Check(await Ui(() => _oleAttempts > 0 && !_dropped), "OLE source was not cancelled safely.");
        await Send(new MousePointerRequest { Options = new MousePointerOptions { ElementHandle = handles["Button"] } });
    }

    private static async Task ExerciseForeground(Dictionary<string, string> handles)
    {
        await Send(new ClickElementRequest { ElementHandle = handles["Button"], Physical = true });
        Check(await Ui(() => _clicks == 2), "Foreground click failed after background unbind.");
        await Send(new SetTextRequest { ElementHandle = handles["Text"], Text = "foreground", Physical = true });
        Check(await Ui(() => _text.Text == "foreground"), "Foreground text replacement failed.");
        await Send(new SendKeysRequest { ElementHandle = handles["Text"], Keys = "Ctrl+A" });
        Check(await Ui(() => _text.SelectionLength == 10), "Foreground Ctrl+A failed.");
        Check(GetForegroundWindow() == await Ui(() => new WindowInteropHelper(_target).Handle), "Foreground target not activated.");
    }

    private static Task<T> Ui<T>(Func<T> action) => Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    private static async Task<JsonElement> Send(IpcRequest request, bool requireSuccess = true, int? processId = null)
    {
        using var pipe = new NamedPipeClientStream(".", "wpf_inspector_" + (processId ?? Process.GetCurrentProcess().Id), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes(IpcSerializer.SerializeRequest(request) + "\n");
        await pipe.WriteAsync(bytes, 0, bytes.Length);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var read = reader.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(15000)) != read) throw new TimeoutException(request.RequestType);
        using var document = JsonDocument.Parse((await read)!);
        var response = document.RootElement.Clone();
        Console.WriteLine(request.RequestType + " foreground: " + DescribeWindow(GetForegroundWindow()));
        if (requireSuccess) Check(response.GetProperty("success").GetBoolean(), request.RequestType + ": " + response);
        return response;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string DescribeWindow(IntPtr window)
    {
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        GetWindowThreadProcessId(window, out var pid);
        return $"{window} PID={pid} {title}";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out POINT point);
}
