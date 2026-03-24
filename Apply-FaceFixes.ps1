#Requires -Version 7.0
#Requires -RunAsAdministrator

param(
    [string]$Root = (Get-Location).Path
)

$matcher = Join-Path $Root "Services\Biometrics\FastFaceMatcher.cs"

if (!(Test-Path $matcher)) {
    throw "FastFaceMatcher.cs not found"
}

Copy-Item $matcher "$matcher.bak" -Force

$content = Get-Content $matcher -Raw

# Replace single compare with multi-frame logic
$pattern = 'var\s+dist\s*=\s*Compare\([^)]+\);'

$replacement = @"
var distances = encodings.Select(e => Compare(e, template));
var dist = distances.Min();
"@

$new = [regex]::Replace($content, $pattern, $replacement)

Set-Content $matcher $new -NoNewline

Write-Host "✓ Multi-frame matching applied" -ForegroundColor Green