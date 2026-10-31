#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
test_project_name="aerosynapse-contract-tests-$$"
trap 'docker compose -p "$test_project_name" -f compose.contracts.yaml down --remove-orphans' EXIT
docker compose -p "$test_project_name" -f compose.contracts.yaml run --build --rm contract-tests
