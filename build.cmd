@echo off
setlocal
cd /d "%~dp0"
if not exist dist mkdir dist
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" (
  echo Windows x64 and .NET Framework 4.8 or newer are required.
  exit /b 1
)
"%compiler%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /out:dist\VoiceKey.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll src\*.cs
exit /b %errorlevel%
