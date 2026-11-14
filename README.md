# Heron

Reusable .NET 8 building blocks for Avalonia desktop applications and durable automation, with an independent Go tenant control service.

- [AutomationTenants server](Docs/automation-tenants-server.md): Go control service and versioned Linux delivery.

- [ClientControls](Docs/client-controls.md): controls, light and dark themes, and an independent Gallery with an in-memory automation mock.
- [ClientFramework](Docs/client-framework.md): plugin loading, host contracts, desktop infrastructure, page lifecycle and navigation.
- [Pregel](Docs/pregel.md): durable graph execution, C# scripts, automation and a standalone CLI.
- [AutomationTenants](Docs/automation-tenants.md): remote workers, native subgraph dispatch and durable resource coordination.

Build requirements and package delivery: [package workflow](Docs/package-workflow.md). PowerShell 7 and a .NET SDK capable of targeting .NET 8 are required; dependencies restore from nuget.org.

Inspect or build all packages:

```powershell
pwsh -File ./Pack.ps1 -List
pwsh -File ./Pack.ps1
```

Open the Gallery:

```powershell
dotnet run --project Source/Client.Controls.Gallery --configuration Debug
```

Run the framework tests:

```powershell
dotnet test Heron.sln --configuration Release
```

Start with the [hello graph](Examples/Pregel/hello/Hello.csx), [review graph](Examples/Pregel/review/Review.csx), [authoring guide](Docs/pregel-authoring.md) or [Pregel skill](Skills/heron-pregel/SKILL.md).

Heron uses the [MIT license](LICENSE). Adapted controls and icons retain their [third-party notices](Source/Client.Controls/THIRD-PARTY-NOTICES.md).
