#!/usr/bin/env bash
# Runs ON the Docker host (called over SSH by .github/workflows/_deploy.yml, or by an operator following docs/runbooks/deploy.md).
# Deploys one release tag of the production-like compose stack:
#   pre-deploy database backup -> pull images -> run the migrator (the only schema writer) -> start services -> wait for health -> record tag.
# The application never migrates itself; this script is the only place a migration is triggered.
#
# Host layout ($DEPLOY_DIR, default /opt/nexaverify), prepared once by an operator:
#   .env.prod                 non-secret settings (see deploy/docker/.env.prod.example); NEXAVERIFY_REGISTRY must be set
#   secrets/                  secret files (never copied by the pipeline; see deploy/docker/prod/make-secrets.sh and docs/runbooks/key-rotation.md)
#   docker-compose.prod.yml   copied here per release together with prod/Caddyfile
#   current-tag, previous-tag written by this script (used by --rollback)
#
#   remote-deploy.sh --tag v1.2.3 [--no-migrate] [--backup-confirmed]
#   remote-deploy.sh --rollback [--expect-tag v1.2.2]
set -euo pipefail

deploy_dir="${DEPLOY_DIR:-/opt/nexaverify}"
tag=""
migrate="yes"
rollback="no"
backup_confirmed="no"
expect_tag=""

while [ $# -gt 0 ]; do
  case "$1" in
    --tag) tag="${2:?--tag needs a value}"; shift 2 ;;
    --no-migrate) migrate="no"; shift ;;
    --rollback) rollback="yes"; shift ;;
    --backup-confirmed) backup_confirmed="yes"; shift ;;
    --expect-tag) expect_tag="${2:?--expect-tag needs a value}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done

cd "$deploy_dir"
[ -f .env.prod ] || { echo ".env.prod missing in $deploy_dir" >&2; exit 66; }
[ -d secrets ] || { echo "secrets/ missing in $deploy_dir (see deploy/docker/prod/make-secrets.sh)" >&2; exit 66; }

if [ "$rollback" = "yes" ]; then
  if [ -s pending-tag ]; then
    # The last deployment never finished: the last GOOD version is current-tag.
    [ -s current-tag ] || { echo "no current-tag recorded; nothing to roll back to" >&2; exit 65; }
    tag="$(cat current-tag)"
  else
    [ -s previous-tag ] || { echo "no previous-tag recorded; nothing to roll back to" >&2; exit 65; }
    tag="$(cat previous-tag)"
  fi
  migrate="no"
  if [ -n "$expect_tag" ] && [ "$expect_tag" != "$tag" ]; then
    echo "rollback target on this host is $tag but the operator expected $expect_tag; aborting" >&2
    exit 65
  fi
  echo "ROLLBACK to $tag (containers only; the database is NOT rolled back, see docs/runbooks/rollback.md)"
fi
[ -n "$tag" ] || { echo "--tag is required" >&2; exit 64; }
case "$tag" in *[!A-Za-z0-9._-]*|"") echo "invalid tag '$tag'" >&2; exit 64 ;; esac

export NEXAVERIFY_TAG="$tag"
compose=(docker compose -f docker-compose.prod.yml --env-file .env.prod)
current="$( [ -s current-tag ] && cat current-tag || true )"
[ "$rollback" = "yes" ] || printf '%s\n' "$tag" > pending-tag

echo "== Pulling images for $tag"
"${compose[@]}" pull --quiet api web migrator

if [ "$migrate" = "yes" ]; then
  echo "== Pre-migration backup"
  if "${compose[@]}" ps --status running --services 2>/dev/null | grep -qx sqlserver; then
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    "${compose[@]}" exec -T sqlserver bash -c "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"\$(cat /run/secrets/sa_password)\" -b -Q \"BACKUP DATABASE [NexaVerify] TO DISK = N'/var/opt/mssql/backup/NexaVerify-pre-${tag}-${stamp}.bak' WITH COMPRESSION, CHECKSUM, INIT\"" \
      || { echo "backup failed: refusing to migrate" >&2; exit 70; }
    echo "backup written to the sqlbackup volume: NexaVerify-pre-${tag}-${stamp}.bak (copy it off-host, docs/runbooks/db-migration.md)"
  elif [ "$backup_confirmed" = "yes" ]; then
    echo "external database: operator confirmed a fresh backup (--backup-confirmed)"
  else
    echo "database is not part of this compose project: take a backup, then re-run with --backup-confirmed" >&2
    exit 70
  fi

  echo "== Migrating (idempotent; schema, guards, app login)"
  "${compose[@]}" up -d sqlserver || true
  "${compose[@]}" run --rm migrator
fi

echo "== Starting services"
"${compose[@]}" up -d --remove-orphans api web caddy

echo "== Waiting for health"
for service in api web; do
  for _ in $(seq 1 60); do
    state="$("${compose[@]}" ps --format '{{.Health}}' "$service" 2>/dev/null | head -n1)"
    [ "$state" = "healthy" ] && break
    sleep 5
  done
  if [ "${state:-}" != "healthy" ]; then
    echo "service $service did not become healthy" >&2
    "${compose[@]}" logs --tail 80 "$service" >&2 || true
    exit 75
  fi
done

if [ "$rollback" = "no" ] && [ -n "$current" ] && [ "$current" != "$tag" ]; then
  printf '%s\n' "$current" > previous-tag
fi
printf '%s\n' "$tag" > current-tag
rm -f pending-tag
echo "Deployed $tag (previous: ${current:-none})"
