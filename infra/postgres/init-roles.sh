#!/bin/sh
set +x
set -eu
# Do not enable shell/psql tracing. Environment is supplied by the authorized
# operator/secret store; no password is passed in process arguments.
: "${TP_DATABASE:?TP_DATABASE is required}"
: "${TP_OWNER_PASSWORD:?TP_OWNER_PASSWORD is required}"
: "${TP_APP_PASSWORD:?TP_APP_PASSWORD is required}"
: "${TP_READONLY_PASSWORD:?TP_READONLY_PASSWORD is required}"
export PGUSER="${PGUSER:-${POSTGRES_USER:-postgres}}"
export PGDATABASE="${PGDATABASE:-postgres}"
export PGPASSWORD="${PGPASSWORD:-${POSTGRES_PASSWORD:-}}"
if psql -X -q -v ON_ERROR_STOP=1 -f /opt/teapremium/init-roles.sql; then
  echo 'Database role provisioning completed; existing passwords preserved.'
else
  echo 'Provisioning failed. Check roles/ownership with the authorized operator; no automatic ownership/password repair.' >&2
  exit 1
fi
