# anchor

A minimal coding harness for the terminal: one model working in one workspace with a few
tools, plus sub-agents, skills, and MCP. It is built on C# / .NET 10 and `Microsoft.Extensions.AI`.

**Budget:** under 10k lines of source. A new feature has to justify every line it adds.

## Shape

```
src/Anchor/              one project
  Core/Agent.cs          the turn loop
  Core/Gate.cs           the only path to disk, processes and MCP tools: policy, approval, effect
  Core/Policy.cs         pure allow / ask / deny rules
  Core/ShellCommand.cs   conservative bash reader: hard denials, read-only commands
  Core/Compactor.cs      summarize, trim, drop rounds
  Core/SessionLog.cs     append-only JSONL
  Core/Definitions.cs    skills and sub-agent definitions (YAML frontmatter)
  Core/SubAgentRunner.cs fresh agent per task, same gate
  Tools/                 read, list, glob, grep, write, edit, shell, agent, skill, ask_user
  Mcp/                   config and trust, background connections, OAuth, keychain
  Providers/             anthropic (native, cached), openai-compatible
  Cli/                   REPL, full-screen TUI, rendering, approvals, config
tests/Anchor.Tests/
```

## Rules

1. **Own the loop.** anchor drives `IChatClient` directly instead of using
   `UseFunctionInvocation()`. The turn ends on the model's final text. There is no default round
   cap: anchor stops when the model repeats the same call 5 times or fails 3 calls in a row. A `-p`
   caller can set its own with `--max-rounds`.
2. **One gate.** Tools reach the disk and processes only through `Gate`: `ReadPathAsync`,
   `WriteAsync` (the user sees a diff first), and `RunAsync`. Each one runs the same sequence:
   policy, then approval, then the effect. A test fails if a tool touches files or processes
   directly. Paths are checked where they really point, with symlinks resolved.
3. **Safe by default.** Writes and non-read-only shell commands ask first, and the sandbox is
   the launch directory. `--yolo` turns both off.
4. **Hard denials that `--yolo` doesn't lift:**
   - `.env*` and credential files are denied.
   - Catastrophic shell commands are denied, including `sudo` and piping curl to a shell.
   - Secret values are masked in process output: secret-named environment variables and values
     from secret files, including gitignored `.env` files and `~/.aws/credentials`.
   - Known limitation: masking matches exact values, so a deliberately re-encoded value
     (`base64 .env`) is not caught. The approval prompt is the guard for that case.
5. **The core never prints.** It emits events, and a renderer draws them. `--json` is just a
   second renderer.
6. **Messages are typed.** Every message carries its kind (user, assistant, tool, summary), so
   anchor never parses string prefixes to tell them apart.

## Approvals

- "Always" for commands (by program) and MCP tools (by name) is saved per workspace in
  `~/.anchor/approvals.json`, so later sessions and `-p` don't ask again. The file belongs to the
  user, so a project can't grant itself anything.
- Not saved: "always" for file writes, because the diff is the point of asking, and for commands
  that use an interpreter (`bash`, `python3`, `node`, `eval`, ...), which can run anything.
- `/approvals` lists what is saved; `/approvals clear` forgets every "always" answer.

## Sub-agents

- The `agent(task, agent?)` tool starts a fresh `Agent` with its own history in the background and
  returns its id at once. Only its final text reaches the parent, as a note it reads after its
  current step.
- A sub-agent goes through the same gate, policy and approver as the main agent. Its tools are
  filtered at call time, so `--yolo` and safe defaults apply to it the same way.
- The default sub-agent is read-only. Named sub-agents live in `.agents/agents/*.md`
  (frontmatter: `name`, `description`, `tools`, `model`; the body is the prompt).
- Sub-agents can't spawn other sub-agents: depth is limited to 1.
- Events are tagged with the agent's id, so the renderer can nest them. Sub-agent token usage
  counts toward the session total shown by `/context`.
- Up to 4 run at once, tagged with ids like `agent-1` or `reviewer-2`. `agent_status` reports
  what each is doing and `agent_stop` stops one.
