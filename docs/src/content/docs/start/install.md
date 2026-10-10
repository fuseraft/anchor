---
title: Install
description: Install anchor on Linux, macOS or Windows, or build it from source.
---

anchor ships as a single self-contained binary. You don't need .NET installed to run it.

## Linux and macOS

```sh
curl -fsSL https://raw.githubusercontent.com/fuseraft/anchor/main/install.sh | bash
```

The script downloads the latest release for your platform and installs `anchor` to
`~/.local/bin`. Pass `--system` to install to `/usr/local/bin` instead (it uses `sudo` if needed):

```sh
curl -fsSL https://raw.githubusercontent.com/fuseraft/anchor/main/install.sh | bash -s -- --system
```

If `~/.local/bin` isn't on your `PATH`, the script prints the line to add to your shell's rc file.

Both install scripts check the download against the release's `SHA256SUMS` and refuse to install
an archive that doesn't match.

anchor installed this way updates itself. Once a day, a session checks for a new release and
downloads it, checked against the same `SHA256SUMS`, and the next time anchor starts it runs the new
version. `"autoUpdate": false` in the [config](/anchor/reference/config/#autoupdate) turns this off;
to update by hand, run the install command again. A copy installed by Homebrew, Scoop or winget
isn't touched: anchor tells you a new release is out and leaves the update to the package manager.

## Windows

```powershell
irm https://raw.githubusercontent.com/fuseraft/anchor/main/install.ps1 | iex
```

This installs `anchor.exe` to `%LOCALAPPDATA%\anchor\bin` and adds that folder to your user
`PATH`. Open a new terminal afterwards. On ARM64, the x64 build runs under emulation.

:::caution[Windows asks before every shell command]
anchor's shell safety rules read commands as bash, but Windows runs them with `cmd.exe`, which
splits and quotes differently. Until the rules understand cmd and PowerShell, Windows builds never
treat a command as read-only and never offer "always allow" for commands. See
[Safety and approvals](/anchor/guides/safety/#windows).
:::

## With a package manager

[Homebrew](https://brew.sh) on macOS and Linux:

```sh
brew install fuseraft/tap/anchor
```

[Scoop](https://scoop.sh) on Windows:

```powershell
scoop bucket add fuseraft https://github.com/fuseraft/scoop-bucket
scoop install anchor
```

`brew upgrade anchor` and `scoop update anchor` update it.

## From a release

Download an archive for your platform from the
[Releases page](https://github.com/fuseraft/anchor/releases), extract it, and put `anchor` on your
`PATH`. Builds are published for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` and `win-x64`.

Each release lists the archives' checksums in `SHA256SUMS`, and carries a signed build provenance
attestation that ties every archive to the GitHub Actions run that built it. With the
[GitHub CLI](https://cli.github.com/), check one with:

```sh
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify anchor-<version>-linux-x64.tar.gz --repo fuseraft/anchor
```

## From source

Building needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The tests also need `git`
and `python3`.

```sh
git clone https://github.com/fuseraft/anchor.git
cd anchor
./build.sh      # runs the tests, then publishes bin/anchor for this machine
```

## Check it works

```sh
anchor --version
```

Next, [set an API key and start a session](/anchor/start/quickstart/).
