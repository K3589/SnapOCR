param(
    [string]$OutDir
)

Add-Type -AssemblyName System.Drawing

$outDir = $OutDir
if ([string]::IsNullOrWhiteSpace($outDir)) {
    $outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SnapOCR\Assets'
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$c1 = [System.Drawing.Color]::FromArgb(255, 46, 124, 236)
$c2 = [System.Drawing.Color]::FromArgb(255, 15, 72, 166)
$white = [System.Drawing.Color]::White
$whiteSoft = [System.Drawing.Color]::FromArgb(235, 255, 255, 255)

function New-RoundedPath([System.Drawing.Rectangle]$r, [int]$radius) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    if ($d -le 0) { $p.AddRectangle($r); return $p }
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int]($size * 0.08)
    $rect = [System.Drawing.Rectangle]::new($pad, $pad, $size - 2 * $pad, $size - 2 * $pad)
    $path = New-RoundedPath $rect ([int]($size * 0.22))
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c1, $c2, [single]45)
    $g.FillPath($brush, $path)

    $penW = [single][Math]::Max(1.2, $size * 0.055)
    $pen = [System.Drawing.Pen]::new($white, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $m = [single]($size * 0.27)
    $arm = [single]($size * 0.15)
    $far = [single]($size) - $m
    # 取景框四角
    $g.DrawLine($pen, $m, ($m + $arm), $m, $m)
    $g.DrawLine($pen, $m, $m, ($m + $arm), $m)
    $g.DrawLine($pen, ($far - $arm), $m, $far, $m)
    $g.DrawLine($pen, $far, $m, $far, ($m + $arm))
    $g.DrawLine($pen, $m, ($far - $arm), $m, $far)
    $g.DrawLine($pen, $m, $far, ($m + $arm), $far)
    $g.DrawLine($pen, ($far - $arm), $far, $far, $far)
    $g.DrawLine($pen, $far, $far, $far, ($far - $arm))

    $lw = [single][Math]::Max(1.0, $size * 0.05)
    $linePen = [System.Drawing.Pen]::new($whiteSoft, $lw)
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $lx0 = [single]($size * 0.35); $lx1 = [single]($size * 0.65)
    $g.DrawLine($linePen, $lx0, [single]($size * 0.43), $lx1, [single]($size * 0.43))
    $g.DrawLine($linePen, $lx0, [single]($size * 0.52), $lx1, [single]($size * 0.52))
    $g.DrawLine($linePen, $lx0, [single]($size * 0.61), [single]($size * 0.54), [single]($size * 0.61))

    $linePen.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$file) {
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
}

foreach ($pair in @(@('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150), @('StoreLogo.png', 50), @('Square71x71Logo.png', 71))) {
    $b = New-IconBitmap ([int]$pair[1])
    Save-Png $b (Join-Path $outDir $pair[0])
    $b.Dispose()
}

$wide = [System.Drawing.Bitmap]::new(310, 150, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($wide)
$g.Clear($c2)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$icon = New-IconBitmap 96
$g.DrawImage($icon, 22, 27, 96, 96)
$f1 = [System.Drawing.Font]::new('Segoe UI', [single]26, [System.Drawing.FontStyle]::Bold)
$f2 = [System.Drawing.Font]::new('Segoe UI', [single]13)
$g.DrawString('SnapOCR', $f1, [System.Drawing.Brushes]::White, 132, 44)
$g.DrawString('Select. Read. Paste.', $f2, [System.Drawing.Brushes]::White, 135, 86)
$g.Dispose(); $icon.Dispose(); $f1.Dispose(); $f2.Dispose()
Save-Png $wide (Join-Path $outDir 'Wide310x150Logo.png')
$wide.Dispose()

$splash = [System.Drawing.Bitmap]::new(620, 300, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($splash)
$g.Clear($c2)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$icon2 = New-IconBitmap 160
$g.DrawImage($icon2, 230, 70, 160, 160)
$g.Dispose(); $icon2.Dispose()
Save-Png $splash (Join-Path $outDir 'SplashScreen.png')
$splash.Dispose()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$blobs = [System.Collections.ArrayList]::new()
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = [System.IO.MemoryStream]::new()
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$blobs.Add($ms.ToArray())
    $b.Dispose()
    $ms.Dispose()
}

$icoPath = Join-Path $outDir 'AppIcon.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($blob in $blobs) { $bw.Write($blob) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
