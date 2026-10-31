#!/bin/sh
set -eu
exec python -m uvicorn app.main:app --host 0.0.0.0 --port 8000 --log-level "${LOG_LEVEL:-info}" --no-access-log
