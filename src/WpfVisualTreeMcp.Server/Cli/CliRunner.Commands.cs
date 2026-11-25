using WpfVisualTreeMcp.Automation.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfVisualTreeMcp.Server.Services;
using WpfVisualTreeMcp.Shared.Models;
using WpfVisualTreeMcp.Shared;
using WpfVisualTreeMcp.Shared.Ipc;

namespace WpfVisualTreeMcp.Server.Cli;

public static partial class CliRunner
{
    private static async Task<int> RunUpdateAsync(string[] args, CliArgs cli)
    {
        // Reject misspelled options rather than accidentally starting an update.
        var allowed = new[]
        {
            "--check",
            "--status",
            "--compact",
            "--verbose"
        };
        if (args.Skip(1).Any(a => !allowed.Contains(a, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Usage: update [--check | --status] [--compact] [--verbose]");
        var result = await SelfUpdater.RunAsync(cli.Flags.Contains("check"), cli.Flags.Contains("status"));
        WriteJson(result, cli);
        return result is JsonElement state && state.GetProperty("status").GetString() == "failed" ? 1 : 0;
    }

    private static async Task<int> RunListAsync(CliArgs cli, ProcessManager processManager)
    {
        var processes = await processManager.GetWpfProcessesAsync();
        WriteJson(new { processes }, cli);
        return 0;
    }

    private static async Task<int> RunAttachAsync(CliArgs cli, ProcessManager processManager)
    {
        var session = await AttachAsync(processManager, cli, cli.Flags.Contains("auto-inject"));
        WriteJson(new { success = session.InspectorReady, processId = session.ProcessId, sessionId = session.SessionId, mainWindowHandle = session.MainWindowHandle, inspectorStatus = session.InspectorStatus, inspectorReady = session.InspectorReady, }, cli);
        return session.InspectorReady ? 0 : 1;
    }

    private static async Task<int> RunRecordAsync(string[] args, CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var action = cli.GetStringOrNull("action") ?? (args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "status");
        WriteJson(await bridge.RecordAsync(action, cli.GetStringOrNull("name")), cli);
        return 0;
    }

    private static async Task<int> RunGetRecordingAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var state = await bridge.RecordAsync("status");
        if (cli.GetStringOrNull("out") is string path)
        {
            if (state.Workflow == null)
                throw new InvalidOperationException("No recorded workflow.");
            using var file = new StreamWriter(new FileStream(path, FileMode.CreateNew));
            await file.WriteAsync(IpcSerializer.Serialize(state.Workflow));
            WriteJson(new { path = Path.GetFullPath(path), active = state.Active }, cli);
        }
        else
            WriteJson(state, cli);
        return 0;
    }

    private static async Task<int> RunAssertAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var actual = await bridge.AssertElementAsync(await ResolveInputTargetAsync(bridge, cli), cli.GetRequired("property"), cli.GetRequired("expected"));
        WriteJson(new { success = true, actualValue = actual }, cli);
        return 0;
    }

    private static async Task<int> RunReplayAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        var path = cli.GetRequired("file");
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new ArgumentException("Workflow file exceeds 2 MiB.");
        var workflow = IpcSerializer.Deserialize<RecordedWorkflow>(await File.ReadAllTextAsync(path)) ?? throw new ArgumentException("Workflow JSON is required.");
        WorkflowCodec.Validate(workflow);
        await AttachAsync(processManager, cli, false);
        var result = await bridge.ReplayAsync(workflow, cli.GetInt("timeout", 120000));
        WriteJson(result, cli);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunExportTestAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var files = WorkflowTestExporter.Export(await bridge.RecordAsync("status"), cli.GetString("format", "xunit"));
        var directory = cli.GetStringOrNull("out");
        if (directory == null)
            WriteJson(new { files }, cli);
        else
        {
            var fullPath = Path.GetFullPath(directory);
            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
                throw new ArgumentException("Export directory must be new or empty.");
            Directory.CreateDirectory(fullPath);
            foreach (var file in files)
            {
                using var writer = new StreamWriter(new FileStream(Path.Combine(fullPath, file.Key), FileMode.CreateNew));
                await writer.WriteAsync(file.Value);
            }

            WriteJson(new { path = fullPath, files = files.Keys }, cli);
        }

        return 0;
    }

