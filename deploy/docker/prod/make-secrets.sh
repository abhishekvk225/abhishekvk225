#!/usr/bin/env bash
# Generates random secrets for the production-like compose stack into deploy/docker/secrets (git-ignored). Files are mode 0644 so
# the unprivileged container users can read the bind mounts; protect the directory itself (mode 0700). Never overwrites existing files.
# Real production: put these values in your vault / orchestrator secrets instead and rotate per docs/runbooks/key-rotation.md.
set -euo pipefail
dir="$(cd "$(dirname "$0")/.." && pwd)/secrets"
mkdir -p "$dir"
chmod 700 "$dir"

put() { # name value
  if [ -e "$dir/$1" ]; then echo "keep   $1 (exists)"; return; fi
  printf '%s' "$2" > "$dir/$1"
  chmod 644 "$dir/$1"
  echo "create $1"
}
rand() { openssl rand -base64 33 | tr -d '/+=\n' | cut -c1-32; }

sa="$(rand)Aa1!"
app="$(rand)Aa1!"
put sa_password "$sa"
put app_db_password "$app"
put seed_admin_password "$(rand)Aa1!"
# Note: a freshly generated password is only used when the file is new; delete ALL files together to regenerate consistently.
# TrustServerCertificate=True is acceptable ONLY on the internal compose network; use Encrypt=True with a trusted certificate elsewhere.
put migrator_connection "Server=sqlserver;Database=NexaVerify;User Id=sa;Password=$(cat "$dir/sa_password");Encrypt=True;TrustServerCertificate=True"
put api_connection "Server=sqlserver;Database=NexaVerify;User Id=nexaverify_app;Password=$(cat "$dir/app_db_password");Encrypt=True;TrustServerCertificate=True"
if [ ! -e "$dir/jwt_signing_key.pem" ]; then
  openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$dir/jwt_signing_key.pem"
  chmod 644 "$dir/jwt_signing_key.pem"
  echo "create jwt_signing_key.pem"
fi
put master_key "$(openssl rand -base64 32)"
echo "Back up master_key and jwt_signing_key.pem in a vault NOW: losing master_key makes biometric data and MFA secrets unrecoverable."
