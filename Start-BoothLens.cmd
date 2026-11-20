@echo off
setlocal
cd /d "%~dp0"
title BoothLens - Keep this window open
if exist "BoothLens.exe" (
  "BoothLens.exe" --open
  goto :stopped
)
if exist "release\BoothLens.exe" (
  "release\BoothLens.exe" --open
  goto :stopped
)
set "SDK=%LOCALAPPDATA%\dotnet-sdk\dotnet.exe"
if not exist "%SDK%" set "SDK=dotnet"
"%SDK%" build src\BoothLens.App\BoothLens.App.csproj --nologo
if errorlevel 1 (
  pause
  exit /b 1
)
"%SDK%" src\BoothLens.App\bin\Debug\net8.0\BoothLens.dll --open
:stopped
echo.
echo BoothLens stopped. Downloads and imports require the app to be running.
echo If startup failed, read the error above. Press any key to close.
pause
