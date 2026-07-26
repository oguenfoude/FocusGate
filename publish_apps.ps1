$ErrorActionPreference = "Stop"

Write-Host "Publishing Alaafi..."
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/alaafi
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/alaafi-dashboard

Write-Host "Publishing FlexiDZ..."
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/flixiDz
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/flixiDz-dashboard

Write-Host "Copying Dashboard files to Alaafi dist..."
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.exe dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.dll dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.pdb dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.deps.json dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.runtimeconfig.json dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\FocusGate.Dashboard.staticwebassets.endpoints.json dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\appsettings.json dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\web.config dist\alaafi\ -Force
Copy-Item dist\alaafi-dashboard\en dist\alaafi\en -Recurse -Force
Copy-Item dist\alaafi-dashboard\fr dist\alaafi\fr -Recurse -Force
Copy-Item dist\alaafi-dashboard\ar dist\alaafi\ar -Recurse -Force
Copy-Item dist\alaafi-dashboard\wwwroot dist\alaafi\wwwroot -Recurse -Force

Write-Host "Copying Dashboard files to FlexiDZ dist..."
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.exe dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.dll dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.pdb dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.deps.json dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.runtimeconfig.json dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\FocusGate.Dashboard.staticwebassets.endpoints.json dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\appsettings.json dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\web.config dist\flixiDz\ -Force
Copy-Item dist\flixiDz-dashboard\en dist\flixiDz\en -Recurse -Force
Copy-Item dist\flixiDz-dashboard\fr dist\flixiDz\fr -Recurse -Force
Copy-Item dist\flixiDz-dashboard\ar dist\flixiDz\ar -Recurse -Force
Copy-Item dist\flixiDz-dashboard\wwwroot dist\flixiDz\wwwroot -Recurse -Force

Write-Host "Done!"
