---
title: Tools
description: The tools anchor gives the model, their parameters, and whether they ask for approval.
---

These are the tools the model can call. Use these names in a named sub-agent's `tools` list.

| Tool          | What it does                                             | Asks?                          |
| ------------- | -------------------------------------------------------- | ------------------------------ |
| `read_file`   | Read a text file as numbered lines.                      | Only outside the directory     |
| `list_dir`    | List files as a tree, honoring `.gitignore`.             | Only outside the directory     |
| `glob`        | Find files by pattern, honoring `.gitignore`.            | Only outside the directory     |
| `grep`        | Search file contents with a regular expression.          | Only outside the directory     |
| `write_file`  | Create a file or replace its content.                    | Yes, with a diff               |
| `edit_file`   | Replace exact text in a file.                            | Yes, with a diff               |
| `shell`       | Run a bash command.                                      | Unless it's read-only          |
| `agent`       | Start a sub-agent in the background.                     | No (its own tools may ask)     |
| `agent_status` | Check on the turn's sub-agents.                         | No                             |
| `agent_stop`  | Stop a running sub-agent.                                | No                             |
| `skill`       | Load a skill's instructions.                             | No                             |
| `ask_user`    | Ask you a multiple-choice question.                      | It is the question             |
| `mcp__<server>__<tool>` | A tool from an MCP server.                     | Unless marked read-only        |

Secret files are denied to every tool, and every result is capped at 30,000 characters (the
middle is cut).

## File tools

### read_file

| Parameter | Default | Description                        |
| --------- | ------- | ---------------------------------- |
| `path`    |         | File path, relative to the working directory. |
| `offset`  | `1`     | First line to read (1-based).      |
| `limit`   | `2000`  | Maximum number of lines.           |

### list_dir

| Parameter | Default | Description                  |
| --------- | ------- | ---------------------------- |
| `path`    | `.`     | Directory to list.           |
| `depth`   | `2`     | How many levels deep.        |

### glob

| Parameter | Default | Description                      |
| --------- | ------- | -------------------------------- |
| `pattern` |         | A glob, such as `**/*.cs`.       |
| `path`    | `.`     | Directory to search.             |

### grep

| Parameter     | Default | Description                                  |
| ------------- | ------- | -------------------------------------------- |
| `pattern`     |         | A regular expression (.NET syntax).          |
| `path`        | `.`     | File or directory to search.                 |
| `glob`        |         | Only search files matching this glob.        |
| `ignore_case` | `false` | Match case-insensitively.                    |

Results are `path:line: text`.

### write_file

| Parameter | Description                  |
| --------- | ---------------------------- |
| `path`    | File path.                   |
| `content` | The complete file content.   |

Content that looks elided, such as `// ... rest unchanged`, is rejected, so a file is never
overwritten with a placeholder.

### edit_file

| Parameter     | Default | Description                                                      |
| ------------- | ------- | ---------------------------------------------------------------- |
| `path`        |         | File path.                                                       |
| `old_string`  |         | The exact text to replace, including indentation.               |
| `new_string`  |         | The replacement.                                                 |
| `replace_all` | `false` | Replace every occurrence. Otherwise `old_string` must be unique. |

## shell

| Parameter         | Default | Description                  |
| ----------------- | ------- | ---------------------------- |
| `command`         |         | The command to run.          |
| `timeout_seconds` | `120`   | Up to 600.                   |

Runs in the working directory with bash (or `sh`), or `cmd.exe` on Windows. stdin is closed, and
`TERM=dumb`, `NO_COLOR=1` and `PAGER=cat` are set so commands don't wait for input. Output is stdout
and stderr combined, followed by the exit code, with secrets masked.

## Agent tools

### agent

| Parameter | Description                                                          |
| --------- | -------------------------------------------------------------------- |
| `task`    | The complete task, with all the context the sub-agent needs.        |
| `agent`   | A named sub-agent. Omit for the default read-only one.              |

Returns the sub-agent's id, such as `agent-1` or `reviewer-2`, straight away. The report arrives
later as a message. Up to 4 sub-agents can run at once.

### agent_status

| Parameter | Description                                        |
| --------- | -------------------------------------------------- |
| `id`      | A sub-agent's id. Omit for every one in the turn. |

For each sub-agent: whether it's running, finished or stopped, for how long, how many tool calls
it has made, its latest three, and its task.

### agent_stop

| Parameter | Description        |
| --------- | ------------------ |
| `id`      | The sub-agent's id. |

Stops a running sub-agent. Its report never arrives.

### skill

| Parameter | Description     |
| --------- | --------------- |
| `name`    | The skill name. |

Only offered when at least one skill is installed.

## ask_user

Asks you a multiple-choice question when the model is blocked on a decision only you can make,
such as a preference the request and the code don't settle. In the REPL it shows the same picker
as `anchor setup`: move with ↑/↓ and press Enter. The last entry, "Something else", lets you type
your own answer. Esc or Ctrl+C dismisses the question, and the model continues on its own
judgment and says what it assumed.

| Parameter     | Default | Description                                            |
| ------------- | ------- | ------------------------------------------------------ |
| `question`    |         | The question, as one sentence.                         |
| `options`     |         | 2 to 8 answers to choose from, the recommended one first. |
| `allow_other` | `true`  | Whether you can type an answer that isn't an option.   |

Offered in the REPL and in `--json` sessions, where it becomes a
[`question` event](/anchor/reference/json/#events-it-adds). It's not offered with `-p`, where
no one can answer, or to sub-agents.
