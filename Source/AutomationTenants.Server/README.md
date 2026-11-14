# Heron.AutomationTenants.Server

Loopback Go control service for tenant and machine-pool assignment, native automation dispatch, durable execution views and logs, schedules, webhook delivery and automation MCP.

Execution remains in the native Worker. The service does not execute source, interpret Mutex capacity, infer native step states or restart work when a machine disconnects.

See [architecture and state contracts](../../Docs/automation-tenants-server.md), [build and delivery](../../Docs/package-workflow.md) and [module commands](AGENTS.md). Start from [the mock configuration](config.example.json); keep the real configuration outside the repository.
