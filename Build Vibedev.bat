@echo off
setlocal
pushd "%~dp0" || exit /b 1

echo Building portable Vibedev for Windows x64...
dotnet publish "src\Vibedev\Vibedev.csproj" -c Release -r win-x64 --self-contained true -o "artifacts\Vibedev"
set "build_exit_code=%errorlevel%"

if not "%build_exit_code%"=="0" (
    echo.
    echo Build failed with exit code %build_exit_code%.
    pause
    popd
    exit /b %build_exit_code%
)

echo.
echo Build succeeded: "%~dp0artifacts\Vibedev\Vibedev.exe"
echo Launch the app with "Run Vibedev.bat".
pause
popd
exit /b 0
