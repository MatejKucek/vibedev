@echo off
setlocal
pushd "%~dp0" || exit /b 1

set "dirty="
for /f "delims=" %%i in ('git status --porcelain') do set "dirty=1"

set "pull_exit_code=0"
if defined dirty goto skip_pull
echo Pulling latest changes with rebase...
git pull -r
set "pull_exit_code=%errorlevel%"
goto after_pull
:skip_pull
echo Local changes detected, skipping pull.
:after_pull

if not "%pull_exit_code%"=="0" (
    echo.
    echo Pull failed with exit code %pull_exit_code%.
    pause
    popd
    exit /b %pull_exit_code%
)

echo.
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
echo Build succeeded. Starting Vibedev...
start "" /D "%~dp0artifacts\Vibedev" "%~dp0artifacts\Vibedev\Vibedev.exe"
popd
exit /b 0
