@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 9 SDK est requis. Telechargez-le depuis https://dotnet.microsoft.com/download/dotnet/9.0
  pause
  exit /b 1
)
dotnet run --project src\PZAdvancedServerManager.App --configuration Release -- --open-browser
endlocal
