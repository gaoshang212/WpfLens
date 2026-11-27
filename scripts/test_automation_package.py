"""Install the actual Automation nupkg in an isolated C# consumer and exercise it live.

Run on Windows with the .NET 10 SDK, .NET 6/8/9/10 Desktop runtimes (x64 and x86)
and .NET Framework 4.8.1. The matrix also verifies older application targets.
--gestures additionally requires an unlocked interactive desktop.
"""
import argparse
import itertools
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape, quoteattr
import zipfile


ROOT = Path(__file__).resolve().parent.parent
MATRIX = json.loads((ROOT / "tests" / "compatibility-matrix.json").read_text(encoding="utf-8"))


def run(command, name, work, env):
    started = time.monotonic()
    print(f"Running {name}...", flush=True)
    log = work / f"{name}.log"
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run([str(arg) for arg in command], cwd=work, env=env,
                                stdout=output, stderr=subprocess.STDOUT, timeout=300)
    print(f"Finished {name} in {time.monotonic() - started:.1f}s", flush=True)
    if result.returncode:
        raise RuntimeError(f"{name} failed; {log}\n{log.read_text(encoding='utf-8', errors='replace')[-12000:]}")
    if name.startswith("smoke-"):
        print(log.read_text(encoding="utf-8", errors="replace"), flush=True)
    elif name.startswith("replay-"):
        print("\n".join(log.read_text(encoding="utf-8", errors="replace").splitlines()[-15:]), flush=True)


def isolated_x86_runtime(major, work, env):
    """Expose only one installed runtime major to the unmodified NuGet helper.

    Junctions avoid copying hundreds of MB and require no symlink privilege.
    These fixtures are confined to this run's artifact directory.
    """
    program_files = env.get("PROGRAMFILES(X86)") or env.get("ProgramFiles(x86)")
    installed = Path(env["DOTNET_ROOT_X86"]) if env.get("DOTNET_ROOT_X86") else Path(program_files) / "dotnet"
    root = work / f"dotnet-x86-only-{major}"
    root.mkdir()
    shutil.copy2(installed / "dotnet.exe", root / "dotnet.exe")
    for relative in ("host/fxr", "shared/Microsoft.NETCore.App", "shared/Microsoft.WindowsDesktop.App"):
        selected = select_runtime(installed, relative, major)
        link_runtime(root, relative, selected, major, work, env)
    return root


def link_runtime(root, relative, selected, major, work, env):
    link = root / relative / selected.name
    link.parent.mkdir(parents=True, exist_ok=True)
    link_env = dict(env, WPF_TEST_LINK=str(link), WPF_TEST_LINK_TARGET=str(selected))
    run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
         "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction "
         "-Path $env:WPF_TEST_LINK -Target $env:WPF_TEST_LINK_TARGET | Out-Null"],
        f"runtime-link-{major}-{selected.parent.name}", work, link_env)


def select_runtime(installed, relative, major):
    # The host resolver is shared across runtime versions; match normal
    # apphost behavior by using the latest installed hostfxr. Only the
    # shared frameworks are restricted to the selected runtime major.
    candidates = [p for p in (installed / relative).iterdir()
                  if p.is_dir() and "-" not in p.name
                  and (relative == "host/fxr" or p.name.startswith(f"{major}."))]
    if not candidates:
        raise RuntimeError(f"Install the x86 .NET {major} Desktop runtime in {installed}")
    selected = max(candidates, key=lambda p: tuple(map(int, p.name.split('.'))))
    return selected


def parse_arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--gestures", action="store_true")
    parser.add_argument("--framework", choices=MATRIX["targetFrameworks"])
    parser.add_argument("--dependency-cache", type=Path)
    return parser.parse_args()


def read_package(packages):
    candidates = list(packages.glob("WpfLens.Automation.*.nupkg"))
    if len(candidates) != 1:
        raise ValueError("Use a package directory containing exactly one Automation .nupkg")
    with zipfile.ZipFile(candidates[0]) as package:
        return verify_package_payload(package)


def verify_package_payload(package):
    names = package.namelist()
    for assembly in ("Automation", "Shared", "Injector"):
        assert f"lib/net8.0/WpfVisualTreeMcp.{assembly}.dll" in names, assembly
    assert "buildTransitive/WpfLens.Automation.targets" in names
    for architecture in ("x64", "x86"):
        for runtime_directory in ("", "coreclr/"):
            assert f"payload/native/{architecture}/{runtime_directory}WpfVisualTreeMcp.Inspector.dll" in names
        assert f"payload/native/{architecture}/WpfInspectorBootstrapper.dll" in names
    assert "payload/native/x86/WpfInjectorHelper.exe" in names
    assert not any("Server.dll" in name or "ModelContextProtocol" in name for name in names)
    manifest = ET.fromstring(package.read("WpfLens.Automation.nuspec"))
    version = manifest.find(".//{*}version").text
    dependencies = {item.attrib["id"] for item in manifest.findall(".//{*}dependency")}
    assert dependencies == {"Microsoft.Extensions.Logging.Abstractions"}, dependencies

    return names, version


