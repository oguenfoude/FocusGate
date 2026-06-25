$key = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
$pubXml = $key.ToXmlString($false)
$privXml = $key.ToXmlString($true)
New-Item -ItemType Directory -Force -Path 'D:\FocusGate\tools' | Out-Null
Set-Content -Path 'D:\FocusGate\tools\private_key.xml' -Value $privXml
Set-Content -Path 'D:\FocusGate\tools\public_key.xml' -Value $pubXml
Write-Output "Keys generated successfully"
Write-Output "PUBLIC KEY:"
Write-Output $pubXml
