param([string[]]$Services = @("Identity","Inventory","Procurement","Localization","Workflow","Audit","Analytics"))
$ErrorActionPreference = "Stop"; Set-Location (Join-Path $PSScriptRoot "..")
foreach ($s in $Services) { dotnet ef database update --project "src/Services/$s/Erp.$s.Infrastructure" --startup-project "src/Services/$s/Erp.$s.Api" --context "${s}DbContext" }
