---
title: Using the REPL
description: How an interactive anchor session works - the screen, turns, typing while it works, approvals, slash commands, shell escapes and cancelling.
---

Running `anchor` with no prompt starts an interactive session in the current directory.

```
anchor · claude-sonnet-5 · /home/you/my-project
/help for commands, Ctrl+D to exit

›
```

## Turns

Anything you type that doesn't start with `/` or `!` is a message to the model. The model then
works in a loop: it calls tools, reads their results, and calls more tools until it answers in
plain text. That whole loop is one **turn**.

While it works, anchor shows each tool call and the files it changes:

```
  ↳ grep def parse
  ↳ read_file src/parser.py
  ✎ src/parser.py +4 -1
  in 12,410 · out 380 · cached 9,728
```

The last line is the token usage for the turn.

There is no fixed limit on how many tool calls a turn makes. Instead, anchor's loop guard stops a
turn that is going in circles:

- The same call with the same arguments 3 times in a row gets a warning; 5 times stops the turn.
- 2 failed calls in a row get a warning; 3 in a row stop the turn.

## The screen

The REPL is a full-screen terminal UI (TUI): the conversation on top, the prompt below a rule,
and a status line at the bottom showing the model, how full the context is, and a spinner while a
turn runs.

```
anchor · claude-sonnet-5 · /home/you/my-project
› fix the build
Looking at the failing target first.
  ↳ shell dotnet build
  ✎ src/Foo.cs +4 -1
──────────────────────────────────────────────────────────────
› also run the tests
⠼ working · claude-sonnet-5 · 12% context    Enter adds to the turn · Ctrl+C cancel
```

| Key                         | Effect                                                        |
| --------------------------- | ------------------------------------------------------------- |
| Enter                       | Send the message.                                             |
| Alt+Enter, Shift+Enter      | New line in the message.                                      |
| ↑ / ↓                       | On the first or last line: earlier messages (history).        |
| ←, →, Home, End, Ctrl+Z     | Edit the message as in any editor; Ctrl+U deletes to the line start. |
| PgUp / PgDn, mouse wheel    | Scroll the conversation. Ctrl+End jumps back to the bottom.   |
| Ctrl+O                      | Show the last tool output or diff in full (see below).        |
| Ctrl+C                      | Cancel the running turn; otherwise clear the message; twice to exit. |
| Ctrl+D                      | Exit, when the message is empty.                              |

Typing `/` offers to complete a slash command.

anchor's replies are Markdown, and the screen styles them as they arrive: headings, **bold**,
*italic*, `code`, lists, task lists, quotes and links. Code blocks are syntax highlighted in the
terminal's own colors, for the languages VS Code knows. Tables are lined up into columns, and a
table wider than the screen wraps its widest columns. A line can still change until the model
finishes it, and a table's columns can shift until its last row is in.

The context percentage in the status line says how close anchor is to compacting the
conversation, which happens on its own once the context passes 80% of the model's window. It's
green while that's out of reach, yellow when a turn as big as the last one would get there, and
red once it's past, meaning anchor compacts before its next request. `/context` shows the numbers.

When you exit, the conversation is printed to the terminal, so it stays in your scrollback.
`anchor --plain` runs the older line-by-line REPL instead, and anchor uses it on its own when
stdin or stdout isn't a terminal. It can't change text once it's printed, so it shows replies as
plain Markdown.

## Seeing a tool's whole output

The conversation shows one line per tool call, the first line of an error, and at most 80 lines
of a diff. **Ctrl+O** opens the whole of the latest tool output, or the diff of the latest
approval, in place of the conversation:

| Key                    | Effect                                     |
| ---------------------- | ------------------------------------------ |
| ↑ / ↓, PgUp / PgDn     | Scroll. Home and End jump to either end.   |
| ← / →                  | The output before or after this one.       |
| Esc, `q`, Ctrl+O       | Back to the conversation.                  |

The last 100 outputs are kept, including those of sub-agents. It works while an approval is
waiting, so you can read a long diff before answering.

## Typing while anchor works

You don't have to wait for a turn to finish. The prompt stays live while the model works:

