@echo off
rem Builds TrayAlways.exe with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
setlocal EnableDelayedExpansion
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set ROOT=%~dp0
set OUT=%ROOT%bin
if not exist "%OUT%" mkdir "%OUT%"

rem Every lang\<culture>.txt becomes the embedded resource "lang.<culture>".
set RES=
for %%f in ("%ROOT%lang\*.txt") do set RES=!RES! "/resource:%%~ff,lang.%%~nf"

set COMMON=/nologo /optimize+ /platform:anycpu /target:winexe /codepage:65001 /win32manifest:"%ROOT%src\app.manifest" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" !RES!

rem Stage 1: build without an icon, use it to render the icon, then build the final exe with it.
"%CSC%" %COMMON% /out:"%OUT%\TrayAlways.stage1.exe" "%ROOT%src\TrayAlways.cs" || exit /b 1
"%OUT%\TrayAlways.stage1.exe" --write-icon "%OUT%\TrayAlways.ico" >nul || exit /b 1
"%CSC%" %COMMON% /win32icon:"%OUT%\TrayAlways.ico" /out:"%OUT%\TrayAlways.exe" "%ROOT%src\TrayAlways.cs" || exit /b 1
del "%OUT%\TrayAlways.stage1.exe"
echo Built %OUT%\TrayAlways.exe
