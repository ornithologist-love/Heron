# AutomationTenants server

- Read [server architecture](../../Docs/automation-tenants-server.md) before changing tenant boundaries, the protocol or state.
- Go 1.27. Source and normal Go build caches stay in this module; do not create isolated build projects.
- Local build: `go build -trimpath -o bin/ ./cmd/heron-automation-tenants`; run with `--config <private.json>`.
- Framework checks: `go test ./...`, `go vet ./...`, `gofmt`. Each test has an `Anchor: Docs/...` comment identifying its documented invariant. Use isolated mock tenants and temporary state only.
- Versioned delivery uses the repository entry `pwsh -File ./Pack.ps1 -ServerOnly`; see the root package workflow. Default target is linux-amd64.
- Listen only on loopback. The embedding HTTP host verifies human identity. Do not add device keys, pairing, authentication issuance or approval flows.
- Agent tenant/pool assignment comes from private host configuration. Preserve existing routes, JSON fields, tenant state and UTF-8 byte receipts.
- One writer per state root. The publication ledger is read-only; its publishing host is the sole writer.
- Never log request headers, credentials, full requests or configuration. Private configuration, state, binaries and logs do not enter Git.
