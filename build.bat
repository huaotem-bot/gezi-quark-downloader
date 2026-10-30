@echo off
rem Build GeZi from source and update the runtime folder (app\) in place.
rem Data files (config/state/accounts/Download) in app\ are NEVER touched.
cd /d "%~dp0"
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
