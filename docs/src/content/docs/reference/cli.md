---
title: Command line
description: Every anchor command-line option, environment variable and exit code.
---

```
anchor [--model <name>] [--yolo] [--allow <rule>]... [--resume [id]] [-p [prompt]] [--until <check>]
       [--max-rounds <n>] [--timeout <seconds>] [--json]
```

With no options, anchor starts an [interactive session](/anchor/guides/repl/) in the current
directory.

## Options

| Option                 | Description                                                                                             |
| ---------------------- | ------------------------------------------------------------------------------------------------------- |
| `-m`, `--model <name>` | The model to use. Overrides `provider.model` in the config.                                             |
| `--yolo`               | Don't ask before writes, commands and reads outside the directory. Hard denials still apply. [More](/anchor/guides/safety/#yolo) |
| `--allow <rule>`       | Don't ask for this, for this run only. Repeatable. `<rule>` is a program (`dotnet`), an MCP tool (`mcp__server__tool`), or `edits` for file writes in the directory. Hard denials still apply. [More](/anchor/guides/scripting/#approvals-in--p) |
| `-r`, `--resume [id]`  | Continue the latest session in this directory, or the session with this id or id prefix.               |
| `-p`, `--print [prompt]` | Run one prompt and print the answer. Piped stdin is added to the prompt. Without `--yolo`, anything that would ask is refused. |
| `--until <check>`      | With `-p`: after each turn run `<check>`, and keep working until it exits 0. [More](/anchor/guides/until/) |
| `--max-rounds <n>`     | With `-p`: stop after `n` model requests (exit code 5). |
| `--timeout <seconds>`  | With `-p`: stop after this many seconds (exit code 124). |
| `--json`               | Write events as JSON lines. Without `-p`, also read requests from stdin. [More](/anchor/reference/json/) |
| `--version`            | Print the version.                                                                                      |
| `-h`, `--help`         | Print usage.                                                                                            |

`--model`, `--resume` and `-p` never take a value that starts with `-`. So in
`anchor --resume -p "hi"`, `--resume` continues the latest session and `"hi"` is the prompt.

## Examples

```sh
anchor                                        # interactive
anchor -m claude-opus-5-5                     # interactive, with another model
anchor --resume                               # continue the latest session here
anchor -p "why does the build fail?"          # one answer, then exit
git diff | anchor -p "review this change"     # piped input
anchor -p --yolo "fix it" --until "make test" # work until the tests pass
anchor -p --allow edits --allow make "fix it" --until "make test" --max-rounds 40 --timeout 900
anchor -p "list the TODOs" --json             # JSON events
anchor --json                                 # editor protocol
```

## Environment variables

| Variable            | Description                                                             |
| ------------------- | ----------------------------------------------------------------------- |
| `ANTHROPIC_API_KEY` | Key for Claude models. When set, the default model is `claude-sonnet-5`. |
| `XAI_API_KEY`       | Key for Grok models. When set (and no Anthropic key), the default model is `grok-4.5`. |
| `OPENAI_API_KEY`    | Key for OpenAI models.                                                  |
| `ANCHOR_HOME`       | Where config and sessions live, instead of `~/.anchor`.                 |
| `NO_COLOR`          | Turn off colored output.                                                |

A model on another OpenAI-compatible server reads its key from the variable named in
`provider.apiKeyEnv`. MCP server configs can reference any variable as `${NAME}`.

## Exit codes

| Code  | Meaning                                                      |
| ----- | ------------------------------------------------------------ |
| `0`   | Completed (and the `--until` check passed, if set).          |
| `1`   | Error.                                                       |
| `2`   | Usage error.                                                 |
| `3`   | Stopped by the loop guard.                                   |
| `4`   | The `--until` check never passed.                            |
| `5`   | Reached the `--max-rounds` limit.                            |
| `124` | Reached the `--timeout` limit.                               |
| `130` | Cancelled.                                                   |
