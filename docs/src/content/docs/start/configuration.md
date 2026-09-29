---
title: Configuration
description: Choose a model and provider, point anchor at an OpenAI-compatible server, and move its home directory.
---

anchor works with no config file when `ANTHROPIC_API_KEY` or `XAI_API_KEY` is set. To choose a
model, add a provider, or configure MCP servers, create `~/.anchor/config.json`:

```json
{
  "provider": { "model": "claude-sonnet-5" }
}
```

The file accepts comments and trailing commas. The full schema is in the
[config reference](/anchor/reference/config/).

## Choosing a model

The model comes from, in order:

1. `--model` (or `-m`) on the command line,
2. `provider.model` in the config,
3. the default for the first API key that is set: `claude-sonnet-5` for `ANTHROPIC_API_KEY`, then
   `grok-4.5` for `XAI_API_KEY`.

You can switch models mid-session with `/model <name>`. The conversation carries over.

anchor recognizes a model's provider from its name:

| Name starts with       | Provider                        | API key             |
| ---------------------- | ------------------------------- | ------------------- |
| `claude-`              | Anthropic (native API, cached)  | `ANTHROPIC_API_KEY` |
| `grok-`                | xAI (OpenAI-compatible)         | `XAI_API_KEY`       |
| `gpt-`, `o1`, `o3`, `o4` | OpenAI                        | `OPENAI_API_KEY`    |

## Other OpenAI-compatible servers

For any other model, such as one served by Ollama, vLLM or LM Studio, set the endpoint and the name
of the environment variable that holds its key:

```json
{
  "provider": {
    "model": "qwen3-coder",
    "endpoint": "http://localhost:11434/v1",
    "apiKeyEnv": "OLLAMA_API_KEY",
    "contextWindow": 32768
  }
}
```

The variable must be set, even if the server ignores it (`export OLLAMA_API_KEY=unused`).

## Context window

anchor keeps the conversation under the model's context window. It assumes 200,000 tokens for
Claude, 256,000 for Grok 4, and 128,000 for anything else. Set `provider.contextWindow` if your
model's window is different, especially for local models with small windows.

## The anchor home directory

Config, sessions and remembered MCP approvals live in `~/.anchor`. Set `ANCHOR_HOME` to use another
directory:

```sh
ANCHOR_HOME=/tmp/anchor-scratch anchor
```

| Path                       | What it holds                                         |
| -------------------------- | ----------------------------------------------------- |
| `config.json`              | Your configuration                                    |
| `sessions/`                | Saved sessions, one JSONL file each                   |
| `mcp-trust.json`           | Your answers about project MCP servers               |
| `skills/<name>/SKILL.md`   | Your personal [skills](/anchor/guides/skills/)        |
| `agents/<name>.md`         | Your personal [sub-agents](/anchor/guides/sub-agents/) |
