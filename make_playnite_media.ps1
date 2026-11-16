# Generates Playnite media for DS Gyro 360 in .\playnite: icon, cover (2:3), background (16:9) and a logo.
param([string]$OutDir = (Join-Path $PSScriptRoot 'playnite'))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$violet = [System.Drawing.Color]::FromArgb(255, 76, 29, 214)
$pink   = [System.Drawing.Color]::FromArgb(255, 255, 46, 136)
$cyan   = [System.Drawing.Color]::FromArgb(255, 0, 229, 255)
$detailColor = [System.Drawing.Color]::FromArgb(255, 120, 34, 190)

function C([int]$a, [System.Drawing.Color]$c) { [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function NewCanvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

# The app's emblem (gamepad + gyro orbit), same design as app.ico, on a 256 grid placed at ($x,$y) with size $S.
function DrawEmblem($g, [single]$x, [single]$y, [single]$S, [bool]$withTile, [single]$glow = 0) {
    $state0 = $g.Save()
    $g.TranslateTransform($x, $y)
    $u = $S / 256.0
    if ($withTile) {
        $bg = RoundRect (8*$u) (8*$u) (240*$u) (240*$u) (56*$u)
        $rect = New-Object System.Drawing.RectangleF 0, 0, $S, $S
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $violet, $pink, 45
        $g.FillPath($grad, $bg)
    }
    $ow = 222*$u; $oh = 92*$u
    if ($glow -gt 0) {
        # Soft cyan halo behind the orbit
        for ($i = 12; $i -ge 1; $i--) {
            $pen = New-Object System.Drawing.Pen (C ([int](8 * $glow)) $cyan), ([single](14*$u + $i * 4.5*$u))
            $st = $g.Save(); $g.TranslateTransform(128*$u, 132*$u); $g.RotateTransform(-22)
            $g.DrawEllipse($pen, -$ow/2, -$oh/2, $ow, $oh)
            $g.Restore($st)
        }
    }
    $orbitPen = New-Object System.Drawing.Pen $cyan, ([single](14*$u))
    $st = $g.Save(); $g.TranslateTransform(128*$u, 132*$u); $g.RotateTransform(-22)
    $g.DrawArc($orbitPen, -$ow/2, -$oh/2, $ow, $oh, 180, 180)
    $g.Restore($st)

    $pad = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pad.AddPath((RoundRect (52*$u) (92*$u) (152*$u) (70*$u) (32*$u)), $false)
    $pad.AddEllipse(42*$u, 112*$u, 64*$u, 88*$u)
    $pad.AddEllipse(150*$u, 112*$u, 64*$u, 88*$u)
    $pad.FillMode = 'Winding'
    $g.FillPath([System.Drawing.Brushes]::White, $pad)
    $detail = New-Object System.Drawing.SolidBrush $detailColor
    $g.FillRectangle($detail, 70*$u, 120*$u, 36*$u, 12*$u)
    $g.FillRectangle($detail, 82*$u, 108*$u, 12*$u, 36*$u)
    foreach ($b in @(@(168,106), @(156,126), @(180,126), @(168,146))) {
        $g.FillEllipse($detail, ($b[0]-7)*$u, ($b[1]-7)*$u, 14*$u, 14*$u)
    }

    $st = $g.Save(); $g.TranslateTransform(128*$u, 132*$u); $g.RotateTransform(-22)
    $g.DrawArc($orbitPen, -$ow/2, -$oh/2, $ow, $oh, -3, 186) # overlaps the back half: no seams
    $dotR = 17*$u
    $a = 40 * [Math]::PI / 180
    $dx = [single]([Math]::Cos($a) * $ow / 2); $dy = [single]([Math]::Sin($a) * $oh / 2)
    $g.FillEllipse([System.Drawing.Brushes]::White, $dx - $dotR, $dy - $dotR, 2*$dotR, 2*$dotR)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $cyan), $dx - $dotR*0.6, $dy - $dotR*0.6, 1.2*$dotR, 1.2*$dotR)
    $g.Restore($st)
    $g.Restore($state0)
}

# Faint concentric tilted orbits, the backdrop motif.
function DrawOrbits($g, [single]$cx, [single]$cy, [single]$r, [int]$count, [int]$alpha, [single]$width) {
    for ($i = 1; $i -le $count; $i++) {
        $rw = $r * $i / $count
        $pen = New-Object System.Drawing.Pen (C $alpha ([System.Drawing.Color]::White)), $width
        $st = $g.Save(); $g.TranslateTransform($cx, $cy); $g.RotateTransform(-22)
        $g.DrawEllipse($pen, -$rw, -$rw * 0.42, 2*$rw, 2*$rw * 0.42)
        $g.Restore($st)
    }
}

