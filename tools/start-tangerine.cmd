@echo off
rem ---------------------------------------------------------------------------
rem  Tangerine Converter (Windows) - first testable build (M1d/M1e/M1f-lite)
rem  Messages are ASCII on purpose: cmd.exe on a zh-TW system would mangle UTF-8.
rem  The human test plan lives in docs\TEST-PLAN-M1-first-run.md
rem ---------------------------------------------------------------------------
setlocal
set "EXE=%~dp0..\src\Sidecar\bin\Release\net8.0-windows\Sidecar.exe"

if not exist "%EXE%" (
  echo [x] Executable not found:
  echo     %EXE%
  echo.
  echo     Build it first:
  echo       dotnet build src\Sidecar\Sidecar.csproj -c Release
  echo.
  pause
  exit /b 1
)

tasklist /fi "imagename eq Sidecar.exe" 2>nul | find /i "Sidecar.exe" >nul
if not errorlevel 1 (
  echo [i] Sidecar.exe is already running - this build is single-instance.
  echo     Close it first ^(Task Manager, or run: taskkill /im Sidecar.exe /f^)
  echo.
  pause
  exit /b 0
)

start "" "%EXE%"
echo [ok] Tangerine Converter started.
echo.
echo   How to convert: in Explorer, drag files while holding SPACE.
echo     - the wheel appears at the cursor
echo     - move to a format, then RELEASE the mouse button  ^(or press Enter^)
echo     - the converted file appears next to the original; the original is untouched
echo     - press ESC to cancel, or release SPACE to dismiss the wheel
echo.
echo   Logs:
echo     %LOCALAPPDATA%\Sidecar\logs\sidecar.log   ^(UI process^)
echo     %LOCALAPPDATA%\Sidecar\worker.log         ^(conversion worker^)
echo.
echo   After testing, run this to check for leftovers / changed originals:
echo     powershell -ExecutionPolicy Bypass -File tools\check-test-artifacts.ps1 -Folder ^<folder you converted in^>
echo.
timeout /t 8 >nul
endlocal
