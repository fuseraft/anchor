# anchor

A small coding agent for the terminal. One model works in one directory through a handful of
tools, and a deterministic harness decides what it may touch.

- **Safe by default.** Writes, most shell commands and reads outside the directory ask first,
  and a write shows you the diff. Secret files (`.env`, keys, credentials) and dangerous
  commands (`sudo`, `rm -rf /`, `curl | sh`) are always denied, even with `--yolo`. Secret
  values are masked in command output.
- **Long sessions.** Context is kept under the model's window by summarizing older turns,
  trimming old tool output, and as a last resort dropping the oldest steps of a long turn.
  Sessions are saved and can be resumed. `/undo` reverts the last turn's file changes.
- **Extensible.** Sub-agents, Agent Skills (`SKILL.md`) and MCP servers (stdio
  and HTTP, with OAuth sign-in) all go through the same approval checkpoint.
- **Scriptable.** `-p` runs one prompt and prints the answer; `--json` streams events for
  editors and tools.

Status: 0.1.0. Linux and macOS. Models: Claude (native API, prompt caching), and any
OpenAI-compatible API (OpenAI, xAI, local servers).

## Install

Download a release from the Releases page and put `anchor` on your `PATH`, or build it:

```sh
./build.sh            # runs the tests, then publishes bin/anchor for this machine
```

Building needs the .NET 10 SDK. The tests also need `git` and `python3`.

## Configure

Set an API key in the environment. With `ANTHROPIC_API_KEY` or `XAI_API_KEY`, anchor picks a
default model; otherwise, or to choose one, pass `--model` or set it in `~/.anchor/config.json`:

```json
{
  "provider": { "model": "claude-sonnet-5" },
  "mcpServers": {
    "docs": { "type": "http", "url": "https://mcp.example.com/mcp" },
    "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
  }
}
```

For another OpenAI-compatible server, set `provider.endpoint` and `provider.apiKeyEnv`.
`provider.contextWindow` overrides the context size. `ANCHOR_HOME` moves `~/.anchor`.

## Use

```sh
anchor                       # interactive
anchor --resume              # continue the latest session in this directory
anchor -p "why does the build fail?"
git diff | anchor -p "review this change"
anchor -p --yolo "fix the failing test"      # -p can't ask, so it refuses unless --yolo
```

In the REPL: `/help`, `/model`, `/context`, `/compact`, `/undo`, `/sessions`, `/agents`,
`/skills`, `/mcp` (`/mcp login|logout <server>`), `/clear`, `/exit`. `!cmd` runs a command in
your own shell; the model never sees it. Ctrl+C cancels the running turn.

`AGENTS.md` in the directory is added to the system prompt.

### Sub-agents and skills

- **Sub-agents.** The model can hand a task to a sub-agent with its own context. The default one
  only reads. Define your own in `.agents/agents/<name>.md` or `~/.anchor/agents/<name>.md`:

  ```markdown
  ---
  name: reviewer
  description: Reviews a diff for bugs and missing tests.
  tools: [read_file, grep, glob, shell]
  ---
  Review the change you're given. Report problems with file:line.
  ```

- **Skills.** Skills live in `.agents/skills/<name>/SKILL.md` or `~/.anchor/skills/<name>/SKILL.md`.
  Only their descriptions are in the prompt; the model loads a skill when a task matches it.

### MCP

MCP servers come from `mcpServers` in the config or from a project's `.mcp.json`, in Claude
Code's format. `${VAR}` in values is replaced from the environment.

- A project's servers can run any program, so anchor asks once per server before starting it.
- Tools a server marks read-only run without asking; the others ask.
- HTTP servers that need OAuth open your browser to sign in. Tokens are kept in the OS keychain.

### JSON mode

`anchor -p "..." --json` prints one JSON event per line and ends with a `result` line.
`anchor --json` is a line protocol for editors:

- It writes `ready`, streams events, and sends `approval_request` with an `id` when an action
  needs approval.
- It reads `{"type":"user_input","text":"..."}`,
  `{"type":"approval_response","id":"a1","answer":"yes|no|always"}` and `{"type":"cancel"}`.

Exit codes for `-p`: 0 completed, 1 error, 2 usage, 3 stopped by the loop guard, 130 cancelled.

## Develop

`DESIGN.md` explains how anchor is put together and why. `./build.sh` runs the tests and
publishes a binary. `dotnet test` runs just the tests.

## License

MIT
