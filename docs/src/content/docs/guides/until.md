---
title: Working until a check passes
description: Use /until and --until to keep the agent working until your own check command exits 0.
---

Models stop early and say they're done. `/until` makes a command, not the model, decide when the
work is finished.

```
› /until dotnet test
After each turn anchor runs `dotnet test` and keeps working until it exits 0 (at most 5 rounds, or until a round changes no files).

› fix the failing parser tests
  ...the model works...
  ✗ check failed (round 1 of 5): dotnet test
  ...the model gets the failing output and works again...
  ✓ check passed: dotnet test
```

## How it works

After each turn, anchor runs your check command in the working directory.

- **Exit code 0**: the work is done.
- **Anything else**: the model gets the check's output as its next message and another round. The
  output is masked for secrets and cut to its last 8,000 characters, where test runners put the
  failures and the summary.

The loop stops when:

- the check passes,
- a round changes no files, because the check would fail the same way again (this also covers the
  model stopping to ask you something, so you can just answer), or
- 5 rounds have run.

When it stops without passing, anchor says why:

```
  check still failing and the last round changed no files: dotnet test
```

Ctrl+C cancels the loop like any turn.

## In the REPL

| Command           | Effect                                         |
| ----------------- | ---------------------------------------------- |
| `/until <command>` | Set the check. It applies to every message until cleared. |
| `/until`          | Show the current check.                        |
| `/until off`      | Clear it.                                      |

A message and all its rounds count as one turn, so `/undo` reverts the whole loop.

## In scripts

```sh
anchor -p --yolo "fix the failing tests" --until "dotnet test"
```

`--until` needs `-p`. If the check never passes, anchor exits with code **4**. With `--json`, each
run of the check emits a `check` event and the final `result` line has a `check` field:
`passed`, `no_changes` or `out_of_rounds`.

## About the check command

The check is your own command, like `!cmd`, so it runs without an approval prompt and isn't subject
to the shell policy. It has a 10-minute timeout.

Good checks are fast, deterministic and exit non-zero on failure:

- `dotnet test`, `npm test`, `cargo test`, `go test ./...`, `pytest -q`
- `make lint && make test`
- `./scripts/verify.sh`

:::tip
A round that changes files only through shell commands, such as `sed -i` or a formatter, counts as
"no changes", because anchor only tracks its own file tools. If the loop stops early like this, tell
the model to continue.
:::
