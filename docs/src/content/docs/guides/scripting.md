---
title: Scripting and CI
description: Run anchor non-interactively with -p, pipe input into it, read JSON events, and use exit codes in scripts.
---

## One prompt, one answer

`-p` runs a single turn and exits. Only the final answer goes to stdout; progress goes to stderr, so
you can pipe or capture the answer cleanly.

```sh
anchor -p "why does the build fail?"
anchor -p "summarize the changes on this branch" > summary.md
```

Piped input is added to the prompt, or is the prompt when there's no argument:

```sh
git diff | anchor -p "review this change"
cat error.log | anchor -p
```

## Approvals in -p

Nobody can answer a prompt in `-p` mode, so anything that would ask is refused, and the refusal is
reported on stderr. Read-only work, such as reading files or running `git diff`, works as usual.

Commands and MCP tools you've answered "always" to in an interactive session in the same directory
are saved and run without asking, so a `-p` run can use `npm test` once you've allowed `npm`.

To allow specific things for one run, pass `--allow` once for each: a program, an MCP tool
(`mcp__server__tool`), or `edits` for file writes in the directory. A command runs only when every
program in it is allowed. Nothing is saved.

```sh
anchor -p --allow edits --allow npm "fix the failing test" --until "npm test"
```

To allow everything that would ask, pass `--yolo`. Hard denials and secret masking still apply
either way.

## Limits

`--max-rounds <n>` stops the run after `n` model requests, counted across all `--until` rounds. Each
sub-agent gets the same limit of its own; one that reaches it stops and reports what it has.
`--timeout <seconds>` stops it after that long, including time spent running the check but not time
spent starting MCP servers: startup gets a separate limit of the same length, and a server that
hasn't connected by then is skipped. Both end the run with a distinct exit code and `result` status,
so a calling program can tell them apart from a failure.

```sh
anchor -p --yolo "fix the failing test" --until "npm test" --max-rounds 40 --timeout 900
```

Project MCP servers you've never approved interactively are skipped in `-p` mode.

## Exit codes

| Code  | Meaning                                                    |
| ----- | ---------------------------------------------------------- |
| `0`   | The turn completed (and the `--until` check passed, if set). |
| `1`   | An error, such as a provider or network failure.           |
| `2`   | A usage error in the command line.                         |
| `3`   | The loop guard stopped a turn that was going in circles.   |
| `4`   | The `--until` check never passed.                          |
| `5`   | The run reached `--max-rounds`.                            |
| `124` | The run reached `--timeout`.                               |
| `130` | Cancelled with Ctrl+C.                                     |

## JSON output

With `--json`, every event is written to stdout as one JSON object per line, ending with a `result`
line:

```sh
anchor -p "list the TODOs" --json
```

```json
{"type":"tool_start","id":"call_1","name":"grep","summary":"TODO"}
{"type":"tool_end","id":"call_1","name":"grep","ok":true,"result":"src/a.cs:12: // TODO ..."}
{"type":"text","text":"There are 3 TODOs:"}
{"type":"usage","input":5210,"output":84,"cached":2304}
{"type":"turn_end","reason":"completed","detail":null}
{"type":"result","status":"completed","text":"There are 3 TODOs: ...","check":null,"files_changed":[],"session":"20260928-225355-be7b","usage":{"input":5210,"output":84,"cached":2304}}
```

Every event type is in the [JSON reference](/anchor/reference/json/).

## In CI

A GitHub Actions step that asks anchor to fix lint errors until the linter passes:

```yaml
- name: Fix lint
  env:
    ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
  run: |
    curl -fsSL https://raw.githubusercontent.com/fuseraft/anchor/main/install.sh | bash
    ~/.local/bin/anchor -p --yolo "fix the lint errors" --until "npm run lint"
```

:::caution
`--yolo` lets the model run any command that isn't hard-denied. Run it where that's acceptable: a CI
runner, a container, or a throwaway checkout.
:::

## Editors and other tools

`anchor --json` without `-p` is a line protocol for driving anchor from an editor or another
program: send requests on stdin, read events on stdout, and answer approval prompts yourself. See the
[JSON reference](/anchor/reference/json/#the-editor-protocol).
