#!/usr/bin/env bash
# Applies all pending migrations. usage: scripts/update-database.sh            (all services)
#                                       scripts/update-database.sh Inventory   (one service)
set -euo pipefail
cd "$(dirname "$0")/.."
list=${1:-"Identity Inventory Procurement Localization Workflow Audit Analytics"}
for s in $list; do
  echo "== $s =="
  dotnet ef database update --project "src/Services/$s/Erp.$s.Infrastructure" --startup-project "src/Services/$s/Erp.$s.Api" --context "${s}DbContext"
done
