param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'Codex.ico'),
    [string]$PreviewPath = (Join-Path $PSScriptRoot 'Codex-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath($Rectangle, [float]$Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $arc = [Drawing.RectangleF]::new($Rectangle.X, $Rectangle.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $Rectangle.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Rectangle.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Rectangle.X
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    $path
}

$size = 256
$bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([Drawing.Color]::Transparent)

$bounds = [Drawing.RectangleF]::new(10, 10, 236, 236)
$shape = New-RoundedRectanglePath $bounds 54
$start = [Drawing.Color]::FromArgb(255, 116, 132, 214)
$finish = [Drawing.Color]::FromArgb(255, 93, 194, 181)
$background = [Drawing.Drawing2D.LinearGradientBrush]::new(
    [Drawing.PointF]::new(20, 20),
    [Drawing.PointF]::new(236, 236),
    $start,
    $finish)
$graphics.FillPath($background, $shape)

$glowBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(42, 255, 255, 255))
$graphics.FillEllipse($glowBrush, -28, -40, 260, 210)

$innerPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(38, 20, 35, 72), 8)
$innerPen.Alignment = [Drawing.Drawing2D.PenAlignment]::Inset
$graphics.DrawPath($innerPen, $shape)
$highlightPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(150, 255, 255, 255), 4)
$highlightPen.Alignment = [Drawing.Drawing2D.PenAlignment]::Inset
$graphics.DrawPath($highlightPen, $shape)

$arcPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(245, 255, 255, 255), 27)
$arcPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$arcPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($arcPen, 56, 56, 144, 144, 38, 282)

$dotBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 210, 239, 226))
$graphics.FillEllipse($dotBrush, 178, 70, 25, 25)
$dotHighlight = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(175, 255, 255, 255))
$graphics.FillEllipse($dotHighlight, 183, 74, 8, 8)

$previewDirectory = Split-Path -Parent $PreviewPath
if ($previewDirectory) { [IO.Directory]::CreateDirectory($previewDirectory) | Out-Null }
$bitmap.Save($PreviewPath, [Drawing.Imaging.ImageFormat]::Png)

$pngStream = [IO.MemoryStream]::new()
$bitmap.Save($pngStream, [Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) { [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null }
$file = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]1)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$pngBytes.Length)
    $writer.Write([uint32]22)
    $writer.Write($pngBytes)
}
finally {
    $writer.Dispose()
    $file.Dispose()
    $pngStream.Dispose()
    $arcPen.Dispose()
    $dotBrush.Dispose()
    $dotHighlight.Dispose()
    $innerPen.Dispose()
    $highlightPen.Dispose()
    $glowBrush.Dispose()
    $background.Dispose()
    $shape.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host "Generated: $OutputPath"
