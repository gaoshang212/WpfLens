# Background input

Select the target by PID or process name. Pass `--pid` or `--process` on each CLI
command. MCP uses the attached process, and the SDK uses its `WpfApplication` instance.

## Usage

This feature is unreleased. Use the server built from this branch and confirm that
`set-input-mode --help` lists both `foreground` and `background`. Older CLI versions
may silently ignore unknown arguments.

```powershell
$wpflens = ".\src\WpfVisualTreeMcp.Server\bin\Release\net8.0\WpfVisualTreeMcp.Server.exe"
& $wpflens set-input-mode --pid 1234 --mode background
& $wpflens click --pid 1234 --automation-id SaveButton --physical
& $wpflens move-mouse --pid 1234 --automation-id HoverButton
& $wpflens set-input-mode --pid 1234 --mode foreground
& $wpflens screenshot --pid 1234 --mode screen --out foreground.png
& $wpflens set-input-mode --pid 1234 --mode background
& $wpflens set-text --pid 1234 --automation-id SearchBox --physical --text 'Background input'
& $wpflens set-input-mode --pid 1234 --mode foreground
```

To enable background input, call `wpf_set_input_mode(mode="background")` in MCP
or `await app.SetInputModeAsync("background")` in the SDK. All callers targeting the
same PID share the mode. The initial mode is `foreground`.

In background mode, physical clicks and typing, coordinate clicks, double-clicks,
right-clicks, pointer movement, scrolling, dragging and keystrokes use background
input. Default single clicks and text entry use UI Automation or the control's
existing input path. Background keystrokes require an explicit element handle or
AutomationId, regardless of desktop keyboard focus.

**MCP clients must release the target window's mouse and keyboard before returning
control to the user.** After the last background action and verification, call
`wpf_set_input_mode(mode="foreground")` on the same target and confirm that the
response reports `mode="foreground"`. Run this cleanup even if an action or replay
fails or is cancelled. If MCP disconnects, reattach to the same running PID and
retry the foreground call. If cleanup still fails, report the error to the user.

Switching back to foreground releases the background binding. Screen capture and
physical input can then activate the target window as needed. The mode switch itself
does not restore focus to another application. Switch to foreground before touch
input or a `screen` capture, including when `auto` selects `screen`. A `render`
capture works in background mode. A failed action leaves the selected mode in effect.

## Binding lifetime

The first background input action binds the target window. The binding preserves
virtual hover and keyboard focus across commands and queries. Each Inspector can bind
one WPF window handle (HWND) at a time. MCP, CLI and SDK callers targeting the same
PID share the binding, and any of them can release it. Each PID maintains its own
state. The interaction lock serializes input actions, so commands from different
callers can still interleave between actions.

**The binding has no idle timeout.** It persists after a CLI command exits, an SDK
client calls `Dispose`, or MCP disconnects. To release a binding left by an earlier
client, switch the same PID to foreground. Repeating that call is safe. While bound,
the target window suppresses some physical input. Wait until release before
operating it manually. Other applications should continue receiving input normally.
The Inspector also attempts cleanup when the window is destroyed or the Inspector
shuts down. If cleanup fails, it reports the error and blocks further input.

End the current background session before targeting another HWND or using foreground
physical input. Each completed input action releases the keys and mouse buttons it
pressed, even while the binding stays active.

## Supported operations and limitations

| Operation or scenario | Background input support |
| --- | --- |
| Click, move, hover, scroll, type and send keys | Supported on standard WPF controls, including when another application covers the window. |
| Mouse drag | Supported within one WPF HWND, for example when dragging a slider thumb. A successful call confirms input delivery only. |
| OLE drag and drop (`DragDrop.DoDragDrop`) | Foreground only. A background OLE call on the bound thread is cancelled. The input command that triggered it fails and runs cleanup. |
| Hover and focus across commands | Preserved within the bound window. Application events can still change either state. |
| Touch | Foreground physical touch only. Gosf 0.1.1 has no touch API. |
| Drag across HWNDs | Foreground physical drags between windows in the same process only. |
| `HwndHost`, separate popup HWNDs and windows on different UI threads | Unsupported. This includes popup menus hosted in a separate HWND. |
| Minimized or hidden windows, ARM64 | Unsupported. |
| System shortcuts, IME, AltGr and dead keys | Support is not guaranteed. The Inspector rejects recognized system shortcuts. |

Background requests never fall back to foreground automatically. Verify the effect
of an action with a property query, wait or assertion. Cancellation cannot undo
input already delivered, and failed actions are not retried automatically.

## Recording and replay

During recording, each successful explicit mode switch produces a `SetInputMode`
step with `arguments.mode` set to `foreground` or `background`. If recording starts
in background mode, the first step records that initial mode.

Input steps store their own arguments. For clicks and text entry, `physical`
specifies whether to simulate mouse or keyboard input. The recorder skips screenshots,
so switching to foreground for a screenshot and immediately back to background
produces two adjacent mode switches in the workflow JSON.

All workflows use `schemaVersion` 1. When a workflow contains a `SetInputMode`
step, replay checks that the Inspector supports mode switching before executing
any steps. Steps before the first switch use foreground input. After replay,
the target retains the last mode set.

## Build from source

Run `dotnet build WpfVisualTreeMcp.sln` to restore `Gosf.Input.Automation 0.1.1`
from NuGet and build the solution. The server and SDK distributions include the
dependency's managed assembly, x86 and x64 native libraries, and license.

## Validation

Run `python scripts/test_background_input.py` to test net472, net48 and net8 on
both x86 and x64. The tests start a target window and a covering window, then
clean them up when finished.

Keep the desktop idle in strict mode so the test can verify that the foreground
HWND and pointer coordinates stay unchanged. With `--allow-desktop-activity`, the
desktop check verifies only that the target window was not activated. Add
`--foreground` to also test physical input after the binding is released. Logs
are saved in `artifacts/background-input-tests`.

See the [validation record](BACKGROUND_INPUT_VALIDATION.md) for the configurations
tested and their results.
