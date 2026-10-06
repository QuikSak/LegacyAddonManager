$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $workspaceRoot 'src\LegacyAddonManager\Assets'
New-Item -ItemType Directory -Path $assetsPath -Force | Out-Null
$sizes = @(16, 32, 48, 64, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(16, 20, 28))
    $gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(232, 188, 106))
    $background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(52, 45, 34))
    $graphics.FillRectangle($background, [single]($size * 0.08), [single]($size * 0.08), [single]($size * 0.84), [single]($size * 0.84))
    # A geometric L stays legible at small Windows icon sizes.
    $graphics.FillRectangle($gold, [single]($size * 0.30), [single]($size * 0.23), [single]($size * 0.14), [single]($size * 0.54))
    $graphics.FillRectangle($gold, [single]($size * 0.30), [single]($size * 0.64), [single]($size * 0.42), [single]($size * 0.13))
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,($stream.ToArray())
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $gold.Dispose(); $background.Dispose()
}
$iconPath = Join-Path $assetsPath 'app.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $iconPath"