    private static async Task<int> RunReportAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.ReportAsync(cli.GetStringOrNull("root"), cli.GetInt("max", 500), cli.GetInt("depth", 25), cli.GetInt("max-findings", 100)), cli);
        return 0;
    }

    private static async Task<int> RunTreeAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var depth = Math.Clamp(cli.GetInt("depth", 25), 1, 100);
        WriteJson(await bridge.GetVisualTreeAsync(cli.GetStringOrNull("root"), depth), cli);
        return 0;
    }

    private static async Task<int> RunPropsAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.GetElementPropertiesAsync(cli.GetRequired("handle")), cli);
        return 0;
    }

    private static async Task<int> RunFindAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.FindElementsAsync(cli.GetStringOrNull("root"), cli.GetStringOrNull("type"), cli.GetStringOrNull("name"), cli.GetStringOrNull("text"), ParseFilter(cli.GetStringOrNull("filter")), cli.Flags.Contains("visible-only"), cli.GetInt("max", 50), cli.GetStringOrNull("automation-id"));
        WriteJson(result, cli);
        return 0;
    }

    private static async Task<int> RunFindDeepAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var type = cli.GetStringOrNull("type");
        var name = cli.GetStringOrNull("name");
        var text = cli.GetStringOrNull("text");
        var automationId = cli.GetStringOrNull("automation-id");
        if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text) && automationId == null)
            throw new ArgumentException("find-deep requires --type, --name, --text or --automation-id to bound the search.");
        WriteJson(await bridge.FindElementsDeepAsync(cli.GetStringOrNull("root"), type, name, text, ParseFilter(cli.GetStringOrNull("filter")), cli.Flags.Contains("visible-only"), automationId), cli);
        return 0;
    }

    private static async Task<int> RunBindingsAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.GetBindingsAsync(cli.GetRequired("handle")), cli);
        return 0;
    }

    private static async Task<int> RunBindingErrorsAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.GetBindingErrorsAsync(), cli);
        return 0;
    }

    private static async Task<int> RunClearBindingErrorsAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        await bridge.ClearBindingErrorsAsync();
        WriteJson(new { success = true, message = "Binding errors cleared." }, cli);
        return 0;
    }

    private static async Task<int> RunDataContextAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var dc = await bridge.GetDataContextAsync(cli.GetRequired("handle"));
        JsonNode? parsed = null;
        if (!string.IsNullOrEmpty(dc.DataContextJson))
        {
            try
            {
                parsed = JsonNode.Parse(dc.DataContextJson);
            }
            catch
            { /* not valid JSON - fall back to the raw string below */
            }
        }

        WriteJson(new { element = dc.Element, dataContext = (object?)parsed ?? dc.DataContextJson }, cli);
        return 0;
    }

    private static async Task<int> RunResourcesAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var scope = cli.GetString("scope", "application");
        var handle = cli.GetStringOrNull("handle");
        if (scope == "element" && string.IsNullOrEmpty(handle))
            throw new ArgumentException("--handle is required when --scope is 'element'.");
        WriteJson(await bridge.GetResourcesAsync(scope, handle), cli);
        return 0;
    }

    private static async Task<int> RunStylesAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.GetStylesAsync(cli.GetRequired("handle")), cli);
        return 0;
    }

    private static async Task<int> RunWatchPropertyAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var watchId = await bridge.WatchPropertyAsync(cli.GetRequired("handle"), cli.GetRequired("property"));
        WriteJson(new { watchId, note = "Watch registered in the Inspector. Re-run 'props' to read the current " + "value; one-shot CLI invocations do not stream change events.", }, cli);
        return 0;
    }

    private static async Task<int> RunHighlightAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        await bridge.HighlightElementAsync(cli.GetRequired("handle"), cli.GetInt("duration", 2000));
        WriteJson(new { success = true, message = "Element highlighted." }, cli);
        return 0;
    }

    private static async Task<int> RunSetInputModeAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        var mode = cli.GetRequired("mode");
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.SetInputModeAsync(mode), cli);
        return 0;
    }

    private static async Task<int> RunClickAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var handle = await bridge.ResolveElementHandleAsync(cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root"));
        var result = await bridge.ClickElementAsync(handle, cli.Flags.Contains("physical"), cli.GetStringOrNull("click-type"), cli.GetDoubleOrNull("x"), cli.GetDoubleOrNull("y"));
        WriteJson(new { success = true, method = result.Method, elementType = result.ElementType, detail = result.Detail, }, cli);
        return 0;
    }

    private static async Task<int> RunMoveMouseAsync(string command, CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        var options = new MousePointerOptions
        {
            ElementHandle = ElementTarget.Validate(cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root")),
            Action = command == "scroll" ? "scroll" : "move",
            X = cli.GetDoubleOrNull("x"),
            Y = cli.GetDoubleOrNull("y"),
            Delta = cli.GetInt("delta", 0),
            Horizontal = cli.Flags.Contains("horizontal"),
            HoverMs = command == "move-mouse" ? cli.GetInt("hover-ms", 500) : 0
        };
        options.Validate();
        await AttachAsync(processManager, cli, false);
        options.ElementHandle = await ResolveInputTargetAsync(bridge, cli);
        WriteJson(await bridge.MousePointerAsync(options), cli);
        return 0;
    }

    private static async Task<int> RunDragAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        var options = new DragOptions
        {
            ElementHandle = ElementTarget.Validate(cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root")),
            TargetHandle = ElementTarget.ValidateOptional(cli.GetStringOrNull("target"), cli.GetStringOrNull("target-automation-id"), cli.GetStringOrNull("target-root")),
            DeltaX = cli.GetDoubleOrNull("delta-x"),
            DeltaY = cli.GetDoubleOrNull("delta-y"),
            StartX = cli.GetDoubleOrNull("start-x"),
            StartY = cli.GetDoubleOrNull("start-y"),
            TargetX = cli.GetDoubleOrNull("target-x"),
            TargetY = cli.GetDoubleOrNull("target-y"),
            Button = cli.GetString("button", "left"),
            DurationMs = cli.GetInt("duration", 500)
        };
        options.Validate();
        await AttachAsync(processManager, cli, false);
        options.ElementHandle = await ResolveInputTargetAsync(bridge, cli);
        options.TargetHandle = await ElementTarget.ResolveOptionalAsync(bridge, cli.GetStringOrNull("target"), cli.GetStringOrNull("target-automation-id"), cli.GetStringOrNull("target-root"));
        var result = await bridge.DragAsync(options);
        WriteJson(new { success = true, result, coordinateSpace = "screen_pixels" }, cli);
        return 0;
    }

    private static async Task<int> RunTouchAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        var options = new TouchGestureOptions
        {
            ElementHandle = ElementTarget.Validate(cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root")),
            Gesture = cli.GetRequired("gesture"),
            X = cli.GetDoubleOrNull("x"),
            Y = cli.GetDoubleOrNull("y"),
            DeltaX = cli.GetDoubleOrNull("delta-x"),
            DeltaY = cli.GetDoubleOrNull("delta-y"),
            Scale = cli.GetDoubleOrNull("scale"),
            AngleDegrees = cli.GetDoubleOrNull("angle"),
            Radius = cli.GetDoubleOrNull("radius"),
            DurationMs = cli.GetIntOrNull("duration")
        };
        options.Validate();
        await AttachAsync(processManager, cli, false);
        options.ElementHandle = await ResolveInputTargetAsync(bridge, cli);
        var result = await bridge.TouchGestureAsync(options);
        WriteJson(new { success = true, result, coordinateSpace = "screen_pixels" }, cli);
        return 0;
    }

    private static async Task<int> RunSelectItemAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var itemText = cli.GetStringOrNull("item-text");
        var index = cli.GetIntOrNull("index");
        if (string.IsNullOrEmpty(itemText) && index is null)
            throw new ArgumentException("select-item requires --item-text or --index.");
        var result = await bridge.SelectItemAsync(await ResolveInputTargetAsync(bridge, cli), itemText, index);
        WriteJson(new { success = true, method = result.Method, elementType = result.ElementType, detail = result.Detail, }, cli);
        return 0;
    }

    private static async Task<int> RunSetTextAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.SetTextAsync(await ResolveInputTargetAsync(bridge, cli), cli.GetRequired("text"), cli.Flags.Contains("physical"));
        WriteJson(new { success = true, method = result.Method, elementType = result.ElementType, detail = result.Detail, }, cli);
        return 0;
    }

    private static async Task<int> RunSendKeysAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.SendKeysAsync(await ElementTarget.ResolveOptionalAsync(bridge, cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root")), cli.GetRequired("keys"));
        WriteJson(new { success = true, method = result.Method, elementType = result.ElementType, detail = result.Detail, }, cli);
        return 0;
    }

    private static async Task<int> RunWaitForAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var type = cli.GetStringOrNull("type");
        var name = cli.GetStringOrNull("name");
        var text = cli.GetStringOrNull("text");
        var automationId = cli.GetStringOrNull("automation-id");
        if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text) && automationId == null)
            throw new ArgumentException("wait-for requires --type, --name, --text or --automation-id to identify the element.");
        WriteJson(await bridge.WaitForElementAsync(cli.GetStringOrNull("root"), type, name, text, cli.GetString("condition", "visible"), cli.GetInt("timeout", 10000), cli.GetInt("poll", 250), automationId), cli);
        return 0;
    }

    private static async Task<int> RunSetPropertyAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.SetPropertyAsync(await ResolveInputTargetAsync(bridge, cli), cli.GetRequired("property"), cli.GetString("value", ""));
        WriteJson(new { success = true, elementType = result.ElementType, appliedValue = result.AppliedValue, valueType = result.ValueType, previousSource = result.PreviousSource, }, cli);
        return 0;
    }

    private static async Task<int> RunRevertPropertyAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        if (cli.Flags.Contains("all") && (cli.GetStringOrNull("automation-id") != null || cli.GetStringOrNull("root") != null))
            throw new ArgumentException("--all cannot be combined with --automation-id or --root; omit --all to revert an edit on that control.");
        await AttachAsync(processManager, cli, false);
        var result = await bridge.RevertPropertyAsync(cli.Flags.Contains("all"), await ElementTarget.ResolveOptionalAsync(bridge, cli.GetStringOrNull("handle"), cli.GetStringOrNull("automation-id"), cli.GetStringOrNull("root")), cli.GetStringOrNull("property"));
        WriteJson(new { success = true, revertedCount = result.RevertedCount, revertedHandle = result.RevertedHandle, revertedProperty = result.RevertedProperty, pendingCount = result.PendingCount, }, cli);
        return 0;
    }

    private static async Task<int> RunEvaluateBindingAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.EvaluateBindingAsync(cli.GetRequired("handle"), cli.GetRequired("property"));
        Console.Out.WriteLine(string.IsNullOrEmpty(result.Json) ? "{}" : result.Json!);
        return 0;
    }

    private static async Task<int> RunExplainTriggersAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.ExplainTriggersAsync(cli.GetRequired("handle"), cli.GetStringOrNull("property"));
        Console.Out.WriteLine(string.IsNullOrEmpty(result.Json) ? "{}" : result.Json!);
        return 0;
    }

    private static async Task<int> RunSnapshotAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.SnapshotAsync(cli.GetStringOrNull("handle"), cli.GetStringOrNull("label"), Math.Clamp(cli.GetInt("depth", 25), 1, 100));
        WriteJson(new { success = true, label = result.Label, elementCount = result.ElementCount }, cli);
        return 0;
    }

    private static async Task<int> RunDiffAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var result = await bridge.DiffAsync(cli.GetRequired("before"), cli.GetRequired("after"));
        // Emit the diff document verbatim (it's already JSON), wrapped with counts.
        var doc = string.IsNullOrEmpty(result.Json) ? "{}" : result.Json!;
        Console.Out.WriteLine(doc);
        return 0;
    }

    private static async Task<int> RunLayoutAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        WriteJson(await bridge.GetLayoutInfoAsync(cli.GetRequired("handle")), cli);
        return 0;
    }

    private static async Task<int> RunExportAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var format = cli.GetString("format", "json").ToLowerInvariant();
        if (format != "json" && format != "xaml")
            throw new ArgumentException("--format must be 'json' or 'xaml'.");
        var export = await bridge.ExportTreeAsync(cli.GetStringOrNull("handle"), format);
        var outPath = cli.GetStringOrNull("out");
        if (!string.IsNullOrEmpty(outPath))
        {
            await File.WriteAllTextAsync(outPath, export.Content);
            WriteJson(new { path = Path.GetFullPath(outPath), format = export.Format, elementCount = export.ElementCount, }, cli);
        }
        else
        {
            WriteJson(new { format = export.Format, elementCount = export.ElementCount, content = export.Content, }, cli);
        }

        return 0;
    }

    private static async Task<int> RunScreenshotAsync(CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge)
    {
        await AttachAsync(processManager, cli, false);
        var fullContent = cli.Flags.Contains("full-content");
        var maxW = Math.Clamp(cli.GetInt("max-width", 1920), 1, fullContent ? 16384 : 3840);
        var maxH = Math.Clamp(cli.GetInt("max-height", 1080), 1, fullContent ? 16384 : 2160);
        var shotMode = cli.GetString("mode", "auto").ToLowerInvariant();
        if (shotMode != "auto" && shotMode != "render" && shotMode != "screen")
            throw new ArgumentException("--mode must be 'auto', 'render' or 'screen'.");
        if (fullContent && shotMode == "screen")
            throw new ArgumentException("--full-content is supported with --mode render only.");
        if (fullContent && shotMode == "auto")
            shotMode = "render";
        var shot = await bridge.CaptureScreenshotAsync(cli.GetStringOrNull("handle"), maxW, maxH, shotMode, fullContent);
        if (string.IsNullOrEmpty(shot.ImageBase64))
            throw new InvalidOperationException("Screenshot capture returned no image data.");
        var pid = processManager.CurrentSession!.ProcessId;
        var outPath = cli.GetStringOrNull("out") ?? Path.Combine(Environment.CurrentDirectory, $"wpf-screenshot-{pid}.png");
        await File.WriteAllBytesAsync(outPath, Convert.FromBase64String(shot.ImageBase64));
        WriteJson(new { path = Path.GetFullPath(outPath), width = shot.Width, height = shot.Height, elementType = shot.ElementType ?? "Window", captureMode = shot.CaptureMode, detail = shot.Detail, }, cli);
        return 0;
    }

    private static Task<int> DispatchCommandAsync(string command, string[] args, CliArgs cli, ProcessManager processManager, NamedPipeBridge bridge) => command switch
    {
        "update" => RunUpdateAsync(args, cli),
        "list" => RunListAsync(cli, processManager),
        "attach" => RunAttachAsync(cli, processManager),
        "record" => RunRecordAsync(args, cli, processManager, bridge),
        "get-recording" => RunGetRecordingAsync(cli, processManager, bridge),
        "assert" => RunAssertAsync(cli, processManager, bridge),
        "replay" => RunReplayAsync(cli, processManager, bridge),
        "export-test" => RunExportTestAsync(cli, processManager, bridge),
        "report" => RunReportAsync(cli, processManager, bridge),
        "tree" => RunTreeAsync(cli, processManager, bridge),
        "props" => RunPropsAsync(cli, processManager, bridge),
        "find" => RunFindAsync(cli, processManager, bridge),
        "find-deep" => RunFindDeepAsync(cli, processManager, bridge),
        "bindings" => RunBindingsAsync(cli, processManager, bridge),
        "binding-errors" => RunBindingErrorsAsync(cli, processManager, bridge),
        "clear-binding-errors" => RunClearBindingErrorsAsync(cli, processManager, bridge),
        "data-context" => RunDataContextAsync(cli, processManager, bridge),
        "resources" => RunResourcesAsync(cli, processManager, bridge),
        "styles" => RunStylesAsync(cli, processManager, bridge),
        "watch-property" => RunWatchPropertyAsync(cli, processManager, bridge),
        "highlight" => RunHighlightAsync(cli, processManager, bridge),
        "set-input-mode" => RunSetInputModeAsync(cli, processManager, bridge),
        "click" => RunClickAsync(cli, processManager, bridge),
        "move-mouse" or "scroll" => RunMoveMouseAsync(command, cli, processManager, bridge),
        "drag" => RunDragAsync(cli, processManager, bridge),
        "touch" => RunTouchAsync(cli, processManager, bridge),
        "select-item" => RunSelectItemAsync(cli, processManager, bridge),
        "set-text" => RunSetTextAsync(cli, processManager, bridge),
        "send-keys" => RunSendKeysAsync(cli, processManager, bridge),
        "wait-for" => RunWaitForAsync(cli, processManager, bridge),
        "set-property" => RunSetPropertyAsync(cli, processManager, bridge),
        "revert-property" => RunRevertPropertyAsync(cli, processManager, bridge),
        "evaluate-binding" => RunEvaluateBindingAsync(cli, processManager, bridge),
        "explain-triggers" => RunExplainTriggersAsync(cli, processManager, bridge),
        "snapshot" => RunSnapshotAsync(cli, processManager, bridge),
        "diff" => RunDiffAsync(cli, processManager, bridge),
        "layout" => RunLayoutAsync(cli, processManager, bridge),
        "export" => RunExportAsync(cli, processManager, bridge),
        "screenshot" => RunScreenshotAsync(cli, processManager, bridge),
        _ => throw new ArgumentException($"Unknown command '{command}'. Run '{Exe} help' for usage.")
    };
}
