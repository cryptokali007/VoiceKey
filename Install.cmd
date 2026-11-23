@echo off
setlocal
cd /d "%~dp0"
set "source=%~dp0VoiceKey.exe"
if exist "%source%" goto install
call "%~dp0build.cmd"
if errorlevel 1 goto failed
set "source=%~dp0dist\VoiceKey.exe"
:install
set "appdir=%LOCALAPPDATA%\Programs\VoiceKey"
if not exist "%appdir%" mkdir "%appdir%"
copy /y "%source%" "%appdir%\VoiceKey.exe" >nul
if errorlevel 1 goto failed
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([IO.Path]::Combine([Environment]::GetFolderPath('Programs'),'VoiceKey.lnk')); $s.TargetPath=[IO.Path]::Combine($env:LOCALAPPDATA,'Programs','VoiceKey','VoiceKey.exe'); $s.Save()"
if errorlevel 1 goto failed
start "" "%appdir%\VoiceKey.exe"
exit /b 0
:failed
echo Install could not finish. Close any running copy of VoiceKey and try again.
pause
exit /b 1
