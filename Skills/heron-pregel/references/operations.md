# Operating tasks and runs

Data lives in `HERON_PREGEL_HOME`, or `~/.heron-pregel` by default. Use `--home <directory>` for an isolated experiment.

## Commands

```bash
heron-pregel validate flow.csx
heron-pregel describe flow.csx
heron-pregel apply flow.csx --input Input.json --name "Nightly report"
heron-pregel run flow.csx --input Input.json
heron-pregel run --task <taskId>
heron-pregel tasks
heron-pregel status --task <taskId> --run <runId>
heron-pregel stop --task <taskId> --run <runId>
heron-pregel serve
heron-pregel api --output api.md
heron-pregel skill --output ./skills/heron-pregel
```

Exit codes: `0` succeeded, `2` usage error, `3` failed, `4` paused for review, `130` cancelled.

## Parameters

A run resolves parameters in this order: values from this run's `--input`, then saved task parameters, then `Input.json` defaults. An input file may be flat, or use the `with` declaration format.

## Reviewing a paused run

`status` prints the pending `review` with its `id` and `contentHash`. The decision file must quote both, so an approval can never apply to content that changed after it was read:

```json
{ "review_id": "<id>", "content_hash": "<hash>", "action": "approve", "feedback": "" }
```

```bash
heron-pregel review --task <taskId> --run <runId> --review-file decision.json
```

`action` is one of the actions the review offers. Only approve when a person has decided, or when the task explicitly delegates that decision.

## Schedules

Triggers are saved with the task, or declared in `Input.json` under `triggers`. They fire only while `heron-pregel serve` runs on that data directory.

## Diagnosing a failure

1. `heron-pregel status` gives the failed step, summary and `evidence` directories.
2. `run.log` and `events.jsonl` in the evidence directory hold the node output.
3. Fix the cause and start a new run. Resume requires the graph fingerprint saved in the checkpoint.
4. Check external side effects before repeating a run that partly completed.
