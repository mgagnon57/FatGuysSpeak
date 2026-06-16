# Kill existing instances
Get-Process -Name "FatGuysSpeak*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# Start server with WPF dashboard (Windows build)
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5238"
Start-Process -FilePath "C:\FatGuysSpeak\FatGuysSpeak.Server\bin\Debug\net9.0-windows10.0.19041.0\FatGuysSpeak.Server.exe" `
    -WorkingDirectory "C:\FatGuysSpeak\FatGuysSpeak.Server" -WindowStyle Hidden
Start-Sleep -Seconds 3

# Start two clients
$client = "C:\FatGuysSpeak\FatGuysSpeak.Client\bin\Debug\net9.0-windows10.0.19041.0\win10-x64\FatGuysSpeak.Client.exe"
Start-Process $client
Start-Sleep -Milliseconds 800
Start-Process $client

Write-Host "Server (port 5238, WPF dashboard) + 2 clients launched"
