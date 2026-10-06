@echo off
rem ---------------------------------------------------------------------------
rem  Confern (Windows) - test launcher
rem  Messages are ASCII on purpose: cmd.exe on a zh-TW system would mangle UTF-8.
rem  The human test plan lives in docs\TEST-PLAN-M1-first-run.md
rem
rem  Renamed from start-tangerine.cmd on 2026-10-05 (the product is Confern).
rem  It launches with --no-autostart so a test run NEVER writes the Windows
rem  autostart key: the desktop shortcut is the normal user path and is the
rem  only launcher that applies the StartWithWindows setting.
rem ---------------------------------------------------------------------------
setlocal
set "EXE=%~dp0..\src\Sidecar\bin\Release\net8.0-windows\Confern.exe"

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

tasklist /fi "imagename eq Confern.exe" 2>nul | find /i "Confern.exe" >nul
if not errorlevel 1 (
  echo [i] Confern.exe is already running - one UI instance at a time.
  echo     Two processes is normal ^(the UI plus its conversion worker^);
  echo     this check only prevents a SECOND UI instance.
  echo     To stop everything: taskkill /im Confern.exe /f
  echo.
  pause
  exit /b 0
)

start "" "%EXE%" --no-autostart
echo [ok] Confern started (this launcher does not touch the autostart key).
echo.
echo   How to convert: in Explorer, drag files while holding SPACE.
echo     - the wheel appears at the cursor
echo     - move to a format, then RELEASE the mouse button  ^(or press Enter^)
echo     - the converted file appears next to the original; the original is untouched
echo     - press ESC to cancel, or release SPACE to dismiss the wheel
echo.
echo   Logs:
echo     %LOCALAPPDATA%\Confern\logs\confern.log   ^(UI process^)
echo     %LOCALAPPDATA%\Confern\worker.log         ^(conversion worker; newest session^)
echo     %LOCALAPPDATA%\Confern\worker-*.log       ^(one file per worker^)
echo.
echo   After testing, run this to check for leftovers / changed originals:
echo     powershell -ExecutionPolicy Bypass -File tools\check-test-artifacts.ps1 -Folder ^<folder you converted in^>
echo.
timeout /t 8 >nul
endlocal
