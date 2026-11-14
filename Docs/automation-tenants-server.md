---
type: architecture
covers: [Source/AutomationTenants.Server/**, Pack.Server.ps1]
---

# AutomationTenants server

The Go service owns tenant and machine-pool assignment, dispatch receipts, durable run views, schedules, webhook delivery and the automation MCP entrance. It communicates with the native [Worker](automation-tenants.md) over the existing JSON protocol. Execution, review continuation, subgraph definitions and Mutex admission remain in Pregel and its Worker. Losing a connection does not restart a run or free its resources.

## Tenant boundary

The service listens only on loopback. An embedding HTTP host verifies human identity before forwarding an actor; the service applies the configured tenant and task scope. Machine names keep their existing registration contract. The service introduces no device authentication or credential issuance.

A machine's configured assignment determines its tenant and pool. Agent requests cannot select another tenant, read another tenant's runs or append to its logs. Native snapshots remain opaque execution evidence rather than another server-side step state machine. A remote subgraph request retains its registered root and encoded input; the Worker validates executable identity.

## Durability

Each tenant owns `tenants/<tenant>/state.json` and `tenants/<tenant>/runs/<run>/events.jsonl` below the configured data root. State schema version remains 1. Existing run, deployment, schedule, outbox and idempotency fields retain their names and interpretation. Code ownership and executable naming do not require a data migration.

The data root has one process-level writer lock, including across executable updates. State is atomically replaced. Log bytes are durably appended before acknowledgement. Receipts count the original UTF-8 raw line plus LF; CR from a CRLF source stays in the raw line. Repeated batches acknowledge the committed prefix without adding another copy, and restart reconstructs offsets from the journal. An incomplete final journal record is discarded during recovery; an invalid complete record is an error. `logs_complete` confirms transport, independently of execution success.

## Webhook identity

An event enters the durable outbox in the same state transaction as its run transition. Delivery is at least once and ordered per hook; the receiver deduplicates by event ID. The signature is HMAC-SHA256 over the exact UTF-8 bytes of `<Unix-seconds>.<JSON-body>`. The body is unchanged by header branding. Timestamp represents the delivery attempt, while the event ID and event time remain stable across retries; receivers enforce their own replay window.

Hosts configure the webhook header prefix, MCP challenge realm and legacy publication owner. The protocol configuration also owns the forwarded actor/access header names and MCP server display name. The framework has no built-in identities: every field is required, and a configuration without them stops at startup rather than silently changing the headers, realm or owner that an existing receiver or client relies on. The MCP server's existing protocol implementation version remains independent of the artifact release version.

## Publication ledger

`approved_workflows_path` is an external read-only contract. The publishing/review host is the only writer. The service never writes approval, submission or publication state, even when it creates execution-control approvals of its own.

The reader accepts this schema, with a map key opaque to the service:

```json
{"schema":1,"published":{"opaque-publication":{"tenant_id":"orchard","task_id":"mock-task","title":"Mock task","definition_id":"mock-graph"}}}
```

Only `published` can register a workflow, and only in its matching tenant with a nonempty task identity and title. Pending submissions and unrelated fields cannot register work. The definition identity belongs to the publisher; the service does not use it to evaluate code. The publisher atomically replaces the ledger. If an already loaded ledger temporarily cannot be read, the service retains its last admitted catalog rather than exposing unreviewed state; an invalid ledger prevents initial startup.

## Delivery boundary

The repository packs a versioned, static Linux executable with the common Heron version and exact source commit. The archive contains the executable, MIT license and a manifest with file lengths and SHA-256 hashes. It contains no configuration, private paths, credentials or tenant data. Build and delivery use [the package workflow](package-workflow.md).

The consumer pins the artifact version, validates the manifest and hashes, and owns service units, local executable naming, human identity, proxies and private configuration. Building an artifact does not activate a service. Existing unit names, directories, protocol defaults and persistent state can remain unchanged when a consumer switches binaries.
