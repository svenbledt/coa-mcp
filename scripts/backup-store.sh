#!/bin/sh
# Nightly backup of the writable store, for a Coolify scheduled task inside the container (the image has it at /app/backup-store.sh):
#   sh /app/backup-store.sh [directory]
# The aspnet image has no sqlite3, so the app makes the copy itself (VACUUM INTO, safe while the server runs).
# Default target is /data/writable/backups, because /data/writable is the one place UID 10001 can write.
# It writes store-YYYYMMDD.sqlite (via a temporary name), keeps 14 days of files with exactly that name, and exits 1 if there is no store.
set -eu
dir=${1:-/data/writable/backups}
here=$(cd "$(dirname "$0")" && pwd)
dll=${COAMCP_DLL:-$here/CoaMcp.Server.dll}
exec dotnet "$dll" admin backup "$dir"
