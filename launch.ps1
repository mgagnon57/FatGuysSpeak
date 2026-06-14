# Kill existing instances
Get-Process -Name "FatGuysSpeak*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# Start server with correct environment
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "C:\FatGuysSpeak\FatGuysSpeak.Server\bin\Debug\net9.0\FatGuysSpeak.Server.exe"
$psi.WorkingDirectory = "C:\FatGuysSpeak\FatGuysSpeak.Server"
$psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development"
$psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://localhost:5238"
$psi.UseShellExecute = $false
[System.Diagnostics.Process]::Start($psi) | Out-Null
Start-Sleep -Seconds 2

# Start two clients
$client = "C:\FatGuysSpeak\FatGuysSpeak.Client\bin\Debug\net9.0-windows10.0.19041.0\win10-x64\FatGuysSpeak.Client.exe"
Start-Process $client
Start-Sleep -Milliseconds 800
Start-Process $client

Write-Host "Server (port 5238) + 2 clients launched"