- Sub-agents belong to the turn that started them. When the model answers while some still run,
  the turn waits for the next report or for the user to type something (such as asking how
  they're doing), then gives it to the model. Cancelling or ending the turn stops whatever still
  runs, so nothing outlives it.
- Approvals and questions go through `SerialApprover`, one at a time; a sub-agent's approvals are
  labelled with its id. `/agents` lists the agents available.

## Skills

- Skills follow the Agent Skills format: a `SKILL.md` file with `name` and `description` in its
  frontmatter.
- anchor looks for them in `.agents/skills/` and `~/.anchor/skills/`. When the same skill exists
  in both, the project copy wins.
- Only the catalog (name + description) goes into the system prompt. The `skill(name)` tool
  loads the full body.
- Skill resources are read with `read` and scripts run with `shell`, so the gate covers them.
  There is no separate script runner that skips the checks. Files in installed skill
  directories can be read without asking; secret files in them are still denied.
- A skill or agent file that is a symlink to a secret file is skipped. `/skills` lists them.

## MCP

- anchor uses the official `ModelContextProtocol` SDK and supports stdio and HTTP servers.
- Servers are configured under `mcpServers` in `~/.anchor/config.json` or in the project's
  `.mcp.json`. The shape matches Claude Code's: `command`/`args`/`env` for stdio,
  `url`/`headers` for HTTP. Headers expand `${ENV_VAR}`.
- A project `.mcp.json` can launch arbitrary commands, so anchor asks once per server before it
  first starts one. It remembers the answer for that exact config, and asks again if the config
  changes. On a name clash, the user's own config wins.
- MCP tools appear as `mcp__<server>__<tool>`. They run through the gate like every other tool:
  - A tool marked `readOnlyHint` runs without asking.
  - Every other MCP tool asks first, unless `--yolo` is on.
  - Output is masked for secrets.
- For HTTP servers, anchor follows redirects itself, so headers are never sent to another
  origin.
- OAuth 2.1 works out of the box, using the SDK's built-in support:
  - Dynamic client registration and PKCE, so a server needs no client setup.
  - Sign-in opens the browser, and a loopback listener catches the redirect.
  - A server that returns 401 starts the sign-in automatically.
  - `clientId`, `clientSecret`, and `scopes` are optional overrides.
- Tokens live in the OS keychain (Keychain, `secret-tool`, Credential Manager), never in a
  plaintext file. Without a keychain, tokens are kept in memory only.
- API keys saved by `anchor setup` also live in the keychain, under the name of the variable they
  stand in for. The variable wins when it's set.
- `/mcp login <server>` and `/mcp logout <server>` manage sign-ins.
- The connect timeout stretches to 5 minutes while sign-in is pending.
- The browser launcher can be swapped out in tests.
- Servers connect in the background, one at a time, so two sign-ins never compete for the
  callback port. A server that fails to start shows a warning instead of blocking the REPL.
  `/mcp` shows each server's state.

## Context

- Context size comes from the provider's real reported usage. Anthropic's counts are normalized
  so that input includes cached tokens, the same as OpenAI's. When a provider reports nothing,
  anchor estimates at chars / 4.
- At 80% of the window, anchor summarizes older turns. It checks after each turn and between
  tool rounds.
- Whole recent turns are kept verbatim, up to 20% of the window. The last turn is always kept,
  so a call is never separated from its result.
- The summary is rejected unless it is non-empty and smaller than what it replaces.
- If summarizing isn't enough, as in one long turn, anchor trims old large tool content, oldest
  first, down to 60% of the window, measured by the provider's real token count:
  - A large result becomes a placeholder that names the tool and its argument, keeps a
    300-character preview, and says to re-run the tool with a narrower range.
  - A large call argument, such as `write_file` content, becomes a character count. The file on
    disk already has the content.
- If trimming still leaves the context over the 80% trigger, as with hundreds of small rounds,
  anchor drops the oldest rounds of the current turn. It cuts only where a call and its result
  stay together, and puts a note right after the user's request listing what those rounds did
  (tools and arguments, plus the model's last message). A later drop merges into the same note.
- The latest round is never trimmed or dropped, because the model hasn't seen its results yet.
- If the summary request itself is too big, it retries with shorter tool excerpts.
- A "context too long" error from the provider triggers one round of summarizing and trimming,
  then a retry.
- The window comes from `provider.contextWindow`, or a per-family default.
- `AGENTS.md` is loaded into the system prompt.

## Sessions

- Each session is an append-only JSONL file in `~/.anchor/sessions/`, readable only by the user.
- New messages are appended. A compaction or `/clear` writes a `reset` record, and replaying the
  file rebuilds the current history.
- `--resume` continues the latest session for this directory, or a given id or id prefix. It
  re-shows the last three turns.
- `/undo` reverts the file-tool changes of the most recent turn that made any, and tells the
  model it did. It leaves alone any file that changed after anchor wrote it. Shell-made changes
  are not tracked.

## Surface

```
anchor [--yolo] [--allow rule]... [--resume [id]] [--model m] [-p "prompt"] [--until check]
       [--max-rounds n] [--timeout s] [--json] [--plain]
/help /model /context /until /compact /approvals /undo /sessions /clear /agents /skills /mcp /exit
!cmd runs in your shell, outside the model's history
```

The REPL is full screen (`Cli/Tui.cs`, on Terminal.Gui and its Editor view for the prompt). `Repl`
is the controller and talks to an `IReplScreen`; `--plain`, or a stdin or stdout that isn't a
terminal, gets the line-based `LineScreen` instead. The Renderer is unchanged: in the TUI it writes
its ANSI text into a `Transcript`, which reads the styles back, so commands and events look the
same in both. Approvals, `ask_user` and setup questions take the prompt's place and ignore keys
until typing stops, so typed text can't answer them.

The user can type while a turn runs. A sent message is queued on the agent and added as a user
message after the current step's tool results, so a call is never separated from its result;
whatever the turn didn't read becomes the next turn. Commands wait for the turn to end.

Config lives in `~/.anchor/config.json`. Sessions are stored in `~/.anchor/sessions/`.

## Headless

- `-p` runs one turn and exits. Only the final answer goes to stdout; progress goes to stderr.
- Piped stdin is appended to the prompt, or is the prompt when there's no argument. A stdin that
  is a socket or a device is ignored when a prompt argument is given, because it may never close.
- Nobody can answer approvals in `-p`, so anything that would ask is refused and reported, unless
  `--yolo` is on. Project MCP servers that were never approved interactively are skipped.
- `--allow <rule>` is "always" given up front, for the session only: a program, an MCP tool, or
  `edits`. It is never saved, and it never lifts a denial.
- `--max-rounds` caps the main agent's model requests over the whole run; `--timeout` cancels the
  run. Sub-agents are bounded by their own loop guard, not by `--max-rounds`.
- Exit codes: 0 completed, 1 error, 2 usage, 3 stopped by the loop guard, 4 the `--until` check never
  passed, 5 reached `--max-rounds`, 124 reached `--timeout` (as with `timeout(1)`), 130 cancelled.
- `--json` writes every event as one JSON object per line. With `-p` it ends with a `result`
  line: status, answer, `--until` result, files the file tools wrote, session id and usage. Without
  `-p` it is a protocol for editors:
  - Requests on stdin: `user_input`, `approval_response`, `cancel`.
  - It announces `ready` whenever it can take input.
  - Approvals arrive as `approval_request` events with an id.
- Sessions from both modes can be resumed.

## Checks

- `--until <check>` (with `-p`) and `/until <check>` (REPL) run the user's command after each
  turn. A failing check sends its output back, masked and cut to the tail, as the next message.
- Done is decided by the exit code, never by a model. The loop ends when the check passes, when
  a round writes no files (the check would fail the same way; this also covers the model asking
  the user something), or after 5 rounds.
- The user wrote the check, as with `!cmd`, so it runs through `Gate.CheckAsync` with no policy
  or approval. A REPL message and all its rounds are one turn for `/undo`.

## Not in anchor

Multi-agent orchestration (graphs, routing, validators), plans, memory, telemetry, and a
plugin registry. Any of these can be added later as a tool behind the gate.

## Tests

- A scripted fake `IChatClient`; tests never call a live LLM.
- Security tests use an approver that always says yes.
- Each milestone is live-checked against a real model.

## Milestones

1. Loop, read tools, providers, and REPL.
2. Gate, write/edit/shell tools, and approvals.
3. Sessions, resume, and compaction.
4. Sub-agents, skills, and MCP.
5. `-p` and `--json` modes, then the v0.1.0 release (Linux and macOS; Windows waits for
   shell safety rules that understand cmd and PowerShell; until then, Windows builds ask before
   every shell command and offer no "always" for them).
6. Windows builds and install scripts, `--until`, parallel sub-agents, and a documentation
   site; then the v0.2.0 release.
7. A full-screen TUI that takes input while a turn runs, background sub-agents shown live,
   `ask_user`, `anchor setup`, named providers, saved "always" approvals, and `--allow`,
   `--max-rounds` and `--timeout` for `-p`; then the v0.3.0 release.
