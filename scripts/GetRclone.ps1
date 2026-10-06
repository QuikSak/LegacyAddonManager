$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $workspaceRoot 'src\LegacyAddonManager\Assets\ThirdParty'
$assetPath = Join-Path $assetDirectory 'rclone-windows-amd64.zip'
$expectedHash = '200EB602C126D82AA38B51E0F6B9AE837473FF99B51278D3F6F837574C494D6E'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
if ((Test-Path -LiteralPath $assetPath) -and (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -eq $expectedHash) {
    Write-Output 'Bundled rclone v1.75.1 archive is already verified.'
    return
}
$temporaryPath = Join-Path $assetDirectory ('.lam-rclone-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri 'https://downloads.rclone.org/v1.75.1/rclone-v1.75.1-windows-amd64.zip' -OutFile $temporaryPath -UseBasicParsing
    if ((Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Downloaded rclone archive failed SHA-256 verification. The existing archive was preserved.' }
    Move-Item -LiteralPath $temporaryPath -Destination $assetPath -Force
    Write-Output 'Downloaded and verified bundled rclone v1.75.1 (Windows x64).'
} finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}
