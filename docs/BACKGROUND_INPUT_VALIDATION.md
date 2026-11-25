# Background input validation record

## 2026-11-23 — Mode switches in schemaVersion 1

- All 293 unit tests passed. An ordinary workflow replayed with a simulated Inspector that lacks mode switching. A workflow with a later `SetInputMode` step checked that capability before replay.
- The .NET 8 x64 desktop smoke test passed with a passive covering window. It replayed ordinary input, recorded and replayed mode switches, and verified the final input mode. The run allowed other desktop activity and checked that the target stayed inactive.
- The CLI and SDK exporters generated xUnit tests for a workflow containing a query followed by two mode switches. Both tests passed against fresh self-hosted WPF processes. The SDK test used the workspace SDK build.

## 2026-11-20 — Switch to NuGet release 0.1.1

- Restored `Gosf.Input.Automation 0.1.1` from nuget.org. The Release build succeeded, 291 unit tests and 42 integration tests passed, and one general sample matrix test was skipped.
- Server publishing, MCP tool packaging and C# SDK packaging all succeeded. Across the three artifacts, all 22 entries checked against the original NuGet package had matching SHA-256 hashes. The entries covered Gosf managed assemblies, x86 and x64 native libraries, and licenses.
- Tested injection with the published server against the .NET Framework 4.8 x86 and .NET 8 x64 samples. The tests verified background input and release of the shared binding across MCP, the CLI and the C# SDK.
- Self-hosted desktop tests passed on all six combinations of net472, net48 and net8.0-windows with x86 and x64. They covered rejection of background OLE drag and drop and recovery from a partial unbind failure. These runs allowed other desktop activity and asserted that the target window was never activated. Logs are in `artifacts/gosf-0.1.1-verification/<framework>-<arch>/desktop.log`.

## Validation with locally patched packages

The 2026-11-17 baseline run used branch `background-input` at baseline `08b5e9c`. Its dependency was `0.1.0-wpflens.6`, an unpublished local patch of Gosf 0.1.0.

## 2026-11-18 — Input protection after a partial unbind failure

The local `0.1.0-wpflens.7` package passed desktop regression tests on .NET 8 x64 and .NET Framework 4.8 x86. The tests used the window-message mouse backend and the DirectInput keyboard backend. Pausing the target UI thread caused keyboard unbinding to time out. After the UI thread resumed, clicks and keystrokes in the same session still failed. Once a retry successfully released the binding, the default mouse backend accepted configuration again. All 297 Release unit tests passed.

## 2026-11-18 — Input mode switching

- In Release, 304 unit tests and 42 integration tests passed. One integration test that requires a separately prepared general sample matrix was skipped.
- The self-hosted desktop samples covered all six combinations of net472, net48 and net8.0-windows with x86 and x64. The checks covered physical typing and default value assignment in background mode, the initial state when recording started in background mode, the order of explicit switches and the final mode after replay.
- In background mode, screenshot requests failed before activating the window both when `screen` was requested explicitly and when a native HWND caused `auto` to select `screen`. For ordinary WPF elements, `auto` still returned `render`. The .NET 8 x64 sample also verified screen capture, physical clicks and keyboard input after switching to foreground. This desktop run used `--allow-desktop-activity` and checked that the target stayed inactive during the background phase.
- On a separate .NET 8 x64 sample, MCP enabled background mode, a CLI action used `--physical` in that mode, the CLI restored foreground mode, and the C# SDK enabled background mode again. The recording retrieved through the CLI matched the JSON exported through MCP. Three consecutive `SetInputMode` steps replayed successfully.

## Build and automated tests

- The full Release rebuild completed with 0 errors and 47 nullable warnings. Fixing those warnings was outside the scope of the change.
- Of the unit tests, 290 passed, 0 failed and 0 were skipped.
- Server publishing and local SDK packaging succeeded. Inspection of the SDK package covered the shared assemblies, native dependencies for .NET Framework and CoreCLR on both architectures, and Gosf license entries.
- The dependency build script succeeded in a dedicated source worktree. The patch passed `git apply --cached --check` against the index at the pinned baseline. The original dependency worktree was untouched.
- `git diff --check` passed.

## Self-hosted tests with a covered window

| Target framework | x86 | x64 |
| --- | --- | --- |
| net472 | Passed | Passed |
| net48 | Passed | Passed |
| net8.0-windows | Passed | Passed |

Each combination used a separate build directory. The test process checked its own bitness. The net472 and net48 targets both ran on the local CLR version `4.0.30319.42000`. The .NET 8 runtime version was `8.0.31`.

The tests exercised clicks, double-clicks, hover across commands, Unicode text replacement, shortcuts, slider drags and scrolling. They also covered OLE rejection and cleanup, explicit and repeated release, rebinding, and cleanup after target resolution failed partway through replay. A text box in a separate process received none of the test input.

The matrix used `python scripts/test_background_input.py --allow-desktop-activity`. All six runs checked that the target stayed inactive. **Because other desktop activity was allowed, these runs do not establish that the foreground HWND and system pointer coordinates stayed unchanged throughout.** Per-configuration logs are in `artifacts/background-input-tests/<framework>-<arch>/desktop.log`.

## Strict desktop checks and recovery after release

The final .NET 8 x64 run used a covering window that did not activate itself. The user left the desktop idle during the run.

- The test process asserted that the foreground HWND and physical pointer coordinates stayed unchanged throughout the background phase. The target activation count was 0.
- A separate Python process sampled the desktop state every 10 ms and observed 0 deviations during background input.
- After release, the existing foreground physical click, text entry and Ctrl+A checks passed.
- The test restored the pointer to its original position at the end of the run.

The log is at `artifacts/background-final-strict-and-recovery.log`. Earlier strict runs disrupted by desktop switching were excluded from the passing results.

## Injection and client compatibility

Injection tests used `SampleWpfApp` without an Inspector reference.

- On .NET Framework 4.8 x86, injection succeeded. The CLI entered text in background mode, MCP queried the target and sent keystrokes, and MCP released the binding.
- The same checks passed on .NET 8 x64, confirming native dependency resolution after CoreCLR injection.
- On both targets, C# SDK background text entry and assertions passed. The binding survived SDK exit. The CLI then released it, and a second release through MCP reported it inactive. This verified that clients share the binding by PID.

The script was `scripts/test_background_frontends.py`. Results are in `artifacts/background-frontends-result.log` and `artifacts/background-frontends-coreclr-result.log`.

The regression run for an existing schemaVersion 1 workflow covered 29 steps. MCP and CLI exports matched, invalid workflows were rejected before input, and the generated xUnit project replayed successfully in a fresh process. The physical gesture option was disabled for this run. The log is at `artifacts/legacy-workflow.log`.

## Unsupported and unverified scenarios

The initial scope excludes background touch, background drags across HWNDs, background OLE drag and drop, HwndHost, separate popups, windows on multiple UI threads, minimized or hidden windows, and ARM64. A background OLE call is cancelled at the native entry point on the bound thread. The triggering input command fails and runs cleanup, with no foreground fallback.

Self-hosted tests covered six configurations. Injection tests covered .NET Framework 4.8 x86 and .NET 8 x64. Testing did not cover every third-party WPF control. Use a property assertion or an application-level check to verify that a control accepted the input. The existing physical drag path still permits cross-window drags. This validation added no cross-window desktop acceptance case.
