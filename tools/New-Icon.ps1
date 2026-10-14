param([string]$OutputRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $navy = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#102b46'))
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#f4f8fc'))
    $mint = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#37cbb3'))
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(8,8,104,104,180,90); $tile.AddArc(144,8,104,104,270,90)
    $tile.AddArc(144,144,104,104,0,90); $tile.AddArc(8,144,104,104,90,90); $tile.CloseFigure()
    $graphics.FillPath($navy,$tile)
    $trace = [System.Drawing.Pen]::new($mint,12)
    $trace.StartCap = $trace.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($coords in @(@(42,74,86,74),@(86,74,86,102),@(86,102,114,102),@(42,136,76,136),@(76,136,76,184),@(76,184,118,184),@(128,38,128,73),@(76,38,76,52),@(42,202,72,202),@(166,44,166,69))) {
        $graphics.DrawLine($trace,[single]$coords[0],[single]$coords[1],[single]$coords[2],[single]$coords[3])
    }
    foreach ($point in @(@(42,74),@(42,136),@(128,38),@(42,202))) { $graphics.FillEllipse($mint,($point[0]-9),($point[1]-9),18,18) }
    $graphics.FillEllipse($white,90,84,140,140)
    $graphics.FillEllipse($navy,104,98,112,112)
    $hand = [System.Drawing.Pen]::new($white,12)
    $hand.StartCap = $hand.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($hand,160,115,160,154); $graphics.DrawLine($hand,160,154,187,171)
    $graphics.FillEllipse($mint,152,146,16,16)
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add($memory.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $OutputRoot 'assets\icon.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $hand.Dispose(); $trace.Dispose(); $tile.Dispose()
    $navy.Dispose(); $white.Dispose(); $mint.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$iconPath = Join-Path $OutputRoot 'src\SPDScheduler.App\app.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$pngs[$i].Length); $writer.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $writer.Write($png) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output $iconPath
