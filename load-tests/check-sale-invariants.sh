#!/usr/bin/env bash
# Runs load-tests/sql/sale-invariants.sql and exits non-zero when any invariant is violated.
#
#   0  every invariant holds (the query returned zero rows)
#   1  at least one violation (the rows are printed)
#   2  the check itself could not run (psql missing, wrong credentials, SQL error, ...)
#
# How psql is reached (first match wins):
#   PSQL_CMD   a full command that reads SQL on stdin, e.g. "psql postgresql://user:pass@host:5432/ticketrush".
#              Needs LOADTEST_CONFIRM=loadtest: the query is read-only, but the point of the guard is that you pass it a
#              LOAD-TEST database on purpose, never a real deployment's.
#   default    the load-test compose project (own name, own volumes, never the real `ticketrush` project):
#              docker compose -p ticketrush-loadtest -f docker-compose.prod.yml -f load-tests/docker-compose.loadtest.yml exec -T postgres psql -U ticketrush -d ticketrush
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
sql="$here/sql/sale-invariants.sql"

if [[ -n "${PSQL_CMD:-}" ]]; then
  if [[ "${LOADTEST_CONFIRM:-}" != "loadtest" ]]; then
    echo "sale invariants: PSQL_CMD points at a database of your choice; set LOADTEST_CONFIRM=loadtest to confirm it is a load-test database, not a real deployment." >&2
    exit 2
  fi
  read -r -a psql <<<"$PSQL_CMD"
else
  psql=(docker compose -p ticketrush-loadtest -f "$repo/docker-compose.prod.yml" -f "$here/docker-compose.loadtest.yml"
        ${ENV_FILE:+--env-file "$ENV_FILE"} exec -T postgres psql -U ticketrush -d ticketrush)
fi

# -X: ignore ~/.psqlrc; -A -t -F'|': bare rows; ON_ERROR_STOP: a SQL error is exit 2, not "no violations".
if ! rows="$("${psql[@]}" -X -v ON_ERROR_STOP=1 -A -t -F ' | ' <"$sql")"; then
  echo "sale invariants: could not run $sql" >&2
  exit 2
fi

if [[ -z "$rows" ]]; then
  echo "sale invariants: OK (0 violations)"
  exit 0
fi

echo "sale invariants: VIOLATED" >&2
echo "check_name | subject | detail" >&2
echo "$rows" >&2
exit 1
