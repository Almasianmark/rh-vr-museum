#!/usr/bin/env bash
# Throwaway Postgres cluster: Supabase stub -> migrations -> ratings_test.sql. Needs postgresql (initdb/pg_ctl/psql).
set -euo pipefail
cd "$(dirname "$0")"
PGBIN=${PGBIN:-$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)}
TMP=$(mktemp -d)
RUNAS=()
if [ "$(id -u)" = 0 ]; then chown postgres "$TMP"; RUNAS=(runuser -u postgres --); fi
"${RUNAS[@]}" "$PGBIN/initdb" -D "$TMP/data" -A trust -U postgres >/dev/null
"${RUNAS[@]}" "$PGBIN/pg_ctl" -D "$TMP/data" -o "-k $TMP -p 54329 -c listen_addresses=''" -l "$TMP/log" start >/dev/null
trap '"${RUNAS[@]}" "$PGBIN/pg_ctl" -D "$TMP/data" stop -m fast >/dev/null; rm -rf "$TMP"' EXIT
PSQL=("${RUNAS[@]}" "$PGBIN/psql" -h "$TMP" -p 54329 -U postgres -d postgres -v ON_ERROR_STOP=1 -q)
"${PSQL[@]}" -f supabase_stub.sql
for f in ../migrations/*.sql; do "${PSQL[@]}" -f "$f"; done
"${PSQL[@]}" -t -f ratings_test.sql
