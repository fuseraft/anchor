---
title: MCP servers
description: Connect stdio and HTTP MCP servers, approve project servers, and sign in with OAuth.
---

anchor connects to [Model Context Protocol](https://modelcontextprotocol.io) servers and offers their
tools to the model. It supports stdio servers (a local program) and HTTP servers.

## Configuring servers

Add servers under `mcpServers` in `~/.anchor/config.json`, or in a `.mcp.json` file at the root of a
project. The format is the same as Claude Code's, so existing `.mcp.json` files work as they are.

```json
{
  "mcpServers": {
    "files": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."]
    },
    "docs": {
      "type": "http",
      "url": "https://mcp.example.com/mcp",
      "headers": { "Authorization": "Bearer ${DOCS_TOKEN}" }
    }
  }
}
```

- A server with `command` is a stdio server. `args`, `env` and `cwd` are optional; `cwd` defaults to
  the working directory.
- A server with `url` and no `command`, or with `type` set to `http`, `sse` or `streamable-http`,
  is an HTTP server. `headers` is optional.
- `${NAME}` in commands, arguments, environment values, URLs and headers is replaced with that
  environment variable (empty if unset). Keep tokens in the environment, not in the file.

If a project's `.mcp.json` and your own config define the same server name, yours wins.

## Project servers ask first

A cloned repository's `.mcp.json` can start any program on your machine. So the first time anchor
sees a project server, it asks:

```
  ? This project's .mcp.json wants to start MCP server 'files': npx -y @modelcontextprotocol/server-filesystem .
  Allow? [y]es [n]o › 
```

anchor remembers the answer for that exact configuration in `~/.anchor/mcp-trust.json`, and asks
again if the configuration changes. In `-p` and `--json` modes, project servers you've never approved
are skipped with a warning.

## Tools and approvals

MCP tools appear to the model as `mcp__<server>__<tool>`. They go through the gate like anchor's own
tools:

- A tool its server marks read-only (`readOnlyHint`) runs without asking.
- Every other tool asks first, unless `--yolo` is on. "Always" allows all calls to that one tool.
- Results are masked for secrets.

## Signing in with OAuth

HTTP servers that need OAuth work without setup. When a server answers 401, anchor opens your browser
to sign in and catches the redirect on a local port. It uses dynamic client registration and PKCE,
so the server needs no client configured in advance.

Tokens are stored in your OS keychain: the macOS Keychain, `secret-tool` (libsecret) on Linux, or
Windows Credential Manager. They are never written to a plain file. Without a keychain, tokens are
kept in memory for the session only.

| Command                 | Effect                                        |
| ----------------------- | --------------------------------------------- |
| `/mcp`                  | Each server's state and number of tools.      |
| `/mcp login <server>`   | Sign in to a server now.                      |
| `/mcp logout <server>`  | Forget the server's tokens.                   |

If a server needs a pre-registered client, set it under `oauth`:

```json
{
  "mcpServers": {
    "corp": {
      "url": "https://mcp.corp.example/mcp",
      "oauth": {
        "clientId": "anchor-cli",
        "clientSecret": "${CORP_MCP_SECRET}",
        "scopes": ["read", "write"],
        "callbackPort": 33418
      }
    }
  }
}
```

## Startup

Servers connect in the background, one at a time, so the prompt is ready right away and two
sign-ins never compete for the callback port. A server that fails to start shows a warning instead
of blocking the session. A stdio server gets 60 seconds to start; an HTTP server gets 5 minutes,
so there's time to sign in. With `-p --timeout`, startup as a whole gets that limit instead.
