#!/usr/bin/env bash
# Build, test, and publish a self-contained single-file binary to ./bin.
set -euo pipefail
cd "$(dirname "$0")"

os=$([ "$(uname -s)" = Darwin ] && echo osx || echo linux)
arch=$(case "$(uname -m)" in x86_64|amd64) echo x64;; aarch64|arm64) echo arm64;; *) uname -m;; esac)
rid="${1:-$os-$arch}"

# Global commit signing can hang git in tests.
GIT_CONFIG_GLOBAL=/dev/null dotnet test
dotnet publish src/Anchor -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -o bin
echo "built bin/anchor ($rid)"
