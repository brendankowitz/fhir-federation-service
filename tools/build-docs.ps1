param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $revision = git rev-parse HEAD
    $stage = 'artifacts/docs/references'
    New-Item -Path $stage -ItemType Directory -Force | Out-Null
    & $Dotnet run --configuration Release --file tools/DocsReference.cs -- $stage $revision
    npm --prefix docs/site ci
    node --test docs/site/scripts/rewrite-links.test.mjs
    node docs/site/scripts/stage-docs.mjs $revision
    npm --prefix docs/site run build
    python tools/check-docs.py docs/site/dist
    node docs/site/scripts/check-migration.mjs docs/site/dist
} finally {
    Pop-Location
}
