param([string]$IcoOut, [string]$PreviewOut)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

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

function Render([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $S / 256.0   # design is on a 256 grid

    # Background: rounded square, violet -> hot pink diagonal gradient
    $bg = RoundRect (8*$u) (8*$u) (240*$u) (240*$u) (56*$u)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $S, $S
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 76, 29, 214)), ([System.Drawing.Color]::FromArgb(255, 255, 46, 136)), 45
    $g.FillPath($grad, $bg)

    $cyan = [System.Drawing.Color]::FromArgb(255, 0, 229, 255)
    $orbitW = [single][Math]::Max(1.6, 14 * $u)

    # Gyro orbit ring (tilted ellipse) - back half drawn before the pad
    $state = $g.Save()
    $g.TranslateTransform(128*$u, 132*$u)
    $g.RotateTransform(-22)
    $orbitPen = New-Object System.Drawing.Pen $cyan, $orbitW
    $ow = 222*$u; $oh = 92*$u
    $g.DrawArc($orbitPen, -$ow/2, -$oh/2, $ow, $oh, 180, 180)
    $g.Restore($state)

    # Gamepad silhouette: body + two grips
    $pad = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pad.AddPath((RoundRect (52*$u) (92*$u) (152*$u) (70*$u) (32*$u)), $false)
    $pad.AddEllipse(42*$u, 112*$u, 64*$u, 88*$u)
    $pad.AddEllipse(150*$u, 112*$u, 64*$u, 88*$u)
    $pad.FillMode = 'Winding'
    $g.FillPath([System.Drawing.Brushes]::White, $pad)

    if ($S -ge 32) {
        $detail = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 120, 34, 190))
        # D-pad
        $g.FillRectangle($detail, 70*$u, 120*$u, 36*$u, 12*$u)
        $g.FillRectangle($detail, 82*$u, 108*$u, 12*$u, 36*$u)
        # Face buttons
        foreach ($b in @(@(168,106), @(156,126), @(180,126), @(168,146))) {
            $g.FillEllipse($detail, ($b[0]-7)*$u, ($b[1]-7)*$u, 14*$u, 14*$u)
        }
    }

    # Front half of the orbit ring, passing over the pad, plus the "gyro" dot riding on it
    $state = $g.Save()
    $g.TranslateTransform(128*$u, 132*$u)
    $g.RotateTransform(-22)
    $g.DrawArc($orbitPen, -$ow/2, -$oh/2, $ow, $oh, 0, 180)
    $dotR = [single][Math]::Max(2.2, 17 * $u)
    $a = 40 * [Math]::PI / 180
    $dx = [single]([Math]::Cos($a) * $ow / 2); $dy = [single]([Math]::Sin($a) * $oh / 2)
    $g.FillEllipse([System.Drawing.Brushes]::White, $dx - $dotR, $dy - $dotR, 2*$dotR, 2*$dotR)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $cyan), $dx - $dotR*0.6, $dy - $dotR*0.6, 1.2*$dotR, 1.2*$dotR)
    $g.Restore($state)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = Render $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256 -and $PreviewOut) { $b.Save($PreviewOut, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    $pngs += , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes($IcoOut, $out.ToArray())
"wrote $IcoOut"
