---
type: architecture
covers: [Source/Core/**]
---

# Heron.Core

`Heron.Core` is a zero-dependency pure-BCL (`net8.0`) library shared across desktop hosts, servers, CLI tools, and plugins without dragging UI, web, logging, or download frameworks into consumers.

## Scope

- `Heron.Core.Process`: subprocess runner with bounded output capture (`DefaultProcessRunner`, `IProcessRunner`), PID/start-time/path verified child process lifecycle (`OwnedProcessRegistry`, `OwnedProcessDomains`), PATH/vendor tool discovery (`InstalledToolLocator`), and Windows in-process process/file-lock inspection (`WindowsProcessInventory`, `WindowsFileLockInventory`).
- `Heron.Core.Config`: thread-safe JSON section cache (`JsonConfigurationStore`) and composable typed module reader (`IConfigurationModule<T>`, `ConfigurationContext`). Product-specific directory discovery (`ConfigServices`) stays in consumers.
- `Heron.Core.Scheduling`: host-agnostic daily fixed-time loop (`DailyTaskRunner`).
- `Heron.Core.Utils`: path segment/filename sanitization (`PathSafety`) and case-insensitive wildcard matching (`FnMatch`).
