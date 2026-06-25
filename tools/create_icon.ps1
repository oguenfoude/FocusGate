Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

$bg = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(9, 9, 11))
$g.FillRectangle($bg, 0, 0, $size, $size)

$green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(16, 185, 129))
$g.FillRectangle($green, 40, 40, 176, 176)

$whiteFont = New-Object System.Drawing.Font("Segoe UI", 72, [System.Drawing.FontStyle]::Bold)
$textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$g.DrawString("FP", $whiteFont, $textBrush, 52, 58)

$g.Dispose()

$pngPath = "D:\FocusGate\src\FocusGate.Desktop\icon.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "PNG icon created: $pngPath"

# Convert to ICO using netconvert or just use the PNG directly
# For WPF, we can use the PNG as the icon
