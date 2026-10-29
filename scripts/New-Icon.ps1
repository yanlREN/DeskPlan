$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path $PSScriptRoot '..\src\DeskPlan.App\Assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$images = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $edge = [single]($size * 0.05)
    $diameter = [single]($size * 0.36)
    $span = [single]($size - 2 * $edge)
    $path.AddArc($edge, $edge, $diameter, $diameter, 180, 90)
    $path.AddArc($edge + $span - $diameter, $edge, $diameter, $diameter, 270, 90)
    $path.AddArc($edge + $span - $diameter, $edge + $span - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($edge, $edge + $span - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#238881'))
    $graphics.FillPath($brush, $path)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]($size * 0.085))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($size * 0.27, $size * 0.52),
        [System.Drawing.PointF]::new($size * 0.43, $size * 0.68),
        [System.Drawing.PointF]::new($size * 0.75, $size * 0.34))
    $graphics.DrawLines($pen, $points)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($stream.ToArray())
    $stream.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$iconPath = Join-Path $iconDirectory 'deskplan.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output $iconPath
