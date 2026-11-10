#!/usr/bin/env bash
# Builds the server for Windows (self-contained, with the agent package) and installs or updates it on a Windows host
# over SMB and WinRM. Refuses to restart while sessions run (pass --force to bdfu_deploy.py to override).
#
# Usage: scripts/deploy/update-windows.sh [host]        (default: $BDFU_DEPLOY_HOST, e.g. backup01.corp.example)
# Needs: ~/.config/backdafuckup/<host-short>.env with BDFU_DEPLOY_USER / BDFU_DEPLOY_PASSWORD, and the venv:
#   python3 -m venv --system-site-packages scripts/deploy/.venv && scripts/deploy/.venv/bin/pip install smbprotocol pywinrm
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
host="${1:-${BDFU_DEPLOY_HOST:-}}"
if [[ -z "$host" ]]; then
  echo "Name the Windows host to install on: scripts/deploy/update-windows.sh <host>, or set BDFU_DEPLOY_HOST." >&2
  exit 2
fi
env_file="$HOME/.config/backdafuckup/${host%%.*}.env"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

echo "== build (win-x64, self-contained)"
"$dotnet" publish "$root/src/Backdafuckup.Server" -c Release -r win-x64 --self-contained -o "$out/publish" --nologo -v quiet

# The agent package is built every time from the same sources: a package left over from an older build would make the
# agents report "up to date" while the server expects their new commands. SKIP_AGENT_BUILD=1 reuses artifacts/agent.
if [[ "${SKIP_AGENT_BUILD:-0}" != "1" ]]; then
  echo "== build agent package"
  "$root/scripts/build-agent.sh" "$root/artifacts/agent"
fi
if [[ -d "$root/artifacts/agent" ]]; then
  cp -r "$root/artifacts/agent" "$out/publish/agent"
else
  echo "warning: artifacts/agent is missing (scripts/build-agent.sh); the server will offer no agent package." >&2
fi

echo "== install on $host"
"$root/scripts/deploy/.venv/bin/python" -W ignore "$root/scripts/deploy/bdfu_deploy.py" --host "$host" --env "$env_file" install --publish "$out/publish"
