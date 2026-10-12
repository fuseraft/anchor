---
title: Making a theme
description: Write your own color theme as a small JSON file, with hex colors and a named palette, and share it.
---

anchor comes with a few themes (`/theme` lists them). You can add your own by putting a JSON file in
`~/.anchor/themes/`. The file's name is the theme's name, so `ember.json` becomes `ember`. You can
use it anywhere a built-in theme works: `/theme ember`, `--theme ember`, the setup wizard, or
`"theme": "ember"` in the config.

## Start from what you have

The easiest way to begin is to save the colors you're already using:

```
› /theme rust
› /theme save ember
Saved ~/.anchor/themes/ember.json. Change it, then /theme ember to see it.
```

That writes every role out in words you can edit. anchor reads theme files again each time you run
`/theme`, so to see a change, save the file and run `/theme ember` again. You don't need to restart.

## The file

```json
{
  "description": "embers on charcoal",
  "base": "rust",
  "palette": {
    "ember": "#ff6a1f",
    "coal": "#5a3a2e",
    "ash": "#a89080"
  },
  "colors": {
    "accent": "bold ember",
    "border": "coal",
    "muted": "ash",
    "comment": "ash italic"
  }
}
```

Every field is optional:

| Field         | What it does                                                                 |
| ------------- | ---------------------------------------------------------------------------- |
| `description` | Shown next to the name in `/theme` and the setup wizard.                     |
| `base`        | A built-in theme to start from. Roles the file doesn't set keep its colors. Without it, the file starts from `default`. |
| `palette`     | Names for colors, so you can set each one once and use it in several roles.  |
| `colors`      | Colors for [roles](/anchor/reference/config/#theme-and-colors), in color words or palette names. |

A color is one or more words separated by spaces:

- **The terminal's 16 colors:** `red`, `bright-blue`, `gray` and the rest. These follow the
  terminal's own palette.
- **256-colors:** a number from `0` to `255`, such as `208` for orange.
- **Hex colors:** `#ff6a1f`, or the short form `#f62`.
- **Styles:** `bold`, `dim`, `italic`, `underline`.
- **Palette names** from the same file. A palette name can't be a color word, so `red` can't be
  redefined.
- `none` for plain text.

Comments and trailing commas are allowed. If part of a file can't be read, anchor reports it when
it starts, and when you switch to that theme, and uses the rest of the file.

## Hex colors on other terminals

Hex colors need a terminal that shows 24-bit color. anchor uses them as written when the terminal
says it can, through `COLORTERM=truecolor`, or when it recognizes the terminal: Windows Terminal,
iTerm2, WezTerm, VS Code, Ghostty, kitty, Alacritty and mintty. Anywhere else, each hex color is
drawn as the closest 256-color, so a theme still looks close to how it was meant to. If your terminal
can show 24-bit color but anchor doesn't recognize it, set `COLORTERM=truecolor`.

## Sharing a theme

A theme is one self-contained file. To share yours, send the file or post it. To use someone
else's, drop their file into `~/.anchor/themes/`. If a file's name matches a built-in theme, or
isn't lowercase letters, digits, `-` and `_`, anchor reports it and skips it.
