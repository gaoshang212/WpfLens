# Background input design

The implementation follows the agreed design. The changes have yet to be committed or released. Test results are in the [validation record](BACKGROUND_INPUT_VALIDATION.md).

## Scope and baseline

- Implement background input in WpfLens using `Gosf.Input.Automation`. The current dependency version is `0.1.1`.
- The `background-input` branch starts at commit `08b5e9cea7b70e18df5bab43d8419dd6f10e243d`.
- The working tree is at `D:\workspace\sources\WpfLens-background-input`.
- The reference implementation is at `D:\workspace\orca\worktrees\WPFVisualTreeMcp\background-input`.

## Investigation findings

At the baseline, the main branch had no contract for background input. Click and SetText accepted a Physical parameter. SendKeys, coordinate clicks, double-clicks and right-clicks used physical input. Adding a mode affects MCP, the CLI, the C# SDK, shared IPC, the Inspector, and workflow recording and replay. Older Inspectors may ignore unknown fields, so callers need a capability handshake before sending input. Otherwise an older Inspector could execute a background request in the foreground.

The reference implementation uses Gosf's DirectInput mouse and keyboard backends for standard WPF controls. While bound, it suppresses physical mouse messages to the target. It rejects minimized windows, drags across HWNDs, `HwndHost` and separate popup HWNDs.

The initial investigation used `Gosf.Input.Automation 0.1.0`. Its nuspec points to upstream commit `4d5ccf437c4ff99e7c7a34dc85adecd9a844929c`. The current `0.1.1` package contains managed assemblies for net462 and net8 and native components for x86 and x64. The caller and target must have the same bitness. `InputSession` is not thread-safe. Its public input methods return a synchronous `bool` and accept no cancellation token.

## Risks and limits of the evidence

- The reference `AutomationInput.cs` creates, binds and disposes a session for each action. This gives no guarantee that virtual focus or hover survives between commands.
- In upstream `0.1.0`, `InputHookClient.cpp` takes no additional reference to an already loaded module, and `HookExport.cpp` eventually releases the module. Source inspection confirmed that sequence. A crash has yet to be reproduced.
- In upstream `0.1.0`, `BindingSession.cpp` clears local state on reset and ignores the backend's unbind result. The public API can therefore report success without confirming that cleanup succeeded.
- `TypeText` processes the entire string synchronously, and the underlying message delivery can block. Deadline handling and cancellation during a call require separate verification.
- The reference document `docs/BACKGROUND_INPUT_REFACTORING_PLAN.md` describes unfinished repairs. Its historical desktop logs differ from the current test source, so those logs cannot establish that the current implementation passes the full net472/net48/net8 matrix on x86 and x64.

## Design decisions

- Background input must never activate the target, move the system pointer or interfere with input to other applications. If these conditions cannot be met, fail explicitly. Automatic fallback to foreground input is prohibited.
- Preserve existing default behavior. Callers must explicitly select background mode.
- Use the published NuGet package `Gosf.Input.Automation 0.1.1`. It includes the fixes needed for in-process binding, recovery from unbind failures and rejection of background OLE drag and drop.
- Cover the six input operations across MCP, CLI, the SDK, recording and replay. Assess touch separately and identify capabilities that are still unimplemented.
- Check existing cross-window drag behavior before applying any restriction. Background constraints apply only to the background path.
- Keep the binding across commands and add the session management this requires. Within the bound window, virtual hover and focus must survive queries between input actions so the interaction can continue.
- Bind automatically on the first background action and provide an explicit way to end the session. Use no idle timeout.
- Limit changes to the contracts, execution paths, lifecycle management and validation required for background input. Keep the existing foreground backends.

## Why the binding persists across commands

After Gosf binds a window, the first synthetic input activates WPF's virtual input state. From then on, Gosf suppresses physical mouse messages and some focus and capture messages on the target UI thread. That suppression continues even with `LockInput(None)`. Unbinding releases virtual capture, sends focus-loss and MouseLeave notifications, and resets the state. The public API has no suspend, resume or state snapshot operation.

