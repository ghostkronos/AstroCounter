@echo off
title AstroCounter
echo AstroCounter Baslatiliyor...
if exist "publish\AstroCounter\AstroCounter.UI.exe" (
    start "" "publish\AstroCounter\AstroCounter.UI.exe"
    exit
)
dotnet run --project src/AstroCounter.UI/AstroCounter.UI.csproj