def prepare_workspace(packages, dependency_cache):
    work = ROOT / "TestResults" / f"sdk-package-{uuid.uuid4().hex}"
    work.mkdir(parents=True)
    print(f"Artifacts: {work}", flush=True)
    env = os.environ.copy()
    # Do not let a developer's global setting silently run a net9 app on net10.
    env.pop("DOTNET_ROLL_FORWARD", None)
    env["NUGET_PACKAGES"] = str(work / "nuget-cache")
    write_nuget_config(work, packages, dependency_cache)
    return work, env


def write_nuget_config(work, packages, dependency_cache):
    fallback = "<fallbackPackageFolders><clear /></fallbackPackageFolders>"
    if dependency_cache:
        dependency_cache = dependency_cache.resolve()
        dependency_cache.mkdir(parents=True, exist_ok=True)
        if any(p.name.lower().startswith("wpflens.") for p in dependency_cache.iterdir()):
            raise ValueError("Dependency cache must not contain WpfLens packages")
        fallback = f'<fallbackPackageFolders><clear /><add key="dependencies" value={quoteattr(str(dependency_cache))} /></fallbackPackageFolders>'
    (work / "NuGet.Config").write_text(f"""<configuration><packageSources><clear />
      <add key="local-sdk" value="{escape(str(packages), {'"': '&quot;'})}" />
      <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
      </packageSources>
      <packageSourceMapping><clear />
        <packageSource key="local-sdk"><package pattern="WpfLens.Automation" /></packageSource>
        <packageSource key="nuget.org"><package pattern="*" /></packageSource>
      </packageSourceMapping>{fallback}</configuration>""", encoding="utf-8")