Consider moving the pointer to open a hover menu, querying the menu, then clicking an item. Unbinding after each command can clear hover before the click. Keeping the binding lets this sequence continue, but the user cannot reliably operate the same window with a physical mouse while it is bound. Other applications should continue to receive input normally.

After reviewing this trade-off, the user confirmed that a persistent binding was required and approved the necessary lifecycle management. Automation has exclusive use of the target window's input while it is bound.

The binding preserves state within a supported window. Separate popup HWNDs, cross-window drags and windows on multiple UI threads fall outside that scope. Hover and focus persistence must be verified with real controls. Each completed action must release the keys and mouse buttons it pressed, even while the session stays bound.

### Session lifecycle

The first background action establishes the binding. A successful action leaves it active until a caller explicitly ends the session. Queries leave its lifetime unchanged. The design has no idle timer, configurable timeout, heartbeat, lease or client-disconnect detection for the binding.

The target Inspector owns the session so it can span one-shot CLI invocations. Ordinary IPC closes the pipe after each request, and SDK Detach and Dispose do not notify the Inspector to release input. If a controller exits or omits cleanup, a later caller can explicitly end the binding.

Clean up when the target window is destroyed or the Inspector stops or is disposed. In-process state ends when the target process exits. If input execution fails, attempt cleanup and report the outcome. Parameter validation failures leave an existing session intact. If cleanup fails, retain enough state to diagnose it and reject further input until recovery is confirmed.

## Implementation plan

### State and concurrency

Each Inspector holds at most one background binding and tracks its Gosf `InputSession` and target HWND. MCP, CLI and SDK clients attached to the same target share that state. Any client can reuse or explicitly end the binding. There are no private client sessions, owner tokens or isolation between controllers.

Hold the existing interaction gate for one input or session-end operation. Release the gate between commands while keeping the Gosf binding. Queries can run, and subsequent input can reuse the binding. Reject a background request for a different HWND and require the caller to end the current session before switching windows. Foreground physical input also requires release first to avoid a conflict with the background hook.

### Public interfaces

- The click, move-mouse, scroll, drag, set-text and send-keys commands route physical input according to the target process's mode. Mode switching, screenshots and workflow recording are described in the [background input guide](INPUT_MODES.md).
- MCP's `wpf_set_input_mode`, the CLI's `set-input-mode` and the SDK's `SetInputModeAsync` share the target Inspector's mode. Switching to foreground releases the binding. Recording and replay represent a switch as `SetInputMode`.
- Background keystrokes require an explicit target element. The current desktop focus cannot supply a missing target.
- The capability handshake covers both background input and session release. If an older Inspector lacks the capability, reject the request before sending any input.

### Changes by component

| Component | Required changes |
| --- | --- |
| Shared IPC and workflow contracts | Represent background mode, the release request and capability fields. Validate parameters and check workflow compatibility. |
| Inspector | Centralize Gosf binding management and background execution. Integrate with the interaction gate, the existing Dispatcher and worker responsibilities, and lifecycle cleanup. |
| Shared InspectorBridge | Forward mode switches, check capabilities and request foreground permission for replay only when a foreground step needs physical input. |
| MCP, CLI and SDK | Expose mode selection and explicit release through the shared bridge so all three use the same input implementation. |
| Recording, replay and test export | Preserve mode changes and explicit release. Reuse the binding across replay steps and have generated tests explicitly clean up in a `finally` block. |
| Project references and injection packaging | Include Gosf and the native components for each architecture. Verify dependency resolution after injection on .NET Framework and CoreCLR. |

Keep the existing `NativeMouseInput`, `NativeTouchInput` and default control interaction paths. Select background execution at input dispatch. Keep Gosf specific to background input and avoid a separate strategy hierarchy for each of the six operations. Follow repository conventions for readable control flow, extracting only what the change requires from long methods.

### Windows foreground permission

