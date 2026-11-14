---
type: architecture
covers: [Source/ClientFramework.Plugins/**, Source/ClientFramework.Abstractions/**, Source/ClientFramework.Runtime/**, Source/ClientFramework.Infrastructure/**, Source/ClientFramework.Shell/**, Source/Client.Host/**]
---

# ClientFramework plugin model

ClientFramework separates reusable plugin mechanisms from the product that hosts them. A host chooses its interface version, shared assemblies, edition and presentation. Plugins consume the host's declared contracts; they do not reference its executable or one another's private implementation.

## Boundaries

`Heron.ClientFramework.Plugins` uses only the BCL. It owns package manifests, dependency resolution, editions, assembly isolation, version storage, enablement choices and assembly of already-built artifacts. It does not invoke compilers or know consumer repositories, native libraries or business features.

`Heron.ClientFramework.Abstractions` contains the narrow desktop contracts for modules, pages, commands, status and optional host capabilities. It depends on the plugin model, Avalonia, MVVM and DI abstractions. Product copy, application-specific window policy and implementations of host capabilities belong to the consumer.

`Heron.ClientFramework.Runtime` creates each plugin's private service provider, manages module lifetime and revocable registrations, and resolves optional services through handles. Hiding a product entry is distinct from stopping its module: the host can preserve running operations and restore presentation using the same instance.

`Heron.ClientFramework.Infrastructure` holds desktop services that have no product meaning: run logging and diagnostics, the live log window, native WebView environment, visibility, scaling and recovery, desktop notifications, folder and background-execution helpers, theme switching and window presence. Product identity and storage are not part of it. Before logging or WebView storage is used, the host calls `ClientHostProfile.Configure` once with its display name, log file prefix, log and cache directories and an optional theme persistence callback; using the services before that throws instead of writing to a guessed location. Product configuration, localisation and domain-specific controls stay in the consumer.

Logging defaults to Information, with Microsoft categories at Warning. `HERON_LOG_LEVEL` and `HERON_LOG_OVERRIDES` configure the process policy; product and plugin-specific defaults belong to the host through `ClientLogging.Levels`.

`Heron.ClientFramework.Shell` holds the business-free parts of a host window that every host needs to show plugins: plugin-scoped presentation and navigation ports (a hidden plugin keeps its background lifetime and stops receiving page changes), plugin page view creation, keep-alive and release, sidebar navigation items and the merge of a saved sidebar order with the currently visible pages. `PluginPageHost` stacks mounted pages in one cell so inactive pages and native web views keep their state while only the active page is visible and hit-testable. It is not a plugin contract: plugins never reference it, so changing it does not change the host interface version.

## Shell window

`ShellWindow` is the whole host window: a self-drawn title bar (sidebar pin, title slot, theme button, action slot; the system caption buttons stay), a sidebar with navigation and the user chip, the page area and a status footer. The host fills slots and keeps everything that has product meaning: title and brand, banner, user menu entries, chip accessories, footer items and overlays such as sign-in or update dialogs. Plugins reach the footer only through `ClientPluginStatus`; menu entries from plugins are not part of the shell.

The sidebar is expanded while it is pinned or the pointer is over it. A hovering drawer floats over ordinary pages, whose edge stays at the pinned width; a page hosted in a native child window cannot be overlapped, so for it the content edge follows the drawer. While pinned, a long press reorders entries; the host moves the item and persists the order.

The theme has three states, system, light and dark, held by `ThemeService`: the system state follows the reported system theme, an explicit choice ignores it, and every choice is persisted through the host's callback. Labels are host text with neutral defaults (`ShellText`), so a host can name its themes without changing the shell.

Slot content keeps the host's data context by plain inheritance from the window. Binding the data context of the presenters that host the slots, even to the same window value, leaves bindings declared inside XAML slot content without a source: they are applied while the elements are populated and never resolve, so visibility bindings fall back to visible.

The user is display only (`IShellUser`: name, account, organization, initials, picture); signing in and sessions belong to the host. The user menu keeps the host's entry order. The shell's theme entry goes first unless the host places one itself; hidden entries keep their place so a binding that shows them does not move them. The menu is a native popup with a self-drawn card, because a flyout presenter leaks colour around rounded corners.

## Heron client launcher

`Source/Client.Host` is the minimal host built on these parts: `ShellWindow`, the plugin host core, the user's account name and the per-user theme and pin choices. It has no server, sign-in, product copy or business services, and declares only the framework contracts as shared assemblies. `--plugins <dir>` names package folders or roots searched for `plugin.json`, `--edition <json>` chooses and orders plugins and adds its package paths; both are assembled into the launcher directory before start, and a plugin the edition names without a package fails the start. `--smoke` starts, reports each plugin and page and exits non-zero on any failure; `--capture <dir>` renders the window headless in light and dark. How to write and plug in a plugin: [client plugins](client-plugins.md).

## Plugin host core

`ClientPluginHostCore` in the runtime is the product-neutral host. It reads the bundled packages, edition and host policy from the product directory, starts the healthy or trial version group when one exists and otherwise the bundled packages, applies enablement choices and starts startup modules in dependency order. An installed group that can no longer be resolved does not block startup: the store resets to the bundled packages and the host reports a recovery message. Two startup rules are product options. A bundled-only platform consumes only the packages shipped with the host, filtered for that platform, and never rewrites the installed group. Following the shipped edition ignores installed plugins outside the edition and raises pinned versions older than the bundled ones. The embedding product supplies the per-plugin service table, user-facing messages and the work that must stop before plugins do.

## Host interface identity

The host interface version is owned by the embedding product and read from its host policy. It is independent of NuGet package versions and assembly versions. It has a major and minor: a plugin declares the minimum host minor it needs, and a host accepts it only when the majors match and its own minor is at least that minimum. A legacy integer is interpreted as that major with minor zero. Malformed versions, different majors and a newer required minor are rejected before activation with the required and available versions.

Additive optional capabilities, additional shared assemblies and interface members with default implementations belong to a minor upgrade. Removing or renaming public members or types, changing signatures, adding required abstract interface members, or changing contract semantics requires a new major. Package-version increments alone do not require recompiling plugins. Assembly identities remain stable; changing a namespace or assembly identity is itself a breaking change.

New edition locks write the canonical major.minor form. Existing integer locks remain readable without migration. Assembly-cache inputs include the normalized host API and the resolver implementation, so a host minor upgrade cannot reuse an edition assembled for an older host. Plugin enablement and installed version state are retained.

Abstractions and ClientControls are validated against checked-in released packages during packing. The reference launcher's host policy supplies the publishing host API; embedding products remain responsible for declaring the same breaking-contract major before adopting an incompatible framework release. API compatibility suppressions are forbidden within the baseline major. An intentional break needs a new host major and individually reviewed suppressions, followed by a new released baseline; changing a major does not silently turn validation off. Binary and source-surface checks cannot detect arbitrary semantic changes, which still require contract review. The delivery workflow is described in [package workflow](package-workflow.md).

## Shared assembly compatibility

Contracts are shared through the host policy. A plugin cannot load a private copy of a shared contract to hide an identity mismatch. Private dependency versions remain isolated in the plugin's assembly load context. Framework assemblies unify to the host runtime, except that a plugin carrying a newer out-of-band build of a framework library than the runtime provides (for example a 9.0 package on an 8.0 runtime) loads its own copy, so it does not wait for a host upgrade. Explicit reviewed identity mappings and compatible patch ranges belong to the host policy; they do not permit arbitrary substitutions.

## Packaging policy and atomic delivery

Native-file selection belongs to the consumer through `IPluginFileFilter`. Copying and cache validation use the same predicate, and its fingerprint participates in assembly reuse. The consumer changes the fingerprint when its rules change. Unfiltered standalone packages retain their complete inputs.

Assembly reads built artifacts, verifies dependencies and the host interface, writes a sibling staging directory, then switches the product or plugin set. The temporary previous directory is part of rollback during the switch and is removed after success. It is not a retained backup. User data and plugin preferences are separate from generated product directories.

The existing mechanism tests and isolated fixture packages live in `Tests/Heron.ClientFramework.Tests` and `Tests/PluginFixtures`. Fixture assembly versions are declared separately from production package identity so compatibility cases retain their intended version differences.
