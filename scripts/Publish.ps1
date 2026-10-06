param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $workspaceRoot 'src\LegacyAddonManager\LegacyAddonManager.csproj'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts'))
$stagingPath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot ('publish-stage-' + [Guid]::NewGuid().ToString('N'))))
$publishPath = Join-Path $artifactsRoot 'publish'
try {
    & (Join-Path $PSScriptRoot 'GetRclone.ps1')
    & dotnet publish $project -c $Configuration -p:PublishProfile=Windows-x64 -p:DebugType=none -p:DebugSymbols=false -o $stagingPath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    $files = @(Get-ChildItem -LiteralPath $stagingPath -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'LegacyAddonManager.exe') { throw 'Expected exactly one published executable.' }
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
    $executable = Join-Path $publishPath 'LegacyAddonManager.exe'
    Move-Item -LiteralPath $files[0].FullName -Destination $executable -Force
    Write-Output "Published self-contained Windows x64 executable: $executable"
    Write-Output "Size: $([Math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 1)) MB"
} finally {
    $allowedPrefix = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $stagingPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
    if (Test-Path -LiteralPath $stagingPath) { Remove-Item -LiteralPath $stagingPath -Recurse -Force }
}
