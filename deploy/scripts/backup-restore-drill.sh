#!/usr/bin/env bash
# NexaVerify backup/restore drill (docs/runbooks/backup-restore.md).
#
# 1. takes a COPY_ONLY, checksummed, compressed full backup of the source database,
# 2. verifies the backup (RESTORE VERIFYONLY ... WITH CHECKSUM),
# 3. restores it under a different name (never over the source),
# 4. runs DBCC CHECKDB on the restored copy and compares table row counts with the source,
# 5. runs the credit-ledger verification against the restored copy (hash chains, balances, signed checkpoints),
# 6. drops the restored copy (unless NV_KEEP_RESTORED=1) and prints how long each phase took (your measured RTO).
#
# Needs: sqlcmd (or a wrapper such as `docker exec -i <container> /opt/mssql-tools18/bin/sqlcmd -C`), and for step 5 the Migrator
# (`dotnet` + this repository, or a published NexaVerify.Migrator) with the SAME Encryption__MasterKeyBase64 as the API: the ledger
# checkpoints are keyed with it, so a restore that lost the key is a failed restore.
#
# Configuration (environment; secrets only through the environment, never as arguments):
#   NV_DRILL_SERVER          SQL Server host[,port]                         (required)
#   NV_SOURCE_DB             database to back up                            (default NexaVerify)
#   SQLCMDUSER / SQLCMDPASSWORD   login with BACKUP/RESTORE/CREATE DATABASE rights (sqlcmd reads these itself)
#   SQLCMD                   sqlcmd command line                            (default "sqlcmd -C")
#   NV_BACKUP_FILE           server-side path of the .bak                    (default <instance backup dir>/<db>_drill_<utc>.bak)
#   NV_RESTORE_DB            name of the restored copy                       (default <db>_drill_<utc>)
#   NV_VERIFY_CMD            ledger verification command                     (default: dotnet run --project src/Migrator -c Release -- verify-ledger)
#   NV_RESTORE_CONNECTION    connection string of the restored copy for the verifier
#                            (default: built from NV_VERIFY_SERVER/NV_DRILL_SERVER, SQLCMDUSER, SQLCMDPASSWORD, TrustServerCertificate=True)
#   Encryption__MasterKeyBase64   the production master key (required for the ledger check, unless NV_SKIP_LEDGER_CHECK=1)
#   NV_VERIFY_SERVER         server address as seen from where the verifier runs, when it differs from NV_DRILL_SERVER (default: same)
#   NV_KEEP_RESTORED=1       keep the restored copy for inspection
#   NV_STRICT_COUNTS=1       fail (not just warn) when row counts differ - use on a quiesced or replica source
#   NV_SKIP_LEDGER_CHECK=1   skip step 5 (the drill is then NOT a complete drill)
#
# Exit codes: 0 drill passed, 1 a step failed, 2 bad configuration, 3 the restored ledger failed verification.
set -euo pipefail

: "${NV_DRILL_SERVER:?set NV_DRILL_SERVER (host[,port])}"
SOURCE_DB="${NV_SOURCE_DB:-NexaVerify}"
STAMP="$(date -u +%Y%m%d%H%M%S)"
RESTORE_DB="${NV_RESTORE_DB:-${SOURCE_DB}_drill_${STAMP}}"
IDENT='^[A-Za-z_][A-Za-z0-9_]{0,100}$'
[[ "$SOURCE_DB" =~ $IDENT && "$RESTORE_DB" =~ $IDENT ]] || { echo "Database names must be plain identifiers (letters, digits, underscore)." >&2; exit 2; }
[[ "$RESTORE_DB" != "$SOURCE_DB" ]] || { echo "The restored copy must not have the source's name." >&2; exit 2; }

read -r -a SQLCMD_CMD <<< "${SQLCMD:-sqlcmd -C}"

q() { "${SQLCMD_CMD[@]}" -S "$NV_DRILL_SERVER" -b -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r'; }
now() { date +%s; }
log() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*"; }
quote_sql() { printf '%s' "$1" | sed "s/'/''/g"; }

cleanup() {
  if [[ "${NV_KEEP_RESTORED:-0}" != "1" ]]; then
    q "IF DB_ID(N'$RESTORE_DB') IS NOT NULL BEGIN ALTER DATABASE [$RESTORE_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$RESTORE_DB]; END" >/dev/null 2>&1 || true
  else
    log "Keeping restored copy [$RESTORE_DB] (NV_KEEP_RESTORED=1)."
  fi
}
trap cleanup EXIT

BACKUP_FILE="${NV_BACKUP_FILE:-}"
if [[ -z "$BACKUP_FILE" ]]; then
  BACKUP_DIR="$(q "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(400))")"
  BACKUP_FILE="${BACKUP_DIR%/}/${SOURCE_DB}_drill_${STAMP}.bak"
fi
BACKUP_SQL="$(quote_sql "$BACKUP_FILE")"

log "Source [$SOURCE_DB] on $NV_DRILL_SERVER -> backup $BACKUP_FILE -> restore as [$RESTORE_DB]"

# ---- 1. backup -------------------------------------------------------------------------------------------------------
t0=$(now)
q "BACKUP DATABASE [$SOURCE_DB] TO DISK = N'$BACKUP_SQL' WITH COPY_ONLY, CHECKSUM, COMPRESSION, INIT, STATS = 25" >/dev/null
t1=$(now)
log "1/5 backup done in $((t1 - t0))s"

# ---- 2. verify the backup file ----------------------------------------------------------------------------------------
q "RESTORE VERIFYONLY FROM DISK = N'$BACKUP_SQL' WITH CHECKSUM" >/dev/null
log "2/5 backup verified (checksums good)"

