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

## Named providers (LiteLLM and other proxies)

To reach several models through one server, such as a LiteLLM proxy at work, give the server a name
under `providers` and write its models as `<name>/<model>`:

```json
{
  "providers": {
    "work": {
      "endpoint": "https://litellm.example.com/v1",
      "apiKeyEnv": "LITELLM_API_KEY"
    }
  },
  "provider": { "model": "work/claude-sonnet-5" }
}
```

Everything after the first `/` is sent to the server as the model name, so `work/anthropic/claude-sonnet-5`
asks for `anthropic/claude-sonnet-5`, and Bedrock-style ids such as `work/anthropic.claude-sonnet-5` or
`work/xai.grok-4.6` are passed through unchanged. The same names work with `--model`, `/model` and a sub-agent's
`model:`, so you can switch between the proxy's models mid-session:

```text
/model work/gpt-5
```

A provider speaks the OpenAI chat completions API unless you set `"type": "anthropic"`. `headers` adds
request headers, with `${VAR}` replaced from the environment, and `apiKeyEnv` can be left out when the
server needs no key or authenticates through a header:

```json
{
  "providers": {
    "work": {
      "endpoint": "https://litellm.example.com/v1",
      "headers": { "Authorization": "Bearer ${LITELLM_API_KEY}", "X-Team": "platform" },
      "contextWindow": 128000
    }
  }
}
```

A name that doesn't start with a configured provider is treated as before, so `claude-sonnet-5` still
goes straight to Anthropic.

## Context window

anchor keeps the conversation under the model's context window. It assumes 200,000 tokens for
Claude, 256,000 for Grok 4, and 128,000 for anything else, going by the model name: any name
containing `claude-` or `grok-4` counts, so `anthropic.claude-sonnet-5` and `xai.grok-4.6` are
recognized. Set `provider.contextWindow`, or `contextWindow` on a named
provider, if your model's window is different, especially for local models with small windows.

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
| `approvals.json`           | "Always" answers saved per directory                  |
| `skills/<name>/SKILL.md`   | Your personal [skills](/anchor/guides/skills/)        |
| `agents/<name>.md`         | Your personal [sub-agents](/anchor/guides/sub-agents/) |
