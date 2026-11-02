#!/usr/bin/env bash
# Creates a self-signed code-signing certificate for the agent builds (approach of the erSitzt/inventory project).
#
# Output (outside the repository, default ~/.backdafuckup-signing):
#   signing.pfx.b64   Base64 of the PFX file   -> environment variable / CI secret SIGNING_CERT_PFX
#   signing.password  password of the PFX file -> SIGNING_CERT_PASSWORD
# The public certificate goes to deploy/signing/<thumbprint>.cer and is committed, so administrators can distribute it
# (Trusted Publishers) and Defender indicators can refer to it.
#
# Usage: scripts/new-signing-cert.sh        then: SIGNING_CERT_PFX=$(cat ~/.backdafuckup-signing/signing.pfx.b64) \
#                                                 SIGNING_CERT_PASSWORD=$(cat ~/.backdafuckup-signing/signing.password) scripts/build-agent.sh
set -euo pipefail

subject="${SIGNING_SUBJECT:-/CN=backdafuckup Release Signing}"
years="${SIGNING_YEARS:-5}"
out="${SIGNING_OUT:-$HOME/.backdafuckup-signing}"
public="${SIGNING_PUBLIC:-$(cd "$(dirname "$0")/.." && pwd)/deploy/signing}"

umask 077
mkdir -p "$out" "$public"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days "$((years * 365))" -subj "$subject" \
    -addext 'basicConstraints=critical,CA:FALSE' -addext 'keyUsage=critical,digitalSignature' \
    -addext 'extendedKeyUsage=critical,codeSigning' \
    -keyout "$work/key.pem" -out "$work/cert.pem" 2>/dev/null

password="$(openssl rand -base64 24)"
# Older algorithms in the PFX: osslsigncode, signtool and every Windows version can read it.
legacy=()
if openssl pkcs12 -help 2>&1 | grep -q -- '-legacy'; then legacy=(-legacy); fi
openssl pkcs12 -export "${legacy[@]}" -inkey "$work/key.pem" -in "$work/cert.pem" -name 'backdafuckup Release Signing' \
    -passout "pass:$password" -out "$work/signing.pfx"

base64 -w0 "$work/signing.pfx" > "$out/signing.pfx.b64"
printf '%s' "$password" > "$out/signing.password"
thumbprint="$(openssl x509 -in "$work/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')"
openssl x509 -in "$work/cert.pem" -outform DER -out "$public/$thumbprint.cer"
chmod 644 "$public/$thumbprint.cer"

echo "Certificate: $(openssl x509 -in "$work/cert.pem" -noout -subject | sed 's/^subject=//'), valid until $(openssl x509 -in "$work/cert.pem" -noout -enddate | cut -d= -f2)"
echo "Thumbprint:  $thumbprint"
echo "Public:      $public/$thumbprint.cer   (commit)"
echo "Secret:      $out   (keep safe: whoever has it can sign agent builds)"
