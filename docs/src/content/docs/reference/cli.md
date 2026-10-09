---
title: Command line
description: Every anchor command-line option, environment variable and exit code.
---

```
anchor setup
anchor [--model <name>] [--yolo] [--allow <rule>]... [--resume [id]] [-p [prompt]] [--until <check>]
       [--max-rounds <n>] [--timeout <seconds>] [--json] [--plain] [--theme <name>]
```

With no options, anchor starts an [interactive session](/anchor/guides/repl/) in the current
directory. `anchor setup` runs the [setup wizard](/anchor/start/configuration/#the-setup-wizard) and
exits.

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
| `--plain`              | Use the line-by-line REPL instead of the full-screen one. It's also used when stdin or stdout isn't a terminal. |
| `--theme <name>`       | Use a color theme for this run: `default`, `bright`, `light` or `mono`. Overrides [`theme`](/anchor/reference/config/#theme-and-colors) in the config. |
| `--version`            | Print the version.                                                                                      |
| `-h`, `--help`         | Print usage.                                                                                            |

`--model`, `--resume` and `-p` never take a value that starts with `-`. So in
`anchor --resume -p "hi"`, `--resume` continues the latest session and `"hi"` is the prompt.

## Examples

```sh
anchor setup                                  # choose a provider and model
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
| `ANCHOR_DEBUG`      | Set to `1` to also print a bug's stack trace on the terminal.           |

A model on another OpenAI-compatible server reads its key from the variable named in
`provider.apiKeyEnv`. When a key variable is unset, anchor uses the key `anchor setup` saved for it
in the OS keychain, or in `~/.anchor/credentials` where there's no keychain. MCP server configs can reference any variable as `${NAME}`.

## Crash logs

When anchor hits a bug, it says so in one line and writes the details, including the stack trace,
to a file in `~/.anchor/logs/`, such as `crash-20261009-221503-3fa9c1.log`. Only you can read it,
and the newest 10 are kept. Please attach it if you report the problem. Failures that aren't bugs,
such as a rejected key or a provider that's down, are only reported, not recorded.

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