def build_consumer(version, names, work, env):
    consumer = work / "consumer"
    consumer.mkdir()
    project = consumer / "PackageConsumer.csproj"
    project.write_text(f"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
      <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
      <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
      </PropertyGroup><ItemGroup>
      <PackageReference Include="WpfLens.Automation" Version="{version}" />
      </ItemGroup></Project>""", encoding="utf-8")
    shutil.copy2(ROOT / "scripts" / "AutomationPackageSmoke.cs", consumer / "Program.cs")
    run(["dotnet", "build", project, "-c", "Release", "--verbosity", "quiet"], "consumer-build", work, env)
    output = work / "published-consumer"
    run(["dotnet", "publish", project, "-c", "Release", "--no-build", "-o", output, "--verbosity", "quiet"], "consumer-publish", work, env)
    verify_published_payload(consumer, output, names)
    return project, output


def verify_published_payload(consumer, output, names):
    for folder in (consumer / "bin" / "Release" / "net8.0-windows", output):
        for name in names:
            if name.startswith("payload/native/"):
                assert (folder / name.removeprefix("payload/")).is_file(), (folder, name)


def build_sample(framework, architecture, work, env):
    case = f"{framework}-{architecture}"
    source = work / f"source-{case}"
    source.mkdir()
    for file in (ROOT / "samples" / "SampleWpfApp").iterdir():
        if file.suffix in (".cs", ".xaml", ".csproj"):
            shutil.copy2(file, source / file.name)
    sample = work / f"sample-{case}"
    command = ["dotnet", "publish", source / "SampleWpfApp.csproj", "-c", "Release",
               f"-p:SampleTargetFramework={framework}", f"-p:PlatformTarget={architecture}",
               "-p:EnableInspectorSelfHosting=false", "-o", sample, "--verbosity", "quiet"]
    if framework.endswith("-windows"):
        command += ["--runtime", f"win-{architecture}", "--self-contained", "false"]
    run(command, f"sample-build-{case}", work, env)
    return sample / "SampleWpfApp.exe"


def test_sample(framework, architecture, output, gestures, work, env):
    case = f"{framework}-{architecture}"
    executable = build_sample(framework, architecture, work, env)
    exported = work / f"exported-{case}"
    command = [output / "PackageConsumer.exe", executable, exported]
    if gestures:
        command += ["--gestures"]
    run(command, f"smoke-{case}", work, env)
    replay_env = dict(env, WPF_TEST_APP=str(executable))
    run(["dotnet", "test", exported / "RecordedWorkflow.Tests.csproj", "-c", "Release", "--verbosity", "normal"],
        f"replay-{case}", work, replay_env)


def build_advanced_consumer(project, work, env):
    advanced = work / "advanced-consumer"
    advanced.mkdir()
    advanced_project = advanced / "AdvancedConsumer.csproj"
    advanced_project.write_text(project.read_text(encoding="utf-8").replace(
        "<TargetFramework>net8.0-windows</TargetFramework>",
        f"<TargetFrameworks>{';'.join(MATRIX['sdkFrameworks'])}</TargetFrameworks>"), encoding="utf-8")
    shutil.copy2(ROOT / "tests" / "WpfVisualTreeMcp.Automation.Smoke" / "Program.cs", advanced / "Program.cs")
    run(["dotnet", "build", advanced_project, "-c", "Release", "--verbosity", "quiet"],
        "advanced-consumer-build", work, env)
    return advanced


def build_advanced_app(framework, architecture, work, env):
    case = f"{framework}-{architecture}"
    source = work / f"advanced-source-{case}"
    source.mkdir()
    for file in (ROOT / "tests" / "WpfVisualTreeMcp.Automation.TestApp").iterdir():
        if file.suffix in (".cs", ".csproj"):
            shutil.copy2(file, source / file.name)
    target = work / f"advanced-app-{case}"
    command = ["dotnet", "publish", source / "WpfVisualTreeMcp.Automation.TestApp.csproj", "-c", "Release", "-f", framework,
               f"-p:TargetFrameworks={framework}", f"-p:PlatformTarget={architecture}",
               "-p:EnableInspectorSelfHosting=false", "-o", target, "--verbosity", "quiet"]
    if framework.endswith("-windows"):
        command += ["--runtime", f"win-{architecture}", "--self-contained", "false"]
    run(command, f"advanced-app-build-{case}", work, env)
    return target


def advanced_environment(env, framework, architecture, sdk_framework, isolated_runtimes):
    case_env = dict(env, WPF_TEST_SDK_MAJOR=sdk_framework[3:].split(".")[0],
                    WPF_TEST_TARGET_MAJOR=framework[3:].split(".")[0] if framework.endswith("-windows") else "4")
    # Reproduce VSTest's x64 root in an x64 consumer launching x86 apps.
    case_env["DOTNET_ROOT"] = str(Path(shutil.which("dotnet")).resolve().parent)
    sdk_major = case_env["WPF_TEST_SDK_MAJOR"]
    if architecture == "x86" and sdk_major in isolated_runtimes and (
            framework == "net481" or framework == sdk_framework):
        case_env["DOTNET_ROOT_X86"] = str(isolated_runtimes[sdk_major])
        case_env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        print(f"Verifying x86 injection with only .NET {sdk_major} available", flush=True)
    return case_env


def test_advanced(framework, architecture, advanced, isolated_runtimes, work, env):
    case = f"{framework}-{architecture}"
    target = build_advanced_app(framework, architecture, work, env)
    for sdk_framework in MATRIX["sdkFrameworks"]:
        case_env = advanced_environment(env, framework, architecture, sdk_framework, isolated_runtimes)
        run([advanced / "bin" / "Release" / sdk_framework / "AdvancedConsumer.exe",
             target / "WpfVisualTreeMcp.Automation.TestApp.exe", "--inject"],
            f"smoke-advanced-{sdk_framework}-to-{case}", work, case_env)


def cache_dependencies(work, dependency_cache):
    if not dependency_cache:
        return
    for package in (work / "nuget-cache").iterdir():
        if not package.is_dir() or package.name.lower().startswith("wpflens."):
            continue
        for version in package.iterdir():
            destination = dependency_cache / package.name / version.name
            if version.is_dir() and not destination.exists():
                shutil.copytree(version, destination)


def selected_cases(framework):
    frameworks = [framework] if framework else MATRIX["targetFrameworks"]
    return list(itertools.product(frameworks, MATRIX["architectures"]))


def main():
    args = parse_arguments()
    packages = args.packages.resolve()
    names, version = read_package(packages)
    cases = selected_cases(args.framework)
    work, env = prepare_workspace(packages, args.dependency_cache)
    isolated_runtimes = {str(major): isolated_x86_runtime(major, work, env) for major in (9, 10)}
    project, output = build_consumer(version, names, work, env)
    for framework, architecture in cases:
        test_sample(framework, architecture, output, args.gestures, work, env)
    advanced = build_advanced_consumer(project, work, env)
    for framework, architecture in cases:
        test_advanced(framework, architecture, advanced, isolated_runtimes, work, env)
    cache_dependencies(work, args.dependency_cache)
    print(f"PASS: {len(cases)} target/architecture cases, "
          f"{len(MATRIX['sdkFrameworks'])} SDK runtimes, exact waits, inspection, actions, export/replay. "
          f"Artifacts: {work}", flush=True)


if __name__ == "__main__":
    main()