- A message you send joins the running turn: the model reads it right after the tool calls it's
  making now, so you can correct course mid-turn ("skip the tests, just fix the build"). If the
  turn ends before that, the message becomes the next turn.
- A `/command` or `!command` waits until the turn ends, then runs.
- A draft you haven't sent stays in the prompt when the turn ends.
- Cancelling the turn with **Ctrl+C** also drops what you sent during it.

## Approvals

When the model wants to do something that needs your say-so, anchor shows the request (with a
diff for file writes) and asks in place of the prompt:

```
  ? Run: npm install lodash
──────────────────────────────────────────────────────────────
Allow? [y]es [n]o [a]lways: commands using npm
```

Press a single key:

| Key        | Effect                                                         |
| ---------- | -------------------------------------------------------------- |
| `y`        | Allow this action once.                                        |
| `a`        | Allow this kind of action from now on (see below).             |
| `n`, Esc   | Decline. The model is told not to retry, and to ask you if it's stuck. |

Other keys are ignored, and so is everything typed in the moment the question appears, so a `y`
you were typing into a message never approves anything. Your unsent message comes back once
you've answered. Questions from `ask_user` and the `/setup` wizard appear in the same place. (In
`--plain` mode any key other than `y` or `a` declines.)

What "always" covers depends on the action: all file writes inside the directory, commands that
use the same programs, or every call to one MCP tool.

"Always" for commands and MCP tools is saved for this directory, so the next session (including
`-p`) doesn't ask again; the label ends in "(saved for this directory)" when it will be. Two kinds
last only until you exit: file writes, because seeing each diff is the point, and commands using an
interpreter (`bash`, `python3`, `node`, ...), which can run anything. `/approvals` lists what is
saved and `/approvals clear` forgets it. See [Safety and approvals](/anchor/guides/safety/) for
what asks and what doesn't.

## Pasting

A multi-line paste goes into the prompt as it is, and you send it with Enter.

## Slash commands

| Command            | What it does                                                                |
| ------------------ | --------------------------------------------------------------------------- |
| `/help`            | List commands.                                                              |
| `/model [name]`    | Show the model, or switch to another. The conversation carries over.       |
| `/setup`           | Choose a provider, save its key and pick a model. [More](/anchor/start/configuration/#the-setup-wizard) |
| `/context`         | How full the context window is, and token usage for the session.           |
| `/until [check]`   | Keep each turn going until a command exits 0. [More](/anchor/guides/until/) |
| `/compact`         | Summarize older turns now.                                                  |
| `/approvals`       | List saved "always" answers. `/approvals clear` forgets them.              |
| `/undo`            | Revert the files changed in the last turn that changed any.                |
| `/sessions`        | List recent sessions in this directory.                                    |
| `/agents`          | List sub-agents.                                                            |
| `/skills`          | List skills.                                                                |
| `/mcp`             | List MCP servers. `/mcp login` and `/mcp logout <server>` manage sign-ins. |
| `/clear`           | Forget the conversation.                                                    |
| `/exit`, `/quit`   | Quit.                                                                       |

The [commands reference](/anchor/reference/commands/) has the details.

## Running your own commands

A line starting with `!` runs in your own shell, in the working directory:

```
› !git status
```

The model never sees these commands or their output, and they don't need approval. Use them to
check on things without spending tokens. The output appears in the conversation; the command gets
no input, so interactive programs (an editor, a pager) need `--plain`. Ctrl+C stops it.

## Undo

`/undo` reverts the file changes of the most recent turn that made any, and tells the model it did.

- It restores files written through anchor's file tools. Changes made by shell commands aren't
  tracked.
- If a file changed again after anchor wrote it, `/undo` leaves it alone and says so.
- Run it again to step further back, up to 20 turns.

## Cancelling and exiting

- **Ctrl+C** during a turn cancels it. The conversation stays valid: any tool calls that didn't
  finish are marked as cancelled.
- **Ctrl+C** twice with an empty prompt, or **Ctrl+D**, exits. The session is saved as you go, so you can
  [resume it](/anchor/guides/sessions/) later.
