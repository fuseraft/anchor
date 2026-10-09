---
title: REPL commands
description: Every slash command in an interactive anchor session.
---

Type these at the `›` prompt. Anything else is a message to the model, except lines starting with
`!`, which run in your own shell.

## Conversation

### /model

```
/model                   pick a model and save it as the default
/model claude-opus-5-5   switch models for this session
```

Without a name, `/model` lists the models on the server the current one comes from, with the
current one chosen, as the [setup wizard](/anchor/start/configuration/#the-setup-wizard) does. The
pick becomes `provider.model` in the config, so later sessions start with it; Esc changes nothing.
To use another provider, run [`/setup`](#setup).

With a name, the switch lasts for the session. Either way the conversation carries over to the new
model. The model name follows the same rules as `--model`; see
[Configuration](/anchor/start/configuration/#choosing-a-model).

### /setup

Runs the [setup wizard](/anchor/start/configuration/#the-setup-wizard): choose a provider, save its
key and pick a model. The session switches to the chosen model and keeps the conversation.

### /theme

```
/theme          list the color themes, the current one marked
/theme mono     switch themes
```

The switch lasts for the session and applies to what's drawn from then on. To keep a theme, pick it
in [`/setup`](#setup), or set [`theme`](/anchor/reference/config/#theme-and-colors) in the config.

### /context

Shows how full the context window is, the number of messages, token totals for the whole
session (sub-agents included), and the size past which anchor compacts on its own.

### /compact

Summarizes older turns now, instead of waiting for the context to reach 80%. The last turn is kept
as it is. If there's nothing old enough to summarize, it says so.

### /clear

Forgets the conversation. The model starts fresh, and the full-screen view is emptied; your files are
untouched. With `--plain`, what was printed stays in the terminal's scrollback.

## Work

### /until

```
/until <command>   set a check that runs after each turn
/until             show the current check
/until off         clear it
```

Each message keeps going until the command exits 0, a round changes no files, or 5 rounds have run.
See [Working until a check passes](/anchor/guides/until/).

### /undo

Reverts the file changes of the most recent turn that made any, and tells the model. Files changed
since anchor wrote them are skipped. Changes made by shell commands aren't tracked. Repeat to go
further back, up to 20 turns.

### /approvals

```
/approvals         list the commands and MCP tools saved as "always" for this directory
/approvals clear   forget every "always" answer, saved or from this session
```

See [Approvals](/anchor/guides/repl/#approvals).

## Sessions

### /sessions

Lists the ten most recent sessions for this directory, marking the current one with `*`. Resume one
with `anchor --resume <id>`.

## Extensions

### /agents

Lists the default sub-agent and any [named sub-agents](/anchor/guides/sub-agents/), with their
tools and models.

### /skills

Lists installed [skills](/anchor/guides/skills/) and their descriptions.

### /mcp

```
/mcp                   each server's state and tool count
/mcp login <server>    sign in to a server now
/mcp logout <server>   forget a server's tokens
```

See [MCP servers](/anchor/guides/mcp/).

## Other

| Input           | Effect                                                           |
| --------------- | ---------------------------------------------------------------- |
| `/help`         | List commands.                                                   |
| `/exit`, `/quit` | Quit.                                                           |
| `!<command>`    | Run a command in your own shell. The model never sees it.        |
| Ctrl+C          | Cancel the running turn; otherwise clear the prompt. Twice: exit. |
| Enter mid-turn  | Send what you typed into the running turn; commands wait for it to end. |
| Alt+Enter       | New line in the message (Shift+Enter where the terminal supports it). |
| ↑ / ↓           | Message history, from the first or last line of the prompt.     |
| PgUp / PgDn     | Scroll the conversation; Ctrl+End follows it again.             |
| Ctrl+O          | Show the last tool output or diff in full; ←/→ for earlier ones, Esc to close. |
| Ctrl+D          | Exit.                                                            |
