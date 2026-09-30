---
title: Safety and approvals
description: What anchor allows, what it asks about, what it always denies, and how secrets are kept out of the model's context.
---

Every file read outside the working directory, every write, every process and every MCP tool call
goes through one checkpoint in anchor, the **gate**. For each action, the gate applies the same
three steps: check the policy, ask you if the policy says to, then do it.

The policy has three answers: **allow**, **ask**, or **deny**.

## What runs without asking

- Reading, listing and searching files inside the working directory.
- Shell commands that anchor can prove are read-only and stay inside the directory, such as `ls`,
  `cat`, `grep`, `rg`, `find` (without `-exec` or `-delete`), `wc`, `diff`, and the read-only `git`
  subcommands: `status`, `diff`, `log`, `show`, `blame`, `ls-files`, `rev-parse`, `describe`,
  `shortlog`.
- MCP tools that their server marks read-only.
- Files in installed skill directories.

A command is read-only only if every part of it is. `grep foo *.py | sort` qualifies;
`grep foo *.py > out.txt` does not, because it writes a file.

## What asks first

- Every file write. You see the diff before anything touches disk.
- Every other shell command.
- Reading anything outside the working directory.
- MCP tools that aren't marked read-only.
- Starting an MCP server defined by a project's `.mcp.json`. anchor asks once per server and
  remembers the answer.

Answering "always" to a command or an MCP tool saves it for this directory in
`~/.anchor/approvals.json`. The file is yours, not the project's, so a cloned repository can't
approve anything on your behalf. "Always" for file writes, and for commands that use an interpreter
such as `bash`, `python3` or `node`, lasts only for the session. Saved approvals never lift a denial.

## What is always denied

These are refused even with `--yolo`:

- **Secret and credential files**, for reading and writing: `.env` and `.env.*`, SSH keys (`id_rsa`,
  `id_ed25519`, `id_ecdsa`, `id_dsa`), `.netrc`, `.pgpass`, `.git-credentials`, and
  `~/.aws/credentials`. Paths are checked where they really point, so a symlink to `.env` is
  denied too. Shell commands that name these files, or globs that would match them, are denied.
- **Privilege escalation**: `sudo`, `su`, `doas`, `pkexec`, `run0`.
- **Raw disk operations**: `mkfs`, `fdisk`, `parted`, `wipefs`, `dd` to a device.
- **Deleting a system or home directory**, as in `rm -rf /` or `rm -rf ~`.
- **Downloading and running code**, as in `curl ... | sh` or `bash <(curl ...)`.

## Secret masking

Command output, MCP results and check output are masked before the model sees them. anchor replaces
the values of:

- environment variables whose names look secret (`*_API_KEY`, `*_TOKEN`, `*_SECRET`, `PASSWORD`,
  and so on),
- entries in secret files, including gitignored `.env` files and `~/.aws/credentials`.

:::note[A known limit]
Masking matches exact values, so a deliberately re-encoded secret, such as the output of
`base64 .env`, isn't caught. That's why commands like this ask first: the approval prompt is the
guard.
:::

## --yolo

```sh
anchor --yolo
```

`--yolo` turns every **ask** into **allow**: writes, commands and outside reads run without asking.
Everything in the always-denied list is still denied, and secrets are still masked. anchor prints a
warning at startup when it's on.

In `-p` mode nobody can answer a prompt, so without `--yolo` anything that would ask is refused and
reported. See [Scripting](/anchor/guides/scripting/).

## Windows

anchor reads shell commands with bash's rules, but on Windows it runs them with `cmd.exe`, which
splits and quotes differently. For example, `echo 'x & del /q foo'` is one harmless `echo` to bash,
but cmd runs the `del`.

So on Windows, until the rules understand cmd and PowerShell:

- no shell command is treated as read-only, so every command asks, and
- "always" is never offered for commands.

Hard denials still apply.
