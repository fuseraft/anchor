---
title: How anchor works
description: anchor's architecture and the rules it's built on - one loop, one gate, safe defaults, and a core that never prints.
---

anchor is a minimal coding harness: one model working in one directory through a few tools, plus
sub-agents, skills and MCP. It's written in C# on .NET 10 and
[`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai), and it
stays under 15,000 lines of source. A new feature has to justify every line it adds.

The full design notes are in
[`DESIGN.md`](https://github.com/fuseraft/anchor/blob/main/DESIGN.md). This page is the short
version.

## The rules

### 1. Own the loop

anchor drives the model directly instead of handing tool calls to a library's automatic loop. A
turn ends when the model answers in text. There's no cap on rounds; instead, a loop guard stops a
turn that repeats the same call 5 times or fails 3 calls in a row.

### 2. One gate

Tools reach the disk, processes and MCP servers only through one class, the `Gate`. Every action
takes the same path:

```
policy (allow / ask / deny)  →  approval (if ask)  →  effect
```

A test fails if any tool touches files or starts a process directly. Paths are checked where they
really point, with symlinks resolved. A write shows its diff first, and a file that changes while
you look at the diff is never overwritten.

### 3. Safe by default

Writes and commands that aren't provably read-only ask first. The sandbox is the directory anchor
was started in. `--yolo` turns the questions off.

### 4. Hard denials that --yolo doesn't lift

Secret and credential files, privilege escalation, raw disk writes, deleting system directories, and
piping downloads into a shell are always denied. Secret values are masked in all output the model
sees. See [Safety and approvals](/anchor/guides/safety/).

### 5. The core never prints

The core emits typed events, and a renderer draws them. The terminal UI, `-p` and `--json` are three
renderers over the same events, which is why the [JSON protocol](/anchor/reference/json/) exposes
everything the terminal shows.

### 6. Messages are typed

Every message carries its kind (user, assistant, tool, summary, note), so anchor never parses text
prefixes to tell them apart. That's what makes compaction and session replay reliable.

## The pieces

```
src/Anchor/
  Core/Agent.cs          the turn loop
  Core/Gate.cs           the only path to disk, processes and MCP tools
  Core/Policy.cs         pure allow / ask / deny rules
  Core/ShellCommand.cs   a conservative bash reader: hard denials, read-only commands
  Core/Compactor.cs      summarize, trim, drop rounds
  Core/Until.cs          work until a check command passes
  Core/SessionLog.cs     append-only JSONL sessions
  Core/SubAgentRunner.cs fresh agents per task in the background, same gate
  Tools/                 read, list, glob, grep, write, edit, shell, agent tools, skill, ask_user
  Mcp/                   config and trust, background connections, OAuth, keychain
  Providers/             Anthropic (official SDK, cached) and OpenAI-compatible, with retries
  Cli/                   REPL, rendering, approvals, -p and --json
```

## What anchor leaves out

Multi-agent orchestration (graphs, routing, validators), plans, memory, telemetry and a plugin
registry. Any of these could be added later as a tool behind the gate.

Deciding when work is done is left to a command you choose, not to a model. That's why
[`/until`](/anchor/guides/until/) takes a check command rather than a goal for a judge model to
evaluate.

## Tests

- Tests use a scripted fake model and never call a live LLM.
- Security tests use an approver that always says yes, so they prove the policy holds on its own.
- Each milestone is also checked by hand against a real model.
