---
type: architecture
covers: [Source/AutomationTenants/**, Tests/Heron.AutomationTenants.Tests/**]
---

# AutomationTenants

`Heron.AutomationTenants` layers remote dispatch over `Heron.Pregel.Automation`. It owns the worker protocol, task catalog and calendar projection, remote native subgraphs, resource transport and the local resource listener. It depends on the automation runtime and its transitive framework dependencies; it has no desktop or product-host dependency.

## Host boundary

The embedding host supplies state and script directories, connection settings, process launch and ownership, publication lookup and HTTP authentication. The worker receives this environment rather than discovering product paths or credentials. Calendar ownership and the cached tenant catalog belong to each automation host; they do not create another execution queue. Local task execution, cancellation, review and history remain in the shared automation runtime.

Scripts are selected by the registered task identity and installed root. A remote request never supplies an executable source path. Native subgraphs resolve from that same root through its compiled mutation path, with matching root and child fingerprints and an input codec check. Source installation verifies package paths, hashes and the admitted source snapshot before execution. A changed root is rejected instead of evaluating another script.

## Published sources and snapshots

The worker checks the deployment's requested source and Shared fingerprints before using the deployment install path and verifies the installed files before reporting readiness. The catalog exposes distinct operations for deployment, interactive downloads and local loading so an embedding host can restrict downloads to deployments and still load verified local files.

Consistency of publisher-owned scripts comes from content verification, not from read-only marks. Installation verifies the archive and content hashes before it swaps the suite and `Shared` into place; every admission recomputes the installed content and the snapshot it runs. A changed, extended or partially deleted installation fails verification and is replaced by the verified publication; it never runs and never blocks the next delivery.

Protection is a guardrail that does not travel with copies. Installed directories deny changes (an inherited Windows access rule, elsewhere no directory write permission); files carry neither the read-only attribute nor reduced modes, because both are copied with a file. A template or asset a script copies out of its package is therefore an ordinary writable file. Installations protected by earlier releases lose their file marks when they are next protected.

Admitted snapshots come from a content-addressed store beside the install root. Each distinct file is stored once under `source-store/blobs`; a version directory is assembled from hard links, or verified copies where a volume has none, and records the blobs it uses. On Windows the blob directory's inherited rule denies data writes through any link while still permitting the attribute access a new link needs; version directories carry no access rule, since changing one would re-propagate inheritance onto shared files. A damaged version is rebuilt from the verified installation.

Storage stays bounded. A run start records use of its snapshot. Retention keeps versions used within 30 days and the two most recently used versions of each script; other versions and blobs no retained version references are removed. Full-copy snapshots from earlier releases age out after the same window, and only the newest replaced-installation backups are kept. Storage only grows when an installation delivers new content, so collection runs after each installation, still under its exclusive script-source lock; it never removes content a load or run is admitting. A host may also request a best-effort pass, which is skipped while any load or run holds the lock.

## Delivery and logs

An Agent can receive work from more than one tenant in the same poll. The server keeps each cloud invocation in its issuing tenant, and the Worker prepares deployments against that invocation's catalog on the dispatch server's authority. This keeps the host's default catalog for normal discovery while letting one machine receive publications from separate tenant libraries.

Delivery identities are persisted before subsequent polls can replay them. Reconnection queries and reports the existing local run, retaining the child invocation and target. A worker or network restart does not cancel the detached execution process. A duplicate child invocation returns the original execution.

An embedding host can attach an environment fingerprint at run admission. It is part of the persisted run record and travels in the existing run snapshot, so a retried report uses the original values. Fingerprints contain version or revision identifiers suitable for reporting.

Log acknowledgements count original UTF-8 bytes, including the source line terminator. A CRLF source therefore retains the carriage return in its transmitted raw line. Only complete lines are acknowledged; a partial final line is held until completed. An acknowledgement received after a lost response advances the durable cursor without replaying an already acknowledged prefix. A terminal execution continues uploading until its complete log receipt is persisted.

## Resources and recovery

The resource listener uses the Pregel resource authority and its persistent ledger. It restores existing grants before accepting new acquisitions after restart. Connection loss neither frees a grant nor permits another worker to run the same work. Parent and child scopes inherit the original resource context; a granted child keeps its identity until the execution side confirms release.

The local resource domain can be shared by several automation data instances. The embedding host chooses that domain and the store-lock identity, so an update can coordinate with already running processes without rewriting their persisted data. Moving to a different resource service requires resolving the previous grants first.

## Wire and storage stability

The existing snake_case protocol fields, route paths and persisted task, run, delivery and resource records remain stable when code moves between hosts. Authentication and product-specific identity upgrades are supplied by the embedding host. Changing CLR names requires the host to register its script and persisted identity mappings; it does not justify replacing an existing checkpoint or changing its fingerprint.
