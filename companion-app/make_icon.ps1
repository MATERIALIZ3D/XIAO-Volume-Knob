Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$dir = "C:\Users\Fantom Works\Documents\PlatformIO\Projects\XIAO Volume Knob\companion-app"

$SM  = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$IM  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$POM = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$Round = [System.Drawing.Drawing2D.LineCap]::Round

function Draw-Master {
  $S = 256
  $bmp = New-Object System.Drawing.Bitmap($S,$S,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = $SM
  $g.Clear([System.Drawing.Color]::Transparent)

  # ---- rounded dark tile background ----
  $r = 46; $d = 2*$r
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0,0,$d,$d,180,90)
  $path.AddArc($S-$d,0,$d,$d,270,90)
  $path.AddArc($S-$d,$S-$d,$d,$d,0,90)
  $path.AddArc(0,$S-$d,$d,$d,90,90)
  $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush([System.Drawing.Point]::new(0,0),[System.Drawing.Point]::new(0,$S),[System.Drawing.Color]::FromArgb(255,36,38,44),[System.Drawing.Color]::FromArgb(255,13,14,17))
  $g.FillPath($bg,$path)

  $cx = 128.0; $cy = 131.0
  $N = 30; $startDeg = 125.0; $spanDeg = 290.0
  $Ri = 92.0; $Ro = 110.0

  # ---- glow pass ----
  for ($i=0; $i -lt $N; $i++) {
    $ang = ($startDeg + $i*($spanDeg/($N-1))) * [Math]::PI/180.0
    $x1 = $cx + $Ri*[Math]::Cos($ang); $y1 = $cy + $Ri*[Math]::Sin($ang)
    $x2 = $cx + $Ro*[Math]::Cos($ang); $y2 = $cy + $Ro*[Math]::Sin($ang)
    $glow = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(65,45,200,255),12.0)
    $glow.StartCap = $Round; $glow.EndCap = $Round
    $g.DrawLine($glow,$x1,$y1,$x2,$y2); $glow.Dispose()
  }
  # ---- ticks ----
  for ($i=0; $i -lt $N; $i++) {
    $ang = ($startDeg + $i*($spanDeg/($N-1))) * [Math]::PI/180.0
    $x1 = $cx + $Ri*[Math]::Cos($ang); $y1 = $cy + $Ri*[Math]::Sin($ang)
    $x2 = $cx + $Ro*[Math]::Cos($ang); $y2 = $cy + $Ro*[Math]::Sin($ang)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,50,200,255),5.5)
    $pen.StartCap = $Round; $pen.EndCap = $Round
    $g.DrawLine($pen,$x1,$y1,$x2,$y2); $pen.Dispose()
  }

  # ---- knob body ----
  $Rb = 82.0
  $rect = New-Object System.Drawing.RectangleF(($cx-$Rb),($cy-$Rb),(2*$Rb),(2*$Rb))
  $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,[System.Drawing.Color]::FromArgb(255,48,51,58),[System.Drawing.Color]::FromArgb(255,11,12,14),90.0)
  $g.FillEllipse($body,$rect)
  $bez = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,7,8,10),6.0)
  $g.DrawEllipse($bez,$rect)
  $hl = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55,130,140,150),2.0)
  $g.DrawEllipse($hl,($cx-$Rb+4),($cy-$Rb+4),(2*$Rb-8),(2*$Rb-8))

  # ---- pointer near top ----
  $pa = 270.0 * [Math]::PI/180.0
  $pi = 42.0; $po = 66.0
  $px1 = $cx + $pi*[Math]::Cos($pa); $py1 = $cy + $pi*[Math]::Sin($pa)
  $px2 = $cx + $po*[Math]::Cos($pa); $py2 = $cy + $po*[Math]::Sin($pa)
  $ptr = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,223,229,235),6.0)
  $ptr.StartCap = $Round; $ptr.EndCap = $Round
  $g.DrawLine($ptr,$px1,$py1,$px2,$py2)

  $g.Dispose()
  return $bmp
}

function Resize-Bmp($master,$size){
  $b = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gg = [System.Drawing.Graphics]::FromImage($b)
  $gg.InterpolationMode = $IM; $gg.SmoothingMode = $SM; $gg.PixelOffsetMode = $POM
  $gg.DrawImage($master,0,0,$size,$size)
  $gg.Dispose()
  return $b
}

function Png-Bytes($bmp){
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
  return ,$ms.ToArray()
}

$master = Draw-Master
$master.Save("$dir\knob_preview.png",[System.Drawing.Imaging.ImageFormat]::Png)

$sizes = 256,64,48,32,16
$images = @()
foreach($s in $sizes){
  if($s -eq 256){ $img = $master } else { $img = Resize-Bmp $master $s }
  $images += ,@{ Size=$s; Bytes=(Png-Bytes $img) }
}

$fs = [System.IO.File]::Create("$dir\knob.ico")
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16*$images.Count
foreach($im in $images){
  $sz = $im.Size; $len = $im.Bytes.Length
  $wh = [Byte]($(if($sz -ge 256){0}else{$sz}))
  $bw.Write($wh); $bw.Write($wh); $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
  $offset += $len
}
foreach($im in $images){ $bw.Write($im.Bytes) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Output ("ICO written: {0} bytes, {1} images" -f (Get-Item "$dir\knob.ico").Length, $images.Count)
