---
type: guide
covers: [Version.props, Directory.Build.props, Directory.Build.targets, Directory.Packages.props, Pack.ps1, Pack.Server.ps1, Source/ClientFramework.Compatibility/**, nuget.config, LICENSE]
---

# Package workflow

Heron produces framework packages independently of its consumers. The default output is `../Binaries/Heron`, next to the repository. A consumer's local feed is an explicit delivery destination for the produced archives. Producing packages never edits a consumer's project references or central package versions.

The root MIT license applies to Heron source and is included with each package through the shared build properties. Package metadata links to the public repository and records the source commit used by SourceLink. ClientControls also carries its third-party notices, including the original licenses for adapted controls and icons.

ClientControls and Pregel Avalonia ship a generated character inventory in `build/Heron.FontCharacters.txt` for hosts that subset their fonts. Each inventory lists the Unicode scalar values used in that package's XAML attributes and text. The file uses UTF-8 and one unique uppercase `U+XXXX` code point per line, sorted by numeric value. Hosts merge both inventories with their own markup and character requirements.

The repository inherits the existing workspace conventions: source belongs in `Source`, durable documentation in `Docs`, process notes in ignored `.work`, and deliverables in the workspace's `Binaries/Heron`. Repository extraction changes ownership; it does not reset directory naming, layering, development or documentation rules.

Libraries under `Source` opt into packing with `IsPackable=true` and use package IDs beginning with `Heron.`. The Gallery and samples remain unpackable. The independent Pregel CLI builds as a .NET tool package named `Heron.Pregel.Cli`.

## Versions and source paths

`Version.props` owns the common package version. `Directory.Packages.props` owns external dependency versions. The pack entry checks each library's effective package version, so overriding a version in an individual project cannot silently split a release.

Assembly versions default to a stable identity independently of the package version. This does not make renames compatible: assembly names, namespaces and persisted type identities still require their own compatibility decisions when code is migrated.

Abstractions and ClientControls enable .NET package validation against the released baseline in `Source/ClientFramework.Compatibility`. Baseline archives are build inputs committed with this repository and restored from its local NuGet source; no consumer checkout or package server is required. The baseline pins the oldest supported contract surface for the current host major. Additive releases retain it. Reviewed breaking changes require a new host major before compatibility suppressions are accepted; after that major is released, its packages become the next baseline. The publishing policy comes from the independent launcher's host policy. Interface-version semantics belong to [ClientFramework](client-framework.md#host-interface-identity).

Debug retains physical source paths for source debugging and XAML hot reload. Release uses a stable virtual source root through PathMap and embeds portable symbols so post-compilation writers cannot introduce physical PDB paths. Local builds do not require a commit or tag.

## Build and deliver

Inspect the selected libraries without compiling:

```powershell
pwsh -File ./Pack.ps1 -List
```

Build Release libraries in their normal output directories and collect local packages:

```powershell
pwsh -File ./Pack.ps1
```

Use `pwsh -File ./Pack.ps1 -NuGetOnly` when preparing just the NuGet batch. Server artifacts are prepared independently with `-ServerOnly`.

The output directory receives all packable projects, including the CLI tool. An explicit `-Feed` receives the nine consumer libraries listed in `FeedPackageIds`: `Heron.Core`, `Heron.ClientControls`, `Heron.ClientFramework.Plugins`, `Heron.ClientFramework.Abstractions`, `Heron.ClientFramework.Desktop`, `Heron.Pregel`, `Heron.Pregel.Automation`, `Heron.Pregel.Avalonia` and `Heron.AutomationTenants`. Relative paths resolve against this repository regardless of the caller's working directory:

```powershell
pwsh -File ./Pack.ps1 -Feed ../Consumer/Packages/Heron
```

All projects finish packing before any archive is added to the selected output. An output lock serializes cooperating deliveries. Existing archives are reusable only when their bytes match; a different archive at the same package version is rejected. Failed build or conflict staging is retained for diagnosis. Package archives are staged, while compiler output paths remain unchanged.

`pwsh -File ./Pack.ps1 -ValidateOnly` runs the same .NET packing, API compatibility and archive checks without delivering packages or building the server artifact. This mode accepts a dirty working tree so public API edits can be checked before committing; it uses the normal compiler output directories and never replaces a released archive. Normal delivery still requires a clean commit.

For a versioned release, commit the source and common version, create the corresponding local version tag, and pack that revision. The consumer then commits the received package files together with its selected package versions. Tags, consumer commits and remote uploads are separate explicit operations; the pack entry performs none of them.

## Server artifacts

The same entry also produces the [AutomationTenants server](automation-tenants-server.md) archive under `Binaries/Heron`. `-ServerOnly` builds only this component; `-List` lists its versioned destination without compiling. Go compilation uses the module's normal build cache. `HERON_GO` can select the Go toolchain. The default runtime is linux-amd64; `-ServerRuntime windows-amd64` supplies a local Windows artifact for integration checks.

The archive name fixes product, common version and runtime. Its manifest fixes the source commit and executable/license hashes; the archive has a SHA-256 sidecar. Archive entry paths, permissions, owner and timestamp are deterministic. A version cannot be replaced with different bytes. The framework delivery contains no service unit or private configuration: those belong to the embedding product. Consumers choose and verify an already built artifact instead of rebuilding a product-owned fork of the server.
