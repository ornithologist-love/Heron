---
type: architecture
covers: [Source/Pregel.Avalonia/**, Source/Client.Host/HostModel.cs, Tests/Heron.Pregel.Avalonia.Tests/**]
---

# Pregel Avalonia workspace

`Heron.Pregel.Avalonia` owns the reusable native automation workspace: task and template selection, schema-driven parameters, schedules, graph navigation, run history, logs, artifacts, review and reexecution. It projects `AutomationHost` rather than introducing another execution engine or store.

The package depends on ClientControls, ClientFramework desktop infrastructure and AutomationTenants presentation models. It has no product host, sign-in provider, publisher address or business settings dependency. Graph and script contracts remain in [Pregel](pregel.md); remote execution contracts remain in [AutomationTenants](automation-tenants.md).

## Host contributions

The embedding host creates one `AutomationPageViewModel` around its existing `AutomationHost`. Optional `AutomationPageExtensions` factories supply global settings and workflow upload panels. The framework owns the modal surface; the host owns each panel's contents, initialization, validation and external effects. A settings panel reports whether saved values changed so the existing editor can refresh its displayed defaults. Without a factory the corresponding entry is hidden.

Shared parameter values, script roots and optional script publishers continue to use `AutomationRuntime`. Exported UI logs use `ClientHostProfile.LogsDirectory`. Moving the UI does not change task IDs, run IDs, serialization, checkpoints, source registration or persisted file layout. The host retains its existing data directory and services.

## 执行与页面生命周期

The host owns execution and shutdown. The workspace only subscribes to events, coalesces visible UI updates and owns presentation state. Bind its view with `NativePageActivity`: selecting another page, hiding the window or minimizing it stops UI refresh and cancels field discovery and revision inspection. Disposing the workspace releases those subscriptions and readers; it never disposes the automation host or cancels its runs. Returning to the page projects current host state again.

This separation preserves running work and scheduled triggers independently of navigation. Downloads and remote connections keep their original service owners; the workspace does not acquire or release them.

## Standalone host and design previews

The [ClientFramework launcher](client-framework.md) includes the real workspace and owns a local automation runtime. It restores explicitly registered scripts and supplies that runtime to plugins through `IAutomationRegistry`. `--data` selects an independent local data directory; `--script` explicitly opens and loads a CSX entry without starting a task. There is no bundled publisher or product settings/upload panel. A fresh launcher without an assembled plugin edition starts this local workspace on its own.

The controls Gallery remains an in-memory mock. Product previews keep their product fixtures in the product repository; the workspace's design projection helpers consume isolated data and never start the automation runtime themselves.

Debug builds preserve source paths and allow XAML hot reload. Release packages carry no HotAvalonia dependency; normal package use does not require a source checkout.
