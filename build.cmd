@echo off
rem Builds LanTalk.exe (app) and lt.exe (command line for Claude Code) with the compiler that ships with Windows.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Core.dll
"%CSC%" /nologo /codepage:65001 /optimize /target:winexe /win32icon:LanTalk.ico /resource:LanTalk.ico /out:LanTalk.exe %REFS% Core.cs Lang.cs Control.cs Ipc.cs Look.cs ControlUi.cs Ui.cs || exit /b 1
"%CSC%" /nologo /codepage:65001 /optimize /target:exe /out:lt.exe %REFS% Core.cs Lang.cs Control.cs Ipc.cs Cli.cs || exit /b 1
echo Build OK
