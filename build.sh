#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Non-interactive shells may not have dotnet on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi

COMPOSE=(docker compose -f docker-compose.dev.yml --env-file .env)

cmd_setup() {
  if [ ! -f .env ]; then
    # Strong password that satisfies SQL Server complexity rules.
    pw="Mx1!$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
    printf 'MSSQL_SA_PASSWORD=%s\n' "$pw" > .env
    chmod 600 .env
    echo "Generated .env"
  fi
  dotnet tool restore
  # Task 15 adds the Playwright browser install here.
}

cmd_dev_db() {
  [ -f .env ] || cmd_setup
  "${COMPOSE[@]}" up -d
  echo "Waiting for SQL Server to become healthy..."
  for _ in $(seq 1 60); do
    cid="$("${COMPOSE[@]}" ps -q sql)"
    status="$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then echo "SQL Server is healthy on localhost:14333"; return 0; fi
    sleep 5
  done
  echo "SQL Server did not become healthy in time" >&2
  return 1
}

cmd_test() {
  dotnet test --solution Maldex.slnx --ignore-exit-code 8  # 8 = a project has no tests yet
  if [ -f ui/package.json ]; then
    npm --prefix ui test -- --run
  fi
}

not_impl() { echo "'$1' is not implemented until Task 19" >&2; exit 2; }

case "${1:-}" in
  setup)  cmd_setup ;;
  dev-db) cmd_dev_db ;;
  test)   cmd_test ;;
  run)    not_impl run ;;
  smoke)  not_impl smoke ;;
  build)  not_impl build ;;
  e2e)    echo "Real-VM E2E is implemented in Plan 1b"; exit 2 ;;
  *) echo "usage: $0 setup|dev-db|run|test|smoke|build|e2e" >&2; exit 64 ;;
esac
