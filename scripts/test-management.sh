#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
project_name="aerosynapse-management-tests-$$"
trap 'docker compose -p "$project_name" -f compose.management-tests.yaml down --volumes --remove-orphans' EXIT
docker compose -p "$project_name" -f compose.management-tests.yaml run --build --rm management-tests
