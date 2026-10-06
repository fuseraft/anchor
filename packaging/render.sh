#!/usr/bin/env bash
# Writes the Homebrew formula and Scoop manifest for a release, from its SHA256SUMS.
#   packaging/render.sh <version> <SHA256SUMS> <out-dir>
set -euo pipefail

version="$1" sums="$2" out="$3"
base="https://github.com/fuseraft/anchor/releases/download/v${version}"
desc="A small coding agent for the terminal"

sha() {
  local file="anchor-${version}-$1"
  local hash
  hash="$(awk -v f="$file" '$2 == f || $2 == "*" f { print $1 }' "$sums")"
  [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || { echo "ERROR: no checksum for $file in $sums" >&2; exit 1; }
  echo "$hash"
}

osx_arm64="$(sha osx-arm64.tar.gz)"
osx_x64="$(sha osx-x64.tar.gz)"
linux_arm64="$(sha linux-arm64.tar.gz)"
linux_x64="$(sha linux-x64.tar.gz)"
win_x64="$(sha win-x64.zip)"

mkdir -p "$out"

cat > "$out/anchor.rb" <<RUBY
class Anchor < Formula
  desc "${desc}"
  homepage "https://fuseraft.ai/anchor/"
  version "${version}"
  license "MIT"

  on_macos do
    on_arm do
      url "${base}/anchor-${version}-osx-arm64.tar.gz"
      sha256 "${osx_arm64}"
    end
    on_intel do
      url "${base}/anchor-${version}-osx-x64.tar.gz"
      sha256 "${osx_x64}"
    end
  end

  on_linux do
    on_arm do
      url "${base}/anchor-${version}-linux-arm64.tar.gz"
      sha256 "${linux_arm64}"
    end
    on_intel do
      url "${base}/anchor-${version}-linux-x64.tar.gz"
      sha256 "${linux_x64}"
    end
  end

  def install
    bin.install "anchor"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/anchor --version")
  end
end
RUBY

cat > "$out/anchor.json" <<JSON
{
  "version": "${version}",
  "description": "${desc}",
  "homepage": "https://fuseraft.ai/anchor/",
  "license": "MIT",
  "architecture": {
    "64bit": {
      "url": "${base}/anchor-${version}-win-x64.zip",
      "hash": "${win_x64}"
    }
  },
  "bin": "anchor.exe",
  "checkver": {
    "github": "https://github.com/fuseraft/anchor"
  },
  "autoupdate": {
    "architecture": {
      "64bit": {
        "url": "https://github.com/fuseraft/anchor/releases/download/v\$version/anchor-\$version-win-x64.zip"
      }
    },
    "hash": {
      "url": "\$baseurl/SHA256SUMS"
    }
  }
}
JSON

echo "wrote $out/anchor.rb and $out/anchor.json"
