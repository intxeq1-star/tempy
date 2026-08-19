#!/bin/sh
set -e
cd "$(dirname "$0")"
if [ ! -x .venv/bin/uvicorn ]; then
  python3 -m venv .venv
  .venv/bin/pip install -r server/requirements.txt
fi
exec .venv/bin/uvicorn server.main:app --host 0.0.0.0 --port 8080
