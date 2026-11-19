@echo off
setlocal
cd /d "%~dp0"
if exist "BoothLens.exe" (
  start "BoothLens" "BoothLens.exe" --open
  exit /b 0
)
if exist "release\BoothLens.exe" (
  start "BoothLens" "release\BoothLens.exe" --open
  exit /b 0
)
set "SDK=%LOCALAPPDATA%\dotnet-sdk\dotnet.exe"
if not exist "%SDK%" set "SDK=dotnet"
"%SDK%" build src\BoothLens.App\BoothLens.App.csproj --nologo
if errorlevel 1 (
  pause
  exit /b 1
)
"%SDK%" src\BoothLens.App\bin\Debug\net8.0\BoothLens.dll --open
pause
