# CI build and compatibility checks

Build and Release both call `.github/workflows/compatibility.yml` after producing
their artifacts. The reusable workflow reads `tests/compatibility-matrix.json`
and runs one SDK job per target framework, with at most six SDK jobs in parallel.
Each job retains both architectures, all three SDK runtimes, package payload
checks, exported xUnit replay and the isolated x86 runtime checks.

The desktop job runs injection and the complete inspection/screenshot matrix
alongside the SDK jobs. Desktop operations remain sequential within each runner
to avoid contention over window activation and keyboard focus. Release also
tests the extracted MCPB archive against the desktop job's sample application.

Release builds its deliverables in `prepare`, validates them in `compatibility`,
then publishes those same artifacts in `release`. Failed or cancelled validation
prevents publishing. The Build workflow retains a final `build` check that fails
if compilation or compatibility checks fail, preserving its required-check
semantics. Downloaded artifacts include the server's hidden metadata.

## Dependency caches

Build jobs cache the standard NuGet package directory, keyed by OS, job and
dependency files. SDK tests use a separate third-party fallback cache per target framework.
Each test run still restores WpfLens.Automation from the supplied local feed into
a fresh package directory. Source mapping selects that feed explicitly, and a
fallback cache containing WpfLens packages is rejected. Successful runs add newly
downloaded third-party dependencies to the fallback cache.

The first run is cold; subsequent runs can reuse dependencies. Runner queues,
artifact transfers and repeated setup affect elapsed time. Compare both overall
workflow duration and individual jobs; parallelism may increase total billed
runner minutes even when wall time decreases.

## Local verification

Use Python 3.12 and the runtimes documented by `test_automation_package.py`:

```powershell
python scripts/test_ci_package_shards.py
python scripts/test_automation_package.py --packages artifacts/packages --framework net8.0-windows --dependency-cache artifacts/sdk-dependencies
```

Omit `--framework` for the complete matrix. Omit `--dependency-cache` for a fully
isolated restore. Logs report elapsed time for each build, smoke test and replay;
CI uploads SDK logs even when a shard fails.
