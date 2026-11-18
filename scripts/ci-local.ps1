<#
.SYNOPSIS
  The checks of .github/workflows/ci.yml, run on this machine before a push (CI on GitHub is paused: free-plan minutes).

.DESCRIPTION
  Same steps, same order, stops at the first failure. Needs .NET (global.json), Node 24, and Docker running for the
  integration tests (Testcontainers). Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -File scripts/ci-local.ps1                     # everything, like CI
  powershell -File scripts/ci-local.ps1 -Only backend       # the backend job only
  powershell -File scripts/ci-local.ps1 -SkipLighthouse     # everything but Lighthouse (the slowest step)
#>
param(
    [ValidateSet("all", "backend", "web")]
    [string]$Only = "all",
    [switch]$SkipIntegration,
    [switch]$SkipE2E,
    [switch]$SkipLighthouse
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$started = Get-Date

function Step([string]$name, [scriptblock]$run) {
    Write-Host ""
    Write-Host "==> $name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $run
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Push-Location $root
try {
    if ($Only -in "all", "backend") {
        Step "Restore" { dotnet restore TicketRush.slnx }
        Step "Build (warnings are errors)" { dotnet build TicketRush.slnx -c Release --no-restore }
        Step "Unit tests: Domain" { dotnet test --project tests/TicketRush.Domain.Tests -c Release --no-build }
        Step "Unit tests: Application" { dotnet test --project tests/TicketRush.Application.Tests -c Release --no-build }
        Step "Unit tests: Architecture" { dotnet test --project tests/TicketRush.ArchitectureTests -c Release --no-build }
        if (-not $SkipIntegration) {
            Step "Docker is running (Testcontainers)" { docker info --format "{{.ServerVersion}}" | Out-Null }
            # Two threads: a laptop runs out of memory and ports with the CI default.
            Step "Integration tests (Testcontainers)" { dotnet test --project tests/TicketRush.Api.IntegrationTests -c Release --no-build -- --max-threads 2 }
        }
        Step "Vulnerable packages (no High or Critical)" {
            $report = dotnet list TicketRush.slnx package --vulnerable --include-transitive | Out-String
            Write-Host $report
            if ($report -match "\b(High|Critical)\b") { $global:LASTEXITCODE = 1 }
        }
    }

    if ($Only -in "all", "web") {
        Push-Location web
        try {
            Step "npm ci" { npm ci }
            Step "API client is up to date with the OpenAPI document" {
                npm run api:gen
                if ($LASTEXITCODE -eq 0) { git diff --exit-code -- src/lib/api/schema.d.ts }
            }
            Step "Lint" { npm run lint }
            Step "Typecheck" { npm run typecheck }
            Step "Unit tests (Vitest)" { npm test }
            Step "Unit tests in a far-away time zone" { npm run test:tz }
            if (-not ($SkipE2E -and $SkipLighthouse)) {
                $env:ENABLE_DESIGN_PAGE = "1"
                Step "Build (with /design for the budget and a11y checks)" { npm run build:e2e }
                Remove-Item Env:ENABLE_DESIGN_PAGE
            }
            if (-not $SkipE2E) {
                Step "Install Playwright Chromium" { npx playwright install chromium }
                Step "E2E (axe, fonts, INP)" { npm run test:e2e }
            }
            if (-not $SkipLighthouse) {
                Step "Lighthouse budgets" { npm run lighthouse }
            }
            Step "Audit: shipped code, any severity" { npm audit --omit=dev --audit-level=low }
            Step "Audit: tooling, high or critical (dated exceptions)" { node scripts/audit-tooling.mjs }
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    Pop-Location
}

$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
Write-Host ""
Write-Host "All CI checks passed in $minutes min. Safe to push." -ForegroundColor Green
