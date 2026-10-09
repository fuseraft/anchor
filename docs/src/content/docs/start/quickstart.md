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

Or run `anchor setup` to choose a provider, save its key and pick a model
from a list. It also sets up LiteLLM and other OpenAI-compatible servers. anchor offers it on
first start when no key is set. For more, see [Configuration](/anchor/start/configuration/).

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
- **a** to allow this kind of action from now on: file writes for the rest of the session, a
  command's programs or an MCP tool saved for this directory,
- **n** or **Esc** to decline. The model is told you declined and not to retry.

Other keys are ignored, so a keystroke meant for the prompt never answers.

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

## Find your way around

- `@src/app.py` in a message attaches the file; Tab completes the path as you type.
- **Ctrl+O** shows the last tool's whole output, or a diff in full.
- **Ctrl+F** finds text in the conversation, and **Ctrl+R** finds an earlier message.
- `/copy` copies the last reply; `/copy code` copies a code block from it.
- `/help` lists every command and key. [Using the REPL](/anchor/guides/repl/) has the details.

## Give it project instructions

anchor adds `AGENTS.md` in the working directory to its system prompt. Use it for build commands,
conventions, and anything the model should know about the project:

```markdown
# AGENTS.md
- Build with `dotnet build`, test with `dotnet test`.
- Keep public APIs documented with XML comments.
```
