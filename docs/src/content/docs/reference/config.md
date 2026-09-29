---
title: Config file
description: The full schema of ~/.anchor/config.json and a project's .mcp.json.
---

anchor reads `config.json` from `~/.anchor`, or from `$ANCHOR_HOME` when that's set. The file is
optional. Keys are case-insensitive, and comments and trailing commas are allowed.

```json
{
  // Which model to use, and how to reach it.
  "provider": {
    "model": "claude-sonnet-5",
    "name": "anthropic",
    "endpoint": null,
    "apiKeyEnv": "ANTHROPIC_API_KEY",
    "contextWindow": 200000
  },
  // MCP servers available in every project.
  "mcpServers": {
    "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
  }
}
```

## provider

| Key             | Type   | Description                                                                                   |
| --------------- | ------ | --------------------------------------------------------------------------------------------- |
| `model`         | string | The model name. `--model` overrides it.                                                       |
| `name`          | string | `anthropic` or `openai`. Inferred from the model name when omitted; `openai` for unknown names. |
| `endpoint`      | string | The API base URL, for OpenAI-compatible servers other than OpenAI and xAI.                   |
| `apiKeyEnv`     | string | The environment variable that holds the API key.                                             |
| `contextWindow` | number | The model's context window in tokens.                                                        |

A model name anchor doesn't recognize needs both `endpoint` and `apiKeyEnv`. Recognized prefixes and
their defaults:

| Prefix                   | `name`      | `endpoint`                | `apiKeyEnv`         | `contextWindow` |
| ------------------------ | ----------- | ------------------------- | ------------------- | --------------- |
| `claude-`                | `anthropic` | Anthropic's API           | `ANTHROPIC_API_KEY` | 200,000         |
| `grok-`                  | `openai`    | `https://api.x.ai/v1`     | `XAI_API_KEY`       | 256,000 for `grok-4*`, else 128,000 |
| `gpt-`, `o1`, `o3`, `o4` | `openai`    | OpenAI's API              | `OPENAI_API_KEY`    | 128,000         |

Any other model defaults to a 128,000-token window.

## mcpServers

An object mapping a server name to its configuration. The same shape is used in a project's
`.mcp.json`, under a top-level `mcpServers` key.

### stdio servers

| Key       | Type             | Description                                                     |
| --------- | ---------------- | --------------------------------------------------------------- |
| `command` | string           | The program to start. Required.                                 |
| `args`    | array of strings | Its arguments.                                                  |
| `env`     | object           | Extra environment variables.                                    |
| `cwd`     | string           | Working directory, relative to the project. Defaults to the project root. |

### HTTP servers

| Key       | Type   | Description                                                                     |
| --------- | ------ | ------------------------------------------------------------------------------- |
| `url`     | string | The server's URL. Required.                                                     |
| `type`    | string | `http`, `sse` or `streamable-http`. Optional: a server with a `url` and no `command` is HTTP. |
| `headers` | object | Extra request headers. They are never sent to another origin on a redirect.   |
| `oauth`   | object | Optional OAuth overrides; see below.                                            |

### oauth

Only needed when a server doesn't support dynamic client registration.

| Key            | Type             | Description                                          |
| -------------- | ---------------- | ---------------------------------------------------- |
| `clientId`     | string           | A pre-registered client id.                          |
| `clientSecret` | string           | Its secret. Use `${VAR}` rather than the literal.    |
| `scopes`       | array of strings | Scopes to request.                                   |
| `callbackPort` | number           | The local port for the sign-in redirect. Default `33418`. |

### Environment variables

`${NAME}` in `command`, `args`, `env` values, `cwd`, `url`, `headers` values and `clientSecret` is
replaced with the environment variable's value, or an empty string when it isn't set.
