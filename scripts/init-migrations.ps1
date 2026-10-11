$ErrorActionPreference = "Stop"; Set-Location (Join-Path $PSScriptRoot "..")
dotnet tool restore
foreach ($s in "Identity","Inventory","Procurement","Localization","Workflow","Audit","Analytics") {
  Write-Host "== $s =="
  dotnet ef migrations add InitialCreate --project "src/Services/$s/Erp.$s.Infrastructure" --startup-project "src/Services/$s/Erp.$s.Api" --context "${s}DbContext" --output-dir Migrations
}
