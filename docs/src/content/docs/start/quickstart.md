---
title: Quickstart
description: Set an API key, start anchor in a project, and make your first change.
---

## 1. Set an API key

anchor reads API keys from the environment. With either of these set, it picks a default model:

| Variable            | Default model     |
| ------------------- | ----------------- |
| `ANTHROPIC_API_KEY` | `claude-sonnet-5` |
| `XAI_API_KEY`       | `grok-4.5`        |

```sh
export ANTHROPIC_API_KEY=sk-ant-...
```

For OpenAI (`OPENAI_API_KEY`) or any other OpenAI-compatible server, pass `--model` or see
[Configuration](/anchor/start/configuration/).

## 2. Start a session

Run anchor from the directory you want it to work in. That directory is its sandbox: it can read
anything inside it freely, and asks before reading outside it.

```sh
cd my-project
anchor
```

```
anchor · claude-sonnet-5 · /home/you/my-project
/help for commands, Ctrl+D to exit

›
```

## 3. Ask for something

```
› the add function in calc.py is wrong, fix it
  ↳ read_file calc.py
  ? Edit calc.py
    @@ -1,2 +1,2 @@
     def add(a, b):
    -    return a - b
    +    return a + b
  Allow? [y]es [n]o [a]lways: all file writes in the workspace › yes
  ✎ calc.py +1 -1
Fixed: `add` was subtracting instead of adding.
```

Every write shows you the diff first. Press:

- **y** to allow this one action,
- **a** to allow this kind of action for the rest of the session,
- anything else to decline. The model is told you declined and not to retry.

Reads inside the directory and read-only shell commands like `ls`, `grep` or `git diff` run
without asking.

## 4. Keep going until the tests pass

Tell anchor how to check the work, and it keeps going until the check passes:

```
› /until python3 -m unittest -q
› make all the tests pass
  ...
  ✗ check failed (round 1 of 5): python3 -m unittest -q
  ...
  ✓ check passed: python3 -m unittest -q
```

See [Working until a check passes](/anchor/guides/until/).

## 5. Undo, resume, exit

- `/undo` reverts the files changed in the last turn.
- **Ctrl+C** cancels a running turn. Pressed twice at the prompt, or **Ctrl+D**, exits.
- `anchor --resume` picks up the latest session in this directory.

## Give it project instructions

anchor adds `AGENTS.md` in the working directory to its system prompt. Use it for build commands,
conventions, and anything the model should know about the project:

```markdown
# AGENTS.md
- Build with `dotnet build`, test with `dotnet test`.
- Keep public APIs documented with XML comments.
```
