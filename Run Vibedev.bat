@echo off
setlocal
set "app=%~dp0release\Vibedev.exe"
if not exist "%app%" (
    echo Vibedev.exe was not found in the release folder.
    pause
    exit /b 1
)
start "" /D "%~dp0release" "%app%"