The bridge can call `AllowSetForegroundWindow(targetPid)` before sending a request. A successful call lets the target process request activation. The target's own code can use that permission too. Windows controls how long the grant lasts. [Windows API reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)

For replay, the Inspector holds the interaction gate for the whole workflow. The bridge can therefore follow the recorded mode switches in order. It requests permission once before dispatch if a foreground step uses physical mouse, keyboard or touch input. A final switch to foreground solely to release the binding requires no grant.

For a single physical input or screen/auto capture request, the bridge may request permission before dispatch. The Inspector owns the mode shared by all clients and resolves the capture backend. It selects background delivery or rejects foreground-only operations while in background mode. Reading or caching the mode in the client would race with switches from other clients.

## Background capabilities outside the initial scope

| Capability | Evidence and initial behavior |
| --- | --- |
| Touch | The public API in Gosf.Input.Automation 0.1.1 provides mouse and keyboard input. The existing physical touch path remains available. |
| OLE drag and drop | Desktop testing showed that entering the native OLE drag-and-drop loop can block. Cancel OLE at the native entry point on the bound thread and propagate the failure to the input command. Foreground OLE and ordinary background Slider/Thumb drags remain available. |
| Drags across HWNDs | The reference `BackgroundInput.cs` requires both endpoints to share an HWND. There is no verified implementation for cross-HWND background drags. The existing physical drag path remains available. |
| Input to HwndHost, native child windows or separate popup HWNDs | The reference implementation explicitly rejects these targets. No supported path has passed verification. |
| Minimized or hidden targets | The design requires a visible, non-minimized window. A window covered by another window can still meet those conditions. |

### Existing cross-window physical drags

In the baseline implementation, `PrepareDrag` stores the start and end HWNDs separately. `NativeMouseInput` validates each endpoint, and `PhysicalInputGuard` checks that the HWND at each point belongs to the target process. The endpoints can have different HWNDs. The code therefore permits physical drags across WPF windows and popups in the same attached process. Arbitrary cross-process drags fall outside that scope.

The existing desktop cases in `test_drag.py` cover OLE drop, slider and splitter interactions within one `DragDemoWindow`. The investigation found no cross-window desktop case. Cross-window behavior has source-level evidence only. New background restrictions must preserve the existing physical path.

## Implementation and acceptance criteria

1. Verify the published Gosf dependency's binding lifetime, cleanup behavior and injection payloads.
2. Add explicit background mode selection and parameter validation to the shared input contract. Use the same capability handshake for MCP, CLI and SDK. Reject unsupported Inspectors, unsupported operations and conflicting arguments before sending input.
3. Implement background execution and persistent sessions in the Inspector using the existing interaction gate, Dispatcher and worker. For each command, resolve the explicit target, validate the bound HWND, prepare and execute the input, then release the keys and buttons pressed by that action. Release the gate between commands so later operations can proceed. Keep the binding until the session ends.
4. Treat a successful action as confirmation of input delivery only. Preserve readback after text writes and verify application outcomes with existing waits and assertions. Cancellation cannot undo delivered input. Do not automatically retry a failed or timed-out action. Report cleanup failures and withhold any claim of recovery or overall success until cleanup succeeds.
5. Preserve the mode and explicit release in recordings, exports and replay. Check capabilities and workflow compatibility before replay so an older endpoint cannot silently deliver input in the foreground. Replay has no implicit idle release. Exported standalone tests must clean up in a `finally` block.
6. Update the tool reference, CLI help and repository plugin skill. Identify which capabilities are supported, which are unimplemented and which have been verified.
7. Run targeted tests for parameters, serialization, the capability handshake, session lifetime, cleanup, recording and replay. Desktop acceptance covers net472, net48 and net8 on x86 and x64. Check input to a covered target, hover and focus across commands, and manual input after release. Verify that background commands leave the foreground HWND and system pointer unchanged and do not redirect input intended for other applications. Record any untested or failing combination explicitly. A successful build alone cannot establish desktop behavior.
