#!/usr/bin/env bash
# Builds the Windows guest agent package that the backup server ships (push installation, setup ZIP, self-update):
#   artifacts/agent/net10/  backdafuckup-agent.exe (self-contained .NET 10, single file) + bdfu-vss.dll
#   artifacts/agent/net48/  backdafuckup-agent.exe (.NET Framework 4.8) + its libraries + bdfu-vss.dll
# Runs on Linux (dotnet SDK + Docker). With SIGNING_CERT_PFX (Base64 of a PFX) and SIGNING_CERT_PASSWORD, the
# executables and DLLs are Authenticode-signed with osslsigncode (scripts/new-signing-cert.sh creates the certificate).
#
# Usage: scripts/build-agent.sh [output directory]   (default: artifacts/agent)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/agent}"
version="${AGENT_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/Backdafuckup.Agent/Backdafuckup.Agent.csproj")}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== native VSS DLL"
"$root/native/bdfu-vss/build.sh" "$work/native"

echo "== agent net10 (self-contained single file)"
"$dotnet" publish "$root/src/Backdafuckup.Agent" -c Release -f net10.0-windows -r win-x64 -p:Version="$version" -o "$work/net10" --nologo -v quiet
echo "== agent net48"
"$dotnet" publish "$root/src/Backdafuckup.Agent" -c Release -f net48 -p:Version="$version" -o "$work/net48" --nologo -v quiet

rm -rf "$out"
for build in net10 net48; do
  mkdir -p "$out/$build"
  # Program files only: no symbols or XML docs; config files of the .NET Framework build stay.
  find "$work/$build" -maxdepth 1 -type f \( -name '*.exe' -o -name '*.dll' -o -name '*.exe.config' \) -exec cp {} "$out/$build/" \;
  cp "$work/native/bdfu-vss.dll" "$out/$build/"
  echo "$version" > "$out/$build/version.txt"
done

if [[ -n "${SIGNING_CERT_PFX:-}" ]]; then
  echo "== signing"
  printf '%s' "$SIGNING_CERT_PFX" | base64 -d > "$work/signing.pfx"
  docker run --rm -u "$(id -u):$(id -g)" -e PASS="${SIGNING_CERT_PASSWORD:-}" -v "$work:/work" -v "$out:/out" backdafuckup/mingw-vss:1 bash -ec '
    command -v osslsigncode >/dev/null || { echo "osslsigncode missing in the image"; exit 1; }
    for f in /out/*/*.exe /out/*/*.dll; do
      osslsigncode sign -pkcs12 /work/signing.pfx -pass "$PASS" -h sha256 -n "backdafuckup agent" \
        -t http://timestamp.digicert.com -in "$f" -out "$f.signed" && mv "$f.signed" "$f"
    done'
fi

echo "Agent $version:"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
