---
title: Using the REPL
description: How an interactive anchor session works - turns, approvals, slash commands, shell escapes and cancelling.
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

## Approvals

When the model wants to do something that needs your say-so, anchor asks:

```
  ? Run: npm install lodash
  Allow? [y]es [n]o [a]lways: commands using npm › 
```

Press a single key:

| Key       | Effect                                                         |
| --------- | -------------------------------------------------------------- |
| `y`       | Allow this action once.                                        |
| `a`       | Allow this kind of action for the rest of the session.         |
| any other | Decline. The model is told not to retry, and to ask you if it's stuck. |

What "always" covers depends on the action: all file writes inside the directory, commands that
use the same programs, or every call to one MCP tool. It lasts until you exit. See
[Safety and approvals](/anchor/guides/safety/) for what asks and what doesn't.

## Pasting

A multi-line paste is sent as one message. If the paste doesn't end in a newline, its last line
stays open so you can finish it before pressing Enter.

## Slash commands

| Command            | What it does                                                                |
| ------------------ | --------------------------------------------------------------------------- |
| `/help`            | List commands.                                                              |
| `/model [name]`    | Show the model, or switch to another. The conversation carries over.       |
| `/context`         | How full the context window is, and token usage for the session.           |
| `/until [check]`   | Keep each turn going until a command exits 0. [More](/anchor/guides/until/) |
| `/compact`         | Summarize older turns now.                                                  |
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
check on things without spending tokens.

## Undo

`/undo` reverts the file changes of the most recent turn that made any, and tells the model it did.

- It restores files written through anchor's file tools. Changes made by shell commands aren't
  tracked.
- If a file changed again after anchor wrote it, `/undo` leaves it alone and says so.
- Run it again to step further back, up to 20 turns.

## Cancelling and exiting

- **Ctrl+C** during a turn cancels it. The conversation stays valid: any tool calls that didn't
  finish are marked as cancelled.
- **Ctrl+C** twice at the prompt, or **Ctrl+D**, exits. The session is saved as you go, so you can
  [resume it](/anchor/guides/sessions/) later.
