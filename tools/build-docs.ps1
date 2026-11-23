param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $revision = git rev-parse HEAD
    $stage = 'artifacts/docs'
    if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -Path $stage -ItemType Directory -Force | Out-Null
    Copy-Item docs/site/docfx.json, docs/site/toc.yml $stage
    foreach ($page in @('docs/site/index.md', 'docs/site/operator.md', 'docs/site/status.md', 'docs/guides/client-developer-guide.md')) {
        (Get-Content $page -Raw).Replace('/REVISION', "/$revision") |
            Set-Content (Join-Path $stage (Split-Path $page -Leaf)) -NoNewline
    }
    & $Dotnet tool restore
    & $Dotnet run --configuration Release --file tools/DocsReference.cs -- $stage $revision
    & $Dotnet tool run docfx "$stage/docfx.json" --warningsAsErrors
    python tools/check-docs.py "$stage/_site"
} finally {
    Pop-Location
}