# ---- 3. restore under a new name ----------------------------------------------------------------------------------------
DATA_DIR="$(q "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))")"
LOG_DIR="$(q "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(400))")"
MOVES=""
while IFS='|' read -r logical type; do
  [[ -n "$logical" ]] || continue
  if [[ "$type" == "L" ]]; then target="${LOG_DIR%/}/${RESTORE_DB}_${logical}.ldf"; else target="${DATA_DIR%/}/${RESTORE_DB}_${logical}.mdf"; fi
  MOVES+=", MOVE N'$(quote_sql "$logical")' TO N'$(quote_sql "$target")'"
done < <("${SQLCMD_CMD[@]}" -S "$NV_DRILL_SERVER" -b -h -1 -W -s'|' -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$BACKUP_SQL'" | tr -d '\r' | awk -F'|' 'NF>3 {print $1"|"$3}')
[[ -n "$MOVES" ]] || { echo "Could not read the file list of the backup." >&2; exit 1; }
t2=$(now)
q "RESTORE DATABASE [$RESTORE_DB] FROM DISK = N'$BACKUP_SQL' WITH CHECKSUM, RECOVERY, REPLACE$MOVES" >/dev/null
t3=$(now)
log "3/5 restored as [$RESTORE_DB] in $((t3 - t2))s"

# ---- 4. integrity and row counts ----------------------------------------------------------------------------------------
q "DBCC CHECKDB (N'$RESTORE_DB') WITH NO_INFOMSGS, ALL_ERRORMSGS" >/dev/null
log "4/5 DBCC CHECKDB clean"
# sys.partitions is metadata, so the answer does not depend on row-level security (a plain COUNT(*) as sysadmin sees no tenant rows).
COUNTS_SQL="SELECT s.name + N'.' + t.name + N'=' + CAST(SUM(p.rows) AS nvarchar(30)) FROM [%s].sys.partitions p JOIN [%s].sys.tables t ON t.object_id = p.object_id JOIN [%s].sys.schemas s ON s.schema_id = t.schema_id WHERE p.index_id IN (0, 1) GROUP BY s.name, t.name ORDER BY 1"
# shellcheck disable=SC2059
SOURCE_COUNTS="$(q "$(printf "$COUNTS_SQL" "$SOURCE_DB" "$SOURCE_DB" "$SOURCE_DB")")"
# shellcheck disable=SC2059
RESTORED_COUNTS="$(q "$(printf "$COUNTS_SQL" "$RESTORE_DB" "$RESTORE_DB" "$RESTORE_DB")")"
if [[ "$SOURCE_COUNTS" == "$RESTORED_COUNTS" ]]; then
  log "    row counts identical to the source ($(wc -l <<< "$RESTORED_COUNTS") tables)"
else
  log "    row counts differ from the source (expected on a live system: writes after the backup started):"
  diff <(echo "$SOURCE_COUNTS") <(echo "$RESTORED_COUNTS") | sed 's/^/      /' || true
  if [[ "${NV_STRICT_COUNTS:-0}" == "1" ]]; then echo "NV_STRICT_COUNTS=1: failing." >&2; exit 1; fi
fi
[[ -n "$RESTORED_COUNTS" ]] || { echo "The restored database has no tables." >&2; exit 1; }

# ---- 5. credit ledger verification on the restored copy -----------------------------------------------------------------
LEDGER_STATUS="skipped"
if [[ "${NV_SKIP_LEDGER_CHECK:-0}" == "1" ]]; then
  log "5/5 ledger verification SKIPPED (NV_SKIP_LEDGER_CHECK=1) - this was not a complete drill"
else
  : "${Encryption__MasterKeyBase64:?set Encryption__MasterKeyBase64 to the production master key (the ledger checkpoints are keyed with it)}"
  if [[ -n "${NV_RESTORE_CONNECTION:-}" ]]; then
    CONNECTION="$NV_RESTORE_CONNECTION"
  else
    : "${SQLCMDUSER:?set SQLCMDUSER/SQLCMDPASSWORD or NV_RESTORE_CONNECTION}" "${SQLCMDPASSWORD:?set SQLCMDPASSWORD or NV_RESTORE_CONNECTION}"
    CONNECTION="Server=${NV_VERIFY_SERVER:-$NV_DRILL_SERVER};Database=$RESTORE_DB;User Id=$SQLCMDUSER;Password=$SQLCMDPASSWORD;TrustServerCertificate=True"
  fi
  read -r -a VERIFY_CMD <<< "${NV_VERIFY_CMD:-dotnet run --project src/Migrator -c Release -- verify-ledger}"
  t4=$(now)
  set +e
  ConnectionStrings__Default="$CONNECTION" Serilog__MinimumLevel__Default="${Serilog__MinimumLevel__Default:-Warning}" "${VERIFY_CMD[@]}"
  rc=$?
  set -e
  t5=$(now)
  if [[ $rc -eq 3 ]]; then
    echo "LEDGER VERIFICATION FAILED on the restored copy: the backup is damaged, was tampered with, or the master key is not the one the ledger was anchored with." >&2
    exit 3
  elif [[ $rc -ne 0 ]]; then
    echo "The ledger verifier could not run (exit $rc)." >&2
    exit 1
  fi
  LEDGER_STATUS="clean ($((t5 - t4))s)"
  log "5/5 ledger verification: $LEDGER_STATUS"
fi

log "DRILL PASSED  backup $((t1 - t0))s, restore $((t3 - t2))s, ledger $LEDGER_STATUS.  Record these in the drill log (docs/runbooks/backup-restore.md)."
