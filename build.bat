@echo off
rem Build GeZi from source and update the runtime folder (app\) in place.
rem Data files (config/state/accounts/Download) in app\ are NEVER touched.
rem NOTE: keep this file ASCII-only. cmd.exe parses .bat in the ANSI codepage
rem       (GBK on zh-CN); UTF-8 Chinese comments get misread and break the script.
cd /d "%~dp0"

rem A running GeZi.exe locks app\GeZi.exe, so robocopy cannot overwrite it and
rem the user would keep launching the old build (seen 2026-10-31). Close it
rem first: graceful WM_CLOSE, then force after 2s. Download progress lives in
rem .qmeta/.qparts and resumes on next start, so a force kill loses nothing.
tasklist /fi "imagename eq GeZi.exe" 2>nul | find /i "GeZi.exe" >nul
if not errorlevel 1 (
    echo === GeZi.exe is running, closing it first ===
    taskkill /im GeZi.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
    taskkill /f /im GeZi.exe >nul 2>&1
)

echo === Building GeZi (Release) ===
dotnet build GeZi.slnx -c Release -v minimal --nologo
if errorlevel 1 goto :fail
echo === Syncing build output to app\ ===
robocopy "src\GeZi\bin\Release\net48" "app" /E /NFL /NDL /NJH /NJS /XF GeZi.config.xml GeZi.state.json >nul
if errorlevel 8 goto :fail
echo === Done. EXE: %~dp0app\GeZi.exe ===
exit /b 0
:fail
echo === BUILD FAILED ===
exit /b 1
