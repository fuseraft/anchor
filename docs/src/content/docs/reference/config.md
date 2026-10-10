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
  // Named servers; use their models as "work/<model>".
  "providers": {
    "work": { "endpoint": "https://litellm.example.com/v1", "apiKeyEnv": "LITELLM_API_KEY" }
  },
  // MCP servers available in every project.
  "mcpServers": {
    "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
  },
  // Colors.
  "theme": "default",
  "colors": { "accent": "bold magenta" },
  // Ring the terminal bell when anchor needs you.
  "bell": true,
  // Download new releases and install them the next time anchor starts.
  "autoUpdate": true
}
```

## provider

| Key             | Type   | Description                                                                                   |
| --------------- | ------ | --------------------------------------------------------------------------------------------- |
| `model`         | string | The model name, or `<provider>/<model>` for a [named provider](#providers). `--model` overrides it. |
| `name`          | string | `anthropic` or `openai`. Inferred from the model name when omitted; `openai` for unknown names. |
| `endpoint`      | string | The API base URL, for OpenAI-compatible servers other than OpenAI and xAI.                   |
| `apiKeyEnv`     | string | The environment variable that holds the API key. When it's unset, a key saved for it by `anchor setup` is read from the OS keychain or `~/.anchor/credentials`. |
| `contextWindow` | number | The model's context window in tokens.                                                        |

A model name anchor doesn't recognize needs both `endpoint` and `apiKeyEnv`. Recognized prefixes and
their defaults:

| Prefix                   | `name`      | `endpoint`                | `apiKeyEnv`         | `contextWindow` |
| ------------------------ | ----------- | ------------------------- | ------------------- | --------------- |
| `claude-`                | `anthropic` | Anthropic's API           | `ANTHROPIC_API_KEY` | 200,000         |
| `grok-`                  | `openai`    | `https://api.x.ai/v1`     | `XAI_API_KEY`       | 256,000 for `grok-4*`, else 128,000 |
| `gpt-`, `o1`, `o3`, `o4` | `openai`    | OpenAI's API              | `OPENAI_API_KEY`    | 128,000         |

Any other model defaults to a 128,000-token window.

## providers

An object mapping a name to an API server, such as a LiteLLM proxy. A model written `<name>/<model>`
is sent to that server as `<model>` (everything after the first `/`). This works in
`provider.model`, `--model`, `/model` and a sub-agent's `model:`. `provider.name`, `endpoint` and
`apiKeyEnv` don't apply to these models; `provider.contextWindow` still overrides the window.

| Key             | Type   | Description                                                                                 |
| --------------- | ------ | ------------------------------------------------------------------------------------------- |
| `endpoint`      | string | The API base URL. Required.                                                                 |
| `type`          | string | `openai` (chat completions, the default) or `anthropic` (messages).                         |
| `apiKeyEnv`     | string | The environment variable that holds the API key, or whose key `anchor setup` saved. Omit it if the server needs no key. |
| `headers`       | object | Extra request headers. `${NAME}` in a value is replaced with the environment variable.     |
| `contextWindow` | number | The context window for this server's models. Defaults by model name: a name containing `claude-` gets 200,000 and one containing `grok-4` gets 256,000, so Bedrock ids like `anthropic.claude-sonnet-5` work. |

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

## theme and colors

`theme` names a built-in theme, and `colors` changes single roles on top of it. The
[setup wizard](/anchor/start/configuration/#the-setup-wizard) saves `theme` for you. `--theme` and
`/theme` override it. `NO_COLOR` still turns color off entirely.

| Theme     | Description                                                                 |
| --------- | --------------------------------------------------------------------------- |
| `default` | anchor's usual colors.                                                      |
| `bright`  | The bright variants and no dim text, for dark terminals whose normal colors are too dark. |
| `light`   | For light backgrounds: no yellow or bright colors, which wash out on white. Warnings are magenta. |
| `mono`    | No colors, only bold, dim and underline.                                    |

Themes use the terminal's own 16 colors, so they follow its palette and read on light and dark
backgrounds alike; `light` is for palettes where yellow is hard to read on white. A color in `colors` is one or more of these words, separated by spaces:
`black`, `red`, `green`, `yellow`, `blue`, `magenta`, `cyan`, `white`, `gray`, `bright-red`,
`bright-green`, `bright-yellow`, `bright-blue`, `bright-magenta`, `bright-cyan`, `bright-white`,
`bold`, `dim`, `italic`, `underline`, or `none` for plain text.

```json
{
  "theme": "bright",
  "colors": { "accent": "bold magenta", "comment": "gray italic" }
}
```

| Role       | Used for                                                          |
| ---------- | ----------------------------------------------------------------- |
| `accent`   | The prompt caret, the title, the spinner and the picker's choice. |
| `tool`     | A tool call's name.                                               |
| `agent`    | A sub-agent's tag.                                                |
| `success`  | Passed checks, added lines in a diff.                             |
| `warning`  | Notices, approval questions.                                      |
| `error`    | Errors, removed lines in a diff.                                  |
| `muted`    | Tool arguments, token counts, hints, table and quote borders.     |
| `border`   | The line above the prompt and the status line.                    |
| `heading`  | Markdown headings.                                                |
| `link`     | Markdown links.                                                   |
| `code`     | Inline code, and code blocks in a language anchor can't highlight. |
| `comment`, `string`, `constant`, `keyword`, `function`, `type` | Syntax highlighting in code blocks. |

An unknown theme, role or color word is reported when anchor starts, and the rest still applies.

## bell

The full-screen REPL rings the terminal bell when a turn ends or an approval or question is
waiting, if you haven't pressed a key or scrolled for 10 seconds. If you're watching, it stays
quiet. How a bell shows up is up to the terminal: a sound, a flash, or a mark on the tab or
window. `"bell": false` turns it off.

## autoUpdate

An interactive session checks for a new release once a day. If anchor was installed with an
install script or from a release archive, it downloads the release into `~/.anchor/update`,
checks it against the release's `SHA256SUMS`, and installs it the next time anchor starts. If
Homebrew, Scoop or winget installed it, or anchor can't write to the folder it's in, anchor only
tells you the release is out. `-p` and `--json` never check. `"autoUpdate": false` turns it off.