function PickFont([single]$size, [string[]]$names, $style = 'Bold') {
    $installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name
    foreach ($n in $names) { if ($installed -contains $n) { return New-Object System.Drawing.Font $n, $size, ([System.Drawing.FontStyle]$style), ([System.Drawing.GraphicsUnit]::Pixel) } }
    return New-Object System.Drawing.Font 'Segoe UI', $size, ([System.Drawing.FontStyle]$style), ([System.Drawing.GraphicsUnit]::Pixel)
}

function CenterText($g, [string]$text, $font, $brush, [single]$cx, [single]$y) {
    $sz = $g.MeasureString($text, $font, 10000, [System.Drawing.StringFormat]::GenericTypographic)
    $g.DrawString($text, $font, $brush, $cx - $sz.Width / 2, $y, [System.Drawing.StringFormat]::GenericTypographic)
}

function Save($bmp, $g, [string]$name, [string]$format = 'Png') {
    $g.Dispose()
    $path = Join-Path $OutDir $name
    if ($format -eq 'Jpeg') {
        $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
        $p = New-Object System.Drawing.Imaging.EncoderParameters 1
        $p.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
        $bmp.Save($path, $enc, $p)
    } else { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    "wrote $path"
}

$titleFonts = 'Segoe UI Black', 'Bahnschrift', 'Segoe UI'
$white = [System.Drawing.Brushes]::White

# ---- Icon: 256x256, the app icon ----
$bmp, $g = NewCanvas 256 256
DrawEmblem $g 0 0 256 $true
Save $bmp $g 'icon.png'

# ---- Cover: 600x900 (2:3) ----
$W = 600; $H = 900
$bmp, $g = NewCanvas $W $H
$rect = New-Object System.Drawing.RectangleF 0, 0, $W, $H
$g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $violet, $pink, 65), $rect)
# darken the lower part so the title reads well
$shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 420, $W, 480), (C 0 ([System.Drawing.Color]::Black)), (C 150 ([System.Drawing.Color]::Black)), 90
$g.FillRectangle($shade, 0, 421, $W, 479)
DrawOrbits $g 300 330 560 7 22 2
DrawEmblem $g 70 120 460 $false 1
$f1 = PickFont 92 $titleFonts
$f2 = PickFont 30 'Segoe UI Semibold', 'Segoe UI' 'Regular'
CenterText $g 'DS GYRO' $f1 $white 300 615
CenterText $g '360' (PickFont 120 $titleFonts) (New-Object System.Drawing.SolidBrush $cyan) 300 700
CenterText $g ('GYRO AIM  ' + [char]0x00B7 + '  XBOX 360 PAD') $f2 (New-Object System.Drawing.SolidBrush (C 220 ([System.Drawing.Color]::White))) 300 840
Save $bmp $g 'cover.png'

# ---- Background: 1920x1080 (16:9), dark so Playnite's text stays readable; art on the right ----
$W = 1920; $H = 1080
$bmp, $g = NewCanvas $W $H
$rect = New-Object System.Drawing.RectangleF 0, 0, $W, $H
$dark1 = [System.Drawing.Color]::FromArgb(255, 14, 10, 34)
$dark2 = [System.Drawing.Color]::FromArgb(255, 58, 16, 92)
$g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $dark1, $dark2, 20), $rect)
# pink glow behind the art
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(820, -40, 1300, 1160)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$pgb.CenterColor = C 120 $pink
$pgb.SurroundColors = @((C 0 $pink))
$g.FillPath($pgb, $glowPath)
DrawOrbits $g 1420 540 1250 9 16 2
DrawEmblem $g 1010 150 800 $false 1
Save $bmp $g 'background.jpg' 'Jpeg'

# ---- Logo: transparent, 1200x360 (emblem + wordmark), for themes / Extra Metadata logos ----
$fl = PickFont 150 $titleFonts
$gf = [System.Drawing.StringFormat]::GenericTypographic
$measure = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap 1, 1))
$W = [int](320 + $measure.MeasureString('DS GYRO 360', $fl, 10000, $gf).Width + 30); $H = 360
$bmp, $g = NewCanvas $W $H
DrawEmblem $g 10 30 300 $false 0.6
$fl = PickFont 150 $titleFonts
$g.DrawString('DS GYRO', $fl, $white, 320, 40, [System.Drawing.StringFormat]::GenericTypographic)
$gyroW = $g.MeasureString('DS GYRO', $fl, 10000, [System.Drawing.StringFormat]::GenericTypographic).Width + 0.28 * $fl.Size # typographic measuring drops trailing spaces
$g.DrawString('360', $fl, (New-Object System.Drawing.SolidBrush $cyan), 320 + $gyroW, 40, [System.Drawing.StringFormat]::GenericTypographic)
$g.DrawString('GYRO AIM FOR EVERY GAME', (PickFont 44 'Segoe UI Semibold', 'Segoe UI' 'Regular'), (New-Object System.Drawing.SolidBrush (C 230 ([System.Drawing.Color]::White))), 328, 232, [System.Drawing.StringFormat]::GenericTypographic)
Save $bmp $g 'logo.png'
