@echo off
setlocal
set "app=%~dp0artifacts\Vibedev\Vibedev.exe"
if not exist "%app%" (
    echo Vibedev.exe was not found in the artifacts\Vibedev folder.
    pause
    exit /b 1
)
start "" /D "%~dp0artifacts\Vibedev" "%app%"
