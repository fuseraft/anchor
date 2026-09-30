---
title: REPL commands
description: Every slash command in an interactive anchor session.
---

Type these at the `›` prompt. Anything else is a message to the model, except lines starting with
`!`, which run in your own shell.

## Conversation

### /model

```
/model                   show the current model and provider
/model claude-opus-5-5   switch models
```

The conversation carries over to the new model. The model name follows the same rules as
`--model`; see [Configuration](/anchor/start/configuration/#choosing-a-model).

### /setup

Runs the [setup wizard](/anchor/start/configuration/#the-setup-wizard): choose a provider, save its
key and pick a model. The session switches to the chosen model and keeps the conversation.

### /context

Shows how full the context window is, the number of messages, and token totals for the whole
session, sub-agents included.

### /compact

Summarizes older turns now, instead of waiting for the context to reach 80%. The last turn is kept
as it is. If there's nothing old enough to summarize, it says so.

### /clear

Forgets the conversation. The model starts fresh; your files are untouched.

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
| Ctrl+C          | Cancel the running turn. Twice at the prompt: exit.              |
| Ctrl+D          | Exit.                                                            |
