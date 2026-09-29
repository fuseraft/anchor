---
title: Sub-agents
description: Hand work to sub-agents with their own context, run read-only investigations in parallel, and define your own named agents.
---

A sub-agent is a fresh agent with its own conversation. The main agent gives it a task, it works
with its own tools, and only its final report comes back. The investigation's file reads and search
results never fill the main agent's context.

The model decides when to use sub-agents. You can also ask directly: "use a sub-agent to find every
place we parse dates".

## The default sub-agent

Without a name, a sub-agent is read-only. It has `read_file`, `list_dir`, `glob`, `grep` and
`skill`, and nothing that writes or runs commands.

## Parallel sub-agents

When a question splits into independent parts, the model can run up to **4** default sub-agents at
the same time with the `agents` tool:

```
  ↳ agents 3 tasks
    [agent 1] ↳ read_file src/Core/Gate.cs
    [agent 3] ↳ read_file src/Core/Compactor.cs
    [agent 2] ↳ read_file src/Core/Agent.cs
```

Parallel sub-agents never show you an approval prompt, so two prompts can't appear at once.
Anything that would ask, such as reading outside the directory, is refused, and the sub-agent
reports what it couldn't do. Named sub-agents always run one at a time.

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
- Sub-agents can't start their own sub-agents.
- A sub-agent can't ask you questions. If something is ambiguous, it makes a reasonable choice and
  says so in its report.
- Sub-agent token usage counts toward the session total in `/context`.
- `/agents` lists the agents available.
