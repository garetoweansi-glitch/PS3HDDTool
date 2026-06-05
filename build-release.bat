@echo off
REM Build script for PS3HDDTool Release (Windows)

echo PS3 HDD Tool - Release Build Script (Windows)
echo ==========================================
echo.

REM Check if dotnet is installed
where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: .NET SDK not found. Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Step 1: Restore NuGet packages...
dotnet restore
if errorlevel 1 (
    echo ERROR: Restore failed
    pause
    exit /b 1
)

echo.
echo Step 2: Building Release configuration...
dotnet publish PS3HddTool.Avalonia\PS3HddTool.Avalonia.csproj ^
    -c Release ^
    -o publish ^
    --no-restore
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo.
echo Step 3: Creating archive...
cd publish
if exist "C:\Program Files\7-Zip\7z.exe" (
    "C:\Program Files\7-Zip\7z.exe" a -tzip PS3HDDTool-Release.zip *
) else (
    echo WARNING: 7-Zip not found. Skipping archive creation.
    echo Download 7-Zip from https://www.7-zip.org/
)
cd ..

echo.
echo.
echo ============================================
echo Build complete!
echo Output: ./publish/
echo ============================================
echo.
echo To download the release:
echo 1. Go to: https://github.com/garetoweansi-glitch/PS3HDDTool
echo 2. Click the "Releases" section on the right
echo 3. Download PS3HDDTool-Release.zip
echo.
echo Or tag for automated GitHub release:
echo 1. git tag -a v1.0.0 -m "Release v1.0.0"
echo 2. git push origin v1.0.0
echo.
pause
