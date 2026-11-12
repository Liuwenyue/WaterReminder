@echo off
setlocal
cd /d "%~dp0"
dotnet run --project ".\WaterReminder.Client\WaterReminder.Client.csproj" -c Release -- %*
