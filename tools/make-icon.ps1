# Renders src/Slate/Assets/slate.ico (purple rounded square with a â¯) at several sizes.
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Slate\Assets\slate.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-IconFrame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    $inset = [Math]::Max(1, $size / 32)
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
    $d = $size * 0.5
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(0x4C, 0x1D, 0x95)), ([System.Drawing.Color]::FromArgb(0x14, 0x08, 0x2A))
    $g.FillPath($fill, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xA7, 0x8B, 0xFA)), ([Math]::Max(1.0, $size / 20.0))
    $g.DrawPath($pen, $path)

    $font = New-Object System.Drawing.Font 'Segoe UI Symbol', ($size * 0.55), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xE9, 0xD5, 0xFF))
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $fmt.LineAlignment = 'Center'
    $g.DrawString([string][char]0x276F, $font, $brush, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $fmt)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large frame: PNG (smaller file; every modern reader supports it at 256).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small frames: classic 32-bit DIB, which System.Drawing.Icon (tray icon) can read.
        $w = New-Object System.IO.BinaryWriter $ms
        $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))   # height covers XOR + AND
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
        $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {                                    # bottom-up BGRA
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)                        # AND mask: all zero, alpha does the work
        $w.Write((New-Object byte[] ($maskRow * $size)))
        $w.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()   # comma: don't unroll the byte array
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $frames.Add((New-IconFrame $s)) }

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                  # ICONDIRENTRY
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $out)).Path + '\slate.ico', $ms.ToArray())
"Wrote $out ($($ms.Length) bytes)"
