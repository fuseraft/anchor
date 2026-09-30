---
title: JSON events and protocol
description: The JSON Lines events anchor writes with --json, and the request protocol for driving it from an editor.
---

With `--json`, anchor writes one JSON object per line to stdout. Every object has a `type`.

## Events

| `type`           | Fields                                                         | When                                           |
| ---------------- | -------------------------------------------------------------- | ---------------------------------------------- |
| `text`           | `text`                                                         | A piece of the model's reply, as it streams.  |
| `tool_start`     | `id`, `name`, `summary`                                        | A tool call starts.                            |
| `tool_end`       | `id`, `name`, `ok`, `result`                                   | A tool call finishes.                          |
| `file_changed`   | `path`, `added`, `removed`                                     | A file was written.                            |
| `loop_warning`   | `message`                                                      | The loop guard warned or stopped the turn.    |
| `context_reduced` | `kind`, `before`, `after`, `count`                            | Context was reduced. `kind` is `compacted`, `trimmed` or `rounds_dropped`. |
| `notice`         | `message`                                                      | Something worth telling the user.              |
| `check`          | `command`, `round`, `passed`, `output`                         | An `--until` check ran.                        |
| `sub_agent`      | `agent`, `event`                                               | An event from a sub-agent. `event` is any event on this list. |
| `usage`          | `input`, `output`, `cached`                                    | Token usage for a turn.                        |
| `turn_end`       | `reason`, `detail`                                             | A turn ended. `reason` is `completed`, `cancelled`, `loop_stopped`, `round_limit` or `error`. |

Parallel sub-agents are named `agent 1`, `agent 2`, and so on in `sub_agent` events.

## With -p

`anchor -p "..." --json` writes the events, then a final `result` line:

```json
{
  "type": "result",
  "status": "completed",
  "text": "The final answer.",
  "check": null,
  "files_changed": ["src/app.ts"],
  "session": "20260928-225355-be7b",
  "usage": { "input": 5210, "output": 84, "cached": 2304 }
}
```

| Field     | Description                                                                      |
| --------- | -------------------------------------------------------------------------------- |
| `status`  | How the run ended: `completed`, `cancelled`, `loop_stopped`, `round_limit`, `timed_out` or `error`. |
| `text`    | The final answer, or empty if the turn didn't complete.                          |
| `check`   | With `--until`: `passed`, `no_changes` or `out_of_rounds`. Otherwise `null`.     |
| `files_changed` | Files anchor's file tools wrote during the run, relative to the directory. Changes made by shell commands aren't tracked; use `git status` for those. |
| `session` | The session id, for `--resume`.                                                  |
| `usage`   | Token totals for the run, sub-agents included.                                   |

Approvals can't be answered in this mode: anything that would ask is refused and reported as a
`notice`, unless `--allow` covers it or `--yolo` is on.

## The editor protocol

`anchor --json` without `-p` runs a session driven over stdin and stdout, for editors and other
programs.

### Events it adds

| `type`             | Fields                           | When                                                  |
| ------------------ | -------------------------------- | ----------------------------------------------------- |
| `ready`            | `session`, `model`, `tools`      | At startup and after each turn: it can take input.    |
| `approval_request` | `id`, `title`, `detail`, `always` | An action needs approval. `detail` is the diff for writes; `always` is the "always" label, or `null` if not offered. |
| `question`         | `id`, `text`, `options`, `allowOther` | The model asks a multiple-choice question with `ask_user`. The turn waits for a `question_response`. |
| `error`            | `message`                        | A request was invalid.                                |

### Requests

Send one JSON object per line on stdin:

```json
{"type":"user_input","text":"fix the failing test"}
{"type":"approval_response","id":"a1","answer":"yes"}
{"type":"question_response","id":"q2","answer":"SQLite"}
{"type":"cancel"}
```

| `type`              | Fields             | Effect                                                         |
| ------------------- | ------------------ | -------------------------------------------------------------- |
| `user_input`        | `text`             | Start a turn. Rejected while a turn is running.               |
| `approval_response` | `id`, `answer`     | Answer an `approval_request`: `yes`, `no` or `always`. Anything else is `no`. |
| `question_response` | `id`, `answer`     | Answer a `question`: one of its `options`, or any other text if `allowOther` is true. A missing or empty `answer`, or text that isn't an option when `allowOther` is false, dismisses the question. |
| `cancel`            |                    | Cancel the running turn.                                       |

The session ends when stdin closes.

### A session

```json
→ {"type":"ready","session":"20260928-231002-4f1a","model":"claude-sonnet-5","tools":["read_file","list_dir","glob","grep","write_file","edit_file","shell","agent","agents"]}
← {"type":"user_input","text":"create hello.txt"}
→ {"type":"tool_start","id":"toolu_1","name":"write_file","summary":"hello.txt"}
→ {"type":"approval_request","id":"a1","title":"Create hello.txt","detail":"@@ -0,0 +1 @@\n+hello\n","always":"all file writes in the workspace"}
← {"type":"approval_response","id":"a1","answer":"yes"}
→ {"type":"file_changed","path":"hello.txt","added":1,"removed":0}
→ {"type":"tool_end","id":"toolu_1","name":"write_file","ok":true,"result":"..."}
→ {"type":"text","text":"Created hello.txt."}
→ {"type":"usage","input":4810,"output":61,"cached":0}
→ {"type":"turn_end","reason":"completed","detail":null}
→ {"type":"ready","session":"20260928-231002-4f1a","model":"claude-sonnet-5","tools":["..."]}
```

`→` is anchor's stdout; `←` is what you write to its stdin.
