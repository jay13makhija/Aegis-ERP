#!/usr/bin/env bash
# Creates the first EF Core migration for every service (run once, then commit the Migrations folders).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet tool restore
for s in Identity Inventory Procurement Localization Workflow Audit Analytics; do
  echo "== $s =="
  dotnet ef migrations add InitialCreate \
    --project "src/Services/$s/Erp.$s.Infrastructure" --startup-project "src/Services/$s/Erp.$s.Api" \
    --context "${s}DbContext" --output-dir Migrations
done
