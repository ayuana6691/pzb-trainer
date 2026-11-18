@echo off
rem One-click packaging:
rem   self-contained .NET 8 runtime + embedded bridge -> single exe, double-click to run.
rem Requires the .NET 8 SDK; pack.ps1 prints the install steps when it is missing.
rem Keep this file ASCII-only: cmd.exe mis-parses UTF-8 batch files after chcp 65001.
setlocal
pushd "%~dp0"

where pwsh >nul 2>nul
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -File "pack.ps1" %*
) else (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "pack.ps1" %*
)
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo [OK] out\release\PBZTrainer.exe   single file, double-click to run
) else (
  echo [FAIL] packaging failed, exit code %RC% -- see the log above
)

popd
if /i not "%CI%"=="true" pause
endlocal & exit /b %RC%
