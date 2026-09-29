---
title: Sessions and context
description: How anchor saves and resumes sessions, and how it keeps long conversations inside the model's context window.
---

## Sessions

Every session is saved as you go, in `~/.anchor/sessions/`, as a JSONL file readable only by you.

```sh
anchor --resume            # the latest session in this directory
anchor --resume 20260928   # a session by id, or a unique prefix of one
```

Resuming re-shows the last three turns so you can see where you left off. Sessions from `-p` and
`--json` runs can be resumed too, interactively or with another `-p`:

```sh
anchor -p "find the bug in the parser"
anchor --resume -p "now write a test for it"
```

`/sessions` lists the ten most recent sessions for the current directory, with their first message.

## Context

A model can only see so much at once. anchor keeps the conversation under the model's context
window automatically. You can check how full it is with `/context`:

```
› /context
~84,120 of 200,000 tokens (42%), 57 messages
Session so far, including sub-agents: in 612,004 · out 18,331 · cached 540,212
```

The numbers come from the provider's own usage reports. When a provider doesn't report usage,
anchor estimates, and says so.

### What happens as it fills

When the context reaches **80%** of the window, anchor works through these steps, stopping as soon
as there's room:

1. **Summarize older turns.** The model writes a summary of the earlier conversation, which replaces
   those turns. Recent whole turns are kept word for word, up to 20% of the window, and the current
   turn always is. A summary that isn't smaller than what it replaces is rejected.
2. **Trim old tool content.** Large old tool results become a short placeholder with a
   300-character preview and a note to re-run the tool with a narrower range. Large old tool
   arguments, like the content of a file write, become a character count, since the file on disk
   already has them.
3. **Drop the oldest steps of the current turn.** For one very long turn, anchor removes its oldest
   steps and puts a note after your request listing what they did. A tool call is never separated
   from its result.

The latest step is never trimmed or dropped, because the model hasn't seen its results yet.

If the provider still reports the request is too long, anchor reduces the context once more and
retries.

### By hand

- `/compact` summarizes older turns now.
- `/clear` forgets the conversation entirely.

The context window size comes from the model family, or `provider.contextWindow` in the
[config](/anchor/reference/config/).
