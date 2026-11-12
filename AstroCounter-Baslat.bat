@echo off
title AstroCounter v1.4.1
echo AstroCounter Baslatiliyor...
if exist "release\AstroCounter-v1.4.1-portable-win-x64\AstroCounter.UI.exe" (
    start "" "release\AstroCounter-v1.4.1-portable-win-x64\AstroCounter.UI.exe"
    exit
)
if exist "publish\AstroCounter\AstroCounter.UI.exe" (
    start "" "publish\AstroCounter\AstroCounter.UI.exe"
    exit
)
dotnet run --project src/AstroCounter.UI/AstroCounter.UI.csproj -c Release

