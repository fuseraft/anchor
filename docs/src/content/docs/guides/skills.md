---
title: Skills
description: Package instructions, scripts and reference files as Agent Skills that the model loads when a task matches.
---

A skill is a folder of instructions the model loads when a task calls for it, in the
[Agent Skills](https://agentskills.io) format. Only each skill's name and description sit in the
system prompt, so you can install many without filling the context.

## Where skills live

- `.agents/skills/<name>/SKILL.md` in the project, or
- `~/.anchor/skills/<name>/SKILL.md` for all your projects.

When both have a skill with the same name, the project's wins. `/skills` lists them.

## Writing a skill

```markdown
---
name: release-notes
description: Write release notes from git history. Use when asked for release notes or a changelog.
---
1. Find the previous tag with `git describe --tags --abbrev=0`.
2. List the commits since then with `git log <tag>..HEAD --oneline`.
3. Group them under Features, Fixes and Other, following `template.md` in this skill's folder.
```

| Field         | Required | Meaning                                                                |
| ------------- | -------- | ---------------------------------------------------------------------- |
| `name`        | yes      | Must match the folder name. Lowercase letters, digits and single hyphens. |
| `description` | yes      | What it does and when to use it. Up to 1,024 characters.              |

Write the description for the model: it decides from this alone whether to load the skill.

## How the model uses a skill

When a task matches a description, the model calls the `skill` tool with the skill's name. That
returns the body of `SKILL.md`, the skill's folder path, and a list of the other files in it.

- The model reads reference files with `read_file`. Files in installed skill folders can be read
  without asking, even when they're outside the working directory.
- It runs scripts with `shell`, through the same approvals as any other command. There's no
  separate script runner that skips the checks.
- Secret files in a skill folder are still denied, and a skill whose `SKILL.md` is a symlink to a
  secret file is skipped.
