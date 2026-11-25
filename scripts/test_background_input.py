"""Exercise a covered WPF window over real Inspector pipes on an unlocked desktop."""
import argparse
from pathlib import Path
import subprocess
import sys


def build(root, framework, arch, output):
    command = ["dotnet", "publish", "tests/WpfVisualTreeMcp.Input.Smoke", "-c", "Release",
               "-f", framework, "-o", str(output), "-p:PlatformTarget=" + arch,
               "-p:NuGetAudit=false", "-p:ArtifactsPath=" + str(output / "build"), "--nologo", "-v", "quiet"]
    if framework.startswith("net8"):
        command += ["-r", "win-" + arch, "--self-contained", "false"]
    result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / "build.log").write_bytes(result.stdout)
    if result.returncode:
        raise RuntimeError(result.stdout.decode("utf-8", errors="replace"))


def exercise(output, args, arch):
    command = [str(output / "WpfVisualTreeMcp.Input.Smoke.exe"), "--expect-" + arch]
    if not args.foreground:
        command += ["--background-only"]
    if args.allow_desktop_activity:
        command += ["--allow-desktop-activity"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        # Covers replay validation and a recording/replay with mode switches.
        log = process.communicate(timeout=90)[0]
    except subprocess.TimeoutExpired:
        # Only terminate this test process and the cover child it launched.
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
        log = process.communicate(timeout=5)[0]
        log += b"\nDesktop smoke exceeded its 90-second deadline.\n"
    (output / "desktop.log").write_bytes(log)
    print(log.decode("utf-8", errors="replace"), flush=True)
    if process.returncode:
        raise RuntimeError(f"Desktop test exited with {process.returncode}: {output}")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--framework", choices=["net472", "net48", "net8.0-windows"])
    parser.add_argument("--arch", choices=["x86", "x64"])
    parser.add_argument("--allow-desktop-activity", action="store_true",
                        help="Verify target stays inactive, but skip exact foreground/cursor equality.")
    parser.add_argument("--foreground", action="store_true", help="Also exercise physical input after unbind.")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    for framework in ([args.framework] if args.framework else ["net472", "net48", "net8.0-windows"]):
        for arch in ([args.arch] if args.arch else ["x86", "x64"]):
            output = root / "artifacts" / "background-input-tests" / (framework + "-" + arch)
            output.mkdir(parents=True, exist_ok=True)
            print("Testing " + framework + " " + arch, flush=True)
            build(root, framework, arch, output)
            exercise(output, args, arch)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.SubprocessError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
