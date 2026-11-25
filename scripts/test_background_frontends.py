"""Verify real injection and background binding shared by MCP, CLI and C# SDK."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import sys
from test_injection import McpClient, value


SDK_PROGRAM = '''using WpfVisualTreeMcp.Automation;
await using var app = await WpfApplication.AttachAsync(int.Parse(args[0]), autoInject: false);
var text = app.ByAutomationId("user.name");
await app.SetInputModeAsync("background");
var result = await text.SetTextAsync("sdk-background", physical: true);
if (!result.Method.StartsWith("Background.")) throw new Exception(result.Method);
await text.AssertTextAsync("sdk-background");
Console.WriteLine("SDK background text verified; shared binding remains for CLI cleanup.");
'''


def cli(server, pid, *args):
    result = subprocess.run([str(server), *args, "--pid", str(pid)],
                            capture_output=True, text=True, encoding="utf-8", timeout=40)
    assert result.returncode == 0, result.stdout + result.stderr
    return json.loads(result.stdout)


def build_sdk_probe(root):
    directory = root / "artifacts" / "background-sdk-probe"
    directory.mkdir(parents=True, exist_ok=True)
    references = [root / ("src/" + name + "/" + name + ".csproj") for name in
                  ("WpfVisualTreeMcp.Automation", "WpfVisualTreeMcp.Shared", "WpfVisualTreeMcp.Injector")]
    project = directory / "Probe.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                       '<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
                       '</PropertyGroup><ItemGroup>' + ''.join('<ProjectReference Include="' + str(p) + '" />' for p in references) +
                       '</ItemGroup></Project>', encoding="utf-8")
    (directory / "Program.cs").write_text(SDK_PROGRAM, encoding="utf-8")
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-v", "quiet",
                    "-p:BuildProjectReferences=false", "-p:NuGetAudit=false"], check=True)
    return directory / "bin/Release/net8.0/Probe.exe"


def exercise(server, sample, probe, log):
    client = McpClient(server, log)
    try:
        attached = value(client.tool("wpf_attach", process_id=sample.pid, auto_inject=True))
        assert attached["inspector_ready"], attached
        assert value(client.tool("wpf_set_input_mode", mode="background"))["mode"] == "background"
        result = cli(server, sample.pid, "set-text", "--automation-id", "user.name", "--text", "cli-background", "--physical")
        assert result["method"].startswith("Background."), result
        value(client.tool("wpf_assert", automation_id="user.name", property_name="Text", expected_value="cli-background"))
        result = value(client.tool("wpf_send_keys", automation_id="user.name", keys="Ctrl+A"))
        assert result["method"].startswith("Background."), result
        assert cli(server, sample.pid, "set-input-mode", "--mode", "foreground")["mode"] == "foreground"
        value(client.tool("wpf_set_input_mode", mode="background"))
        value(client.tool("wpf_set_text", automation_id="user.name", text="mcp-background", physical=True))
        subprocess.run([str(probe), str(sample.pid)], check=True, timeout=40)
        assert cli(server, sample.pid, "set-input-mode", "--mode", "foreground")["mode"] == "foreground"
        cli(server, sample.pid, "record", "start")
        for mode in ("background", "foreground", "background"):
            value(client.tool("wpf_set_input_mode", mode=mode))
        stopped = cli(server, sample.pid, "record", "stop")["workflow"]
        exported = value(client.tool("wpf_export_test", format="json"))
        workflow = json.loads(exported["files"]["workflow.json"])
        assert workflow == stopped and workflow["schemaVersion"] == 1, workflow
        assert [(s["action"], s["arguments"]) for s in workflow["steps"]] == [
            ("SetInputMode", {"mode": mode}) for mode in ("background", "foreground", "background")]
        result = value(client.tool("wpf_replay", workflow=workflow))
        assert result["success"] and result["completedSteps"] == 3, result
    finally:
        try:
            cli(server, sample.pid, "set-input-mode", "--mode", "foreground")
        finally:
            client.close()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--sample", type=Path, required=True)
    parser.add_argument("--probe", type=Path, help="Reuse an already built SDK probe executable.")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    assert not (args.sample.parent / "WpfVisualTreeMcp.Inspector.dll").exists(), "Use a sample built without self-hosting."
    probe = args.probe.resolve() if args.probe else build_sdk_probe(root)
    sample = subprocess.Popen([str(args.sample.resolve())])
    try:
        time.sleep(2)
        with (root / "artifacts/background-frontends.log").open("w", encoding="utf-8") as log:
            exercise(args.server.resolve(), sample, probe, log)
        print("PASS: real injection, background MCP/CLI/SDK actions, shared release across clients and SDK exit.")
    finally:
        if sample.poll() is None:
            sample.terminate()
        sample.wait(timeout=10)


if __name__ == "__main__":
    main()
