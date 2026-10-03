---
title: Sub-agents
description: Hand work to sub-agents that run in the background with their own context, check on them, and define your own named agents.
---

A sub-agent is a fresh agent with its own conversation. The main agent gives it a task, it works
with its own tools, and only its final report comes back. The investigation's file reads and search
results never fill the main agent's context.

The model decides when to use sub-agents. You can also ask directly: "use a sub-agent to find every
place we parse dates".

## The default sub-agent

Without a name, a sub-agent is read-only. It has `read_file`, `list_dir`, `glob`, `grep` and
`skill`, and nothing that writes or runs commands.

## Background sub-agents

Sub-agents run in the background. The model starts one with the `agent` tool, gets an id like
`agent-1` back at once, and can keep working or start more, up to **4** at a time. When a question
splits into independent parts, it starts one per part:

```
  ↳ agent Find where sessions are written
  ↳ agent Find where sessions are read back
    [agent-1] ↳ grep SessionLog
    [agent-2] ↳ read_file src/Core/SessionLog.cs
    [agent-1] ↳ read_file src/Cli/Repl.cs
```

Each report comes back to the model as a message when that sub-agent finishes. If the model
finishes its own reply while sub-agents are still running, the turn waits for them.

### Checking on them

In the full-screen REPL, the status line shows each running sub-agent with its tool calls and
tokens so far, updated as it works:

```
⠙ working · claude-sonnet-5 · 12% context · agent-1: 7 calls, 12k tokens · agent-2: 3 calls, 4.1k tokens
```

For more detail, ask. While the turn waits, type a message, such as "how are the agents doing?". The model reads it
straight away and can call `agent_status`. That shows each sub-agent's state, how long it has run,
how many tool calls it has made and the latest ones. The model can also stop one that is no longer
needed with `agent_stop`.

You don't have to wait for the turn to pause. Anything you type while it runs reaches the model
after its current step.

### Lifetime

Sub-agents belong to the turn that started them. Ctrl+C cancels the turn and every sub-agent with
it. If the turn ends any other way, such as hitting the loop guard, sub-agents still running are
stopped and their reports dropped.

### Approvals

Sub-agents can ask for approval while other agents work. Prompts appear one at a time, and a
sub-agent's prompt names it:

```
  ? [reviewer-2] Run: dotnet test
```

## Named sub-agents

Define your own as Markdown files with YAML frontmatter:

- `.agents/agents/<name>.md` in the project, or
- `~/.anchor/agents/<name>.md` for all your projects.

When both define the same name, the project's wins.

```markdown
---
name: reviewer
description: Reviews a diff for bugs and missing tests.
tools: [read_file, grep, glob, shell]
model: claude-opus-5-5
---
Review the change you're given. Report problems with file:line, most serious first.
```

| Field         | Required | Meaning                                                                 |
| ------------- | -------- | ----------------------------------------------------------------------- |
| `name`        | yes      | Lowercase letters, digits and single hyphens, up to 64 characters.     |
| `description` | yes      | When to use it. The main agent sees this. Up to 1,024 characters.      |
| `tools`       | no       | A list or comma-separated string of [tool names](/anchor/reference/tools/). Defaults to the read-only set. |
| `model`       | no       | A model for this agent. Defaults to the session's current model.       |

The body is added to the sub-agent's system prompt.

## Rules

- Sub-agents go through the same gate, policy and approvals as the main agent. A named agent with
  `write_file` still shows you the diff and asks.
- Two sub-agents, or a sub-agent and the main agent, can work on the same files at once. Give
  agents that write separate files, or run them one after another.
- Sub-agents can't start their own sub-agents.
- A sub-agent can't ask you questions. If something is ambiguous, it makes a reasonable choice and
  says so in its report.
- Sub-agent token usage counts toward the session total in `/context`.
- `/agents` lists the agents available.
