# Getting Started

This guide will help you set up and start using WpfLens to inspect WPF applications with AI coding agents.

## Prerequisites

Before you begin, ensure you have:

- **Windows 10/11** - WPF applications only run on Windows
- **.NET 8.0 SDK** - [Download here](https://dotnet.microsoft.com/download/dotnet/8.0)
- **A WPF application** - Either your own or the included sample app
- **An MCP-compatible AI agent** - Claude Code, Cursor, or similar

## Installation

### Option 1: Build from Source

1. Clone the repository:
   ```bash
   git clone https://github.com/gaoshang212/WpfLens.git
   cd WpfLens
   ```

2. Build the solution:
   ```bash
   dotnet build
   ```

3. The MCP server will be available at:
   ```
   src/WpfVisualTreeMcp.Server/bin/Debug/net8.0/WpfVisualTreeMcp.Server.exe
   ```

### Option 2: .NET Tool Installation

```bash
dotnet tool install -g WpfLens.Mcp
```

### Option 3: Codex plugin

Install the MCP configuration and companion skill together using the
[Codex plugin instructions](../README.md#codex). This method requires
the .NET 10 SDK in addition to the .NET 8 runtime and resolves the latest stable
server from NuGet. Install from the Git marketplace. Plugin and skill updates
use the marketplace, separately from CLI updates.

### Updating an installation

```powershell
wpflens update --check
wpflens update
wpflens update --status
```

For a portable copy, invoke `WpfVisualTreeMcp.Server.exe update` from that
installation's `server` folder. A global tool can also be updated with
`dotnet tool update -g WpfLens.Mcp`; a portable copy can be replaced by extracting
the latest release.

The command updates **only the installation it was launched from**. Global and
custom `--tool-path` tools download the exact official NuGet package, then use
`dotnet tool update` from that staged package (a .NET SDK is required). Temporary
NuGet download failures and publication delays receive up to four attempts, with
10, 20 and 30 seconds between attempts. If preparation fails, installed files are
unchanged; retry later. Portable folders receive the latest stable GitHub release's complete
server payload, verified against its SHA-256 digest. A source build under `bin`
is left to the source/build workflow; local manifest tools and `dnx` caches are
left to their package manager. `--check` explains which installation was detected.

On Windows, installation starts in a hidden worker **after the CLI exits** so its
executable is unlocked. A `scheduled` result means the worker was launched, not
that installation finished. Run `update --status` until it reports `updated` or
`failed`; a failed status returns exit code 1. JSON output includes the log path.
Status, logs and portable backups are stored under
`%LOCALAPPDATA%\WpfLens\updates`, separately for each installation.

Close other MCP instances using the same files before updating. The updater does
not stop applications: occupied files cause an error, and portable replacement
failures trigger restoration from backup. Unrelated files and MCP configuration
are preserved. After success, restart the MCP client and previously inspected WPF
apps so both server and injected Inspector use the new version. Other installed
copies and any separately copied self-hosted Inspector require their own update.

## Configuration

### Claude Code

There are multiple ways to configure the MCP server in Claude Code:

#### Option 1: Command Line (Recommended)

Use the `claude mcp add` command for quick setup:

```bash
# Build the server first
cd WpfLens
dotnet build -c Release

# Add to current project only
claude mcp add wpflens -- C:/path/to/WpfVisualTreeMcp/src/WpfVisualTreeMcp.Server/bin/Release/net8.0/WpfVisualTreeMcp.Server.exe

# Or add globally (available in all projects)
claude mcp add --scope user wpflens -- C:/path/to/WpfVisualTreeMcp/src/WpfVisualTreeMcp.Server/bin/Release/net8.0/WpfVisualTreeMcp.Server.exe
```

Verify the server was added:
```bash
claude mcp list
```

Remove the server if needed:
```bash
claude mcp remove wpflens
```

#### Option 2: JSON Configuration

Create or edit `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "wpflens": {
      "command": "C:/path/to/WpfVisualTreeMcp/src/WpfVisualTreeMcp.Server/bin/Release/net8.0/WpfVisualTreeMcp.Server.exe",
      "args": []
    }
  }
}
```

For user-wide configuration, merge the MCP entry into `~/.claude.json`, preserving other settings, or use the `--scope user` command above.

Restart Claude Code or reload the window after making changes.

### Cursor

1. Create or edit `.cursor/mcp.json` in your project:

   ```json
   {
     "mcpServers": {
       "wpflens": {
         "command": "C:/path/to/WpfVisualTreeMcp.Server.exe",
         "args": []
       }
     }
   }
   ```

2. Restart Cursor.

### VS Code with Continue

1. Edit your Continue configuration (`~/.continue/config.json`):

   ```json
   {
     "mcpServers": [
       {
         "name": "wpflens",
         "command": "C:/path/to/WpfVisualTreeMcp.Server.exe"
       }
     ]
   }
   ```

## Setting Up Your WPF Application (Self-Hosted Mode)

Self-hosting is an alternative to automatic injection when you can modify the target application. Reference the Inspector in your project and initialize it at startup as shown below.

### Step 1: Add Project Reference

Add a reference to `WpfVisualTreeMcp.Inspector` in your WPF project:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/WpfVisualTreeMcp.Inspector/WpfVisualTreeMcp.Inspector.csproj" />
</ItemGroup>
```

### Step 2: Initialize the Inspector

In your `App.xaml.cs`, initialize the inspector on startup:

```csharp
using System.Diagnostics;
using System.Windows;
using WpfVisualTreeMcp.Inspector;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize the WPF Visual Tree Inspector
        // This creates a named pipe server for the MCP server to connect to
        InspectorService.Initialize(Process.GetCurrentProcess().Id);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean up the inspector service
        InspectorService.Instance?.Dispose();
        base.OnExit(e);
    }
}
```

### Step 3: Build and Run

Build your application. The inspector will start automatically and listen for MCP server connections.

## Quick Start Tutorial

### Step 1: Start a WPF Application

Either start your own WPF application (with the Inspector set up as above) or use the included sample:

```bash
cd WpfLens
dotnet run --project samples/SampleWpfApp
```

The sample app already has the Inspector configured.

### Step 2: List Available Processes

Ask your AI agent to list WPF processes:

```
Show me all running WPF applications I can inspect.
```

Expected response:
```
Found 1 WPF application:
- SampleWpfApp (PID: 1234) - "Sample WPF Application"
```

### Step 3: Attach to the Application

```
Attach to SampleWpfApp so I can inspect it.
```

### Step 4: Explore the Visual Tree

```
Show me the visual tree of the main window.
```

This will display the hierarchy of UI elements.

### Step 5: Inspect an Element

```
Show me the properties of the SubmitButton.
```

### Step 6: Find Binding Errors

```
Are there any binding errors in this application?
```

## Common Tasks

### Debugging Layout Issues

```
Get the layout information for the ContentGrid element.
What are the actual sizes and margins?
```

### Finding Elements

```
Find all TextBox elements in the application.
Find elements where IsEnabled is false.
```

### Analyzing Bindings

```
Show me all bindings on the UserNameTextBox.
What is the binding source for the SelectedItem property?
```

### Checking Resources

```
List all application-level resources.
What styles are defined in this application?
```

## Troubleshooting

### "No WPF applications found"

- Ensure the target application is running
- Check that it's a .NET Framework WPF application
- The application must have a main window visible

### "Failed to attach to process" or "Inspector not loaded"

- Ensure the target application has the Inspector DLL set up (self-hosted mode)
- The application must call `InspectorService.Initialize()` on startup
- Check that the Inspector project reference is correctly added

### "Connection timeout" or "Communication error"

- The Inspector's named pipe server may not be running
- Verify the application started successfully and called `InspectorService.Initialize()`
- Check the pipe name: `wpf_inspector_{processId}`

### "Element not found"

- The element may have been removed from the visual tree
- Try refreshing by getting the visual tree again
- The element handle may have expired (handles are session-based)

### "Binding path error"

- The binding path contains a typo
- The source property doesn't exist
- The DataContext is null

## Best Practices

### Mouse dragging and touch gestures

Find a visible source/target with `wpf_find_elements`, then use `wpf_drag` for a mouse
drag or `wpf_touch_gesture` for `tap`, `long_press`, `swipe`, `pinch`, or `rotate`.
CLI users can run `drag --help` and `touch --help` for equivalent options.

```powershell
WpfVisualTreeMcp.Server.exe drag --pid 1234 --handle elem_42 --delta-x 80
WpfVisualTreeMcp.Server.exe touch --pid 1234 --handle elem_57 --gesture pinch --scale 1.5
```

These operations require an interactive desktop and a visible, unobstructed target.
Coordinates are local WPF DIPs. Verify the application's state after input. For touch,
the app must handle touch/manipulations and its input stack must accept injected
contacts; see the [compatibility setup](TOOLS_REFERENCE.md#touch-compatibility-and-verification).
The sample's **Drag demo** and **Touch demo** provide controls and live telemetry.

1. **Start with the visual tree** - Get an overview before diving into specific elements
2. **Use element search** - Don't manually navigate large trees
3. **Check binding errors first** - Many UI issues are caused by broken bindings
4. **Export for offline analysis** - Large trees can be exported to JSON

## Next Steps

- Read the [Tools Reference](TOOLS_REFERENCE.md) for complete API documentation
- Explore the [Architecture](ARCHITECTURE.md) to understand how it works
- Try the sample application with intentional binding errors
- Integrate into your development workflow
