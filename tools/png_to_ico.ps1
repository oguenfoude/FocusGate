Add-Type -AssemblyName System.Drawing

$pngPath = "D:\FocusGate\src\FocusGate.Desktop\icon.png"
$icoPath = "D:\FocusGate\src\FocusGate.Desktop\icon.ico"

# Create icon from bitmap
$bmp = [System.Drawing.Bitmap]::FromFile($pngPath)
$hIcon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)

$fs = [System.IO.File]::Create($icoPath)
$icon.Save($fs)
$fs.Close()

$icon.Dispose()
$bmp.Dispose()

$icoSize = (Get-Item $icoPath).Length
Write-Output "ICO created: $icoPath ($icoSize bytes)"
