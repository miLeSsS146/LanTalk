@echo off
rem Builds LanTalk.exe (app) and lt.exe (command line for Claude Code) with the compiler that ships with Windows.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set REFS=/r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Core.dll /r:%WPF%\UIAutomationClient.dll /r:%WPF%\UIAutomationTypes.dll /r:%WPF%\WindowsBase.dll
set RES=/resource:LanTalk.ico /resource:icon\devices\tower-mono.svg,tower.svg /resource:icon\devices\mini-mono.svg,mini.svg /resource:icon\devices\laptop-mono.svg,laptop.svg
"%CSC%" /nologo /codepage:65001 /optimize /target:winexe /win32icon:LanTalk.ico %RES% /out:LanTalk.exe %REFS% Core.cs Lang.cs Control.cs Remote.cs Work.cs Input.cs Ipc.cs Look.cs ControlUi.cs InputUi.cs Ui.cs || exit /b 1
"%CSC%" /nologo /codepage:65001 /optimize /target:exe /out:lt.exe %REFS% Core.cs Lang.cs Control.cs Remote.cs Work.cs Input.cs Ipc.cs Cli.cs Mcp.cs || exit /b 1
echo Build OK
