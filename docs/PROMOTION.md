# WpfLens release setup

WpfLens is independently maintained in `gaoshang212/WpfLens` and retains
its Git history and MIT attribution from `faze79/WPFVisualTreeMcp`.
WpfLens packages and catalog entries have not been published yet. Historical
upstream releases do not establish publication or ownership for WpfLens.

## Distribution identities

| Channel | Identity |
| --- | --- |
| MCP and CLI NuGet package | `WpfLens.Mcp` |
| C# SDK NuGet package | `WpfLens.Automation` |
| .NET tool command | `wpflens` |
| MCP Registry | `io.github.gaoshang212/wpf-lens` |
| Codex marketplace / plugin / MCP entry | `WpfLens` / `wpflens` / `wpflens` |
| GitHub release assets | `WpfLens-vX.Y.Z-*` |

Implementation namespaces and assemblies remain `WpfVisualTreeMcp.*`.
The SDK's `buildTransitive/WpfLens.Automation.targets` must match its package ID
so NuGet imports the target that copies the injection payload to consumers.

## Configure NuGet publishing once

1. Use a nuget.org account authorized to publish both WpfLens package IDs.
2. Create a [Trusted Publishing policy](https://www.nuget.org/account/trustedpublishing)
   for repository owner `gaoshang212`, repository `WpfLens`, workflow
   `release.yml`, and both packages (including creation of new packages).
   Leave the environment empty while the workflow has no `environment:`.
3. Set this repository's Actions secret `NUGET_USER` to that NuGet profile name.

The release workflow uses `NuGet/login@v1` and GitHub OIDC. Without `NUGET_USER`,
it explicitly skips NuGet publication while still producing a GitHub Release.
A successful GitHub Release therefore does not by itself verify NuGet publication.

## Prepare and verify a release

1. Select the feature commits for the release and merge them after verification.
2. Keep the tag `vX.Y.Z`, both package project versions, MCP manifest version and
   package version, and `plugins/wpflens/.codex-plugin/plugin.json` version aligned.
3. Add the corresponding CHANGELOG entry and run the Build workflow. Retain the
   framework, architecture, standalone SDK consumer and exported xUnit checks.
4. Push the release tag when publication is intended. The Release workflow builds
   and tests the portable ZIP, both NuGet packages and an MCPB bundle. Install
   the Codex plugin from the Git marketplace.
5. Verify both NuGet package versions through a fresh install. Confirm the
   `wpflens` command, `WpfLens` MCP handshake and the injection payload.
6. Verify exported xUnit projects restore `WpfLens.Automation` and run successfully.
7. After the exact NuGet version is public, run **Publish to MCP Registry**.
   It uses GitHub OIDC for `io.github.gaoshang212/*`. The package's embedded
   `.mcp/server.json` and README `mcp-name` marker must agree with that identity.
8. Verify plugin installation and startup from the WpfLens marketplace. Verify
   update checks use the WpfLens package and WpfLens releases.

CLI self-update selects stable versions. Install prerelease packages explicitly;
do not rely on stable auto-update to select a prerelease.

## Upstream synchronization

Keep `origin` pointing at `https://github.com/gaoshang212/WpfLens.git`.
Retain the original fork as `fork` for upstream pull requests, and use `upstream` for
`https://github.com/faze79/WPFVisualTreeMcp.git`. Integrate fetched upstream changes
on a synchronization branch, resolve conflicts, and run CI before merging into
WpfLens `main`. Preserve published history and tags.

WpfLens is a standalone repository. The original
`https://github.com/gaoshang212/WPFVisualTreeMcp.git` fork remains available for
contributing changes upstream. Preserve shared commit history so upstream merges
remain possible; migrate selected feature branches without publishing old release tags.

## Discovery and support

Use WpfLens Issues for public bug reports. Add WpfLens to
catalogs only after its own packages are public and verified. Keep its entries
separate from upstream listings and acknowledge the original project.

Reference: [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
[MCP authentication](https://modelcontextprotocol.io/registry/authentication),
[MCP package ownership](https://modelcontextprotocol.io/registry/package-types).
