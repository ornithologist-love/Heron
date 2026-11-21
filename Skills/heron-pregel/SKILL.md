---
name: heron-pregel
description: Write, validate, run and resume Heron.Pregel graph scripts (.csx), including typed inputs, stages, parallel branches, human review, retries, resource locks and schedules. Use when a task should become a repeatable, recoverable automation rather than a one-off shell script.
---

# Heron.Pregel

Heron.Pregel runs durable graphs written as C# scripts. Its checkpoints let a paused run continue from the saved position.

## The objects

| Object | Meaning |
| --- | --- |
| Script (`.csx`) | Defines the graph. Loading it runs its top level but no nodes |
| `Input.json` | Default parameters and choices; may also declare triggers |
| Task | A saved configuration with a stable `taskId`, parameters and triggers |
| Run | One execution with its own `runId`, status, steps, logs and state |

A written file is not a saved task, a saved task has not run, and a schedule only fires while `heron-pregel serve` is running. Report which of these you actually reached, with evidence.

## Workflow

1. Read the existing script and input. For an existing task, run `heron-pregel tasks` and keep its `taskId`.
2. Follow [authoring](references/authoring.md) when writing or changing a graph. Run `heron-pregel api` to check signatures for the installed version.
3. Run `heron-pregel validate <file.csx>` for graphs. Scripts that return a template are compiled by `apply` and `run`.
4. Use `apply` to save, or `run` to execute. Commands are listed in [operations](references/operations.md).
5. On failure, read the failed step and its evidence directory first. Before re-running, check which external side effects already happened.

## Boundaries

- Keep the script's top level free of side effects. `describe`, `validate` and `apply` all execute it.
- Put external work in nodes, and call existing command line tools rather than porting them.
- Script versions are content hashes. To run updated code, start a new run. Resume a paused run against its saved graph.
- Only say a thing happened if you saw it happen: validation is not a successful run, and a saved schedule is not a fired trigger.
