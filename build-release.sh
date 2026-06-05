#!/bin/bash
# Build script for PS3HDDTool Release

echo "PS3 HDD Tool - Release Build Script"
echo "===================================="
echo ""

# Check if dotnet is installed
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found. Please install .NET 8.0 SDK"
    exit 1
fi

echo "Step 1: Restore NuGet packages..."
dotnet restore

echo ""
echo "Step 2: Building Release configuration..."
dotnet publish PS3HddTool.Avalonia/PS3HddTool.Avalonia.csproj \
    -c Release \
    -o publish \
    --no-restore

echo ""
echo "Step 3: Creating archive..."
if command -v 7z &> /dev/null; then
    cd publish
    7z a -tzip PS3HDDTool-Release.zip *
    cd ..
elif command -v zip &> /dev/null; then
    cd publish
    zip -r PS3HDDTool-Release.zip *
    cd ..
else
    echo "WARNING: 7z or zip not found. Skipping archive creation."
fi

echo ""
echo "Build complete!"
echo "Output: ./publish/"
echo ""
echo "To create a GitHub release:"
echo "1. Tag your commit: git tag -a v1.0.0 -m 'Release v1.0.0'"
echo "2. Push tag: git push origin v1.0.0"
echo "3. The GitHub Actions workflow will automatically build and release"
