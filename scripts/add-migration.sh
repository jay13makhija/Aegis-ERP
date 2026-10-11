#!/usr/bin/env bash
# usage: scripts/add-migration.sh Inventory AddSerialNumbers
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet ef migrations add "$2" --project "src/Services/$1/Erp.$1.Infrastructure" --startup-project "src/Services/$1/Erp.$1.Api" \
  --context "$1DbContext" --output-dir Migrations
