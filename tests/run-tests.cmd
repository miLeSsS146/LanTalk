@echo off
rem Loopback only (127.0.0.1), uses a temp folder, doesn't touch the real LanTalk data.
cd /d "%~dp0.."
call "%~dp0..\build.cmd" || exit /b 1
set WORK=%TEMP%\LanTalkTest-%RANDOM%%RANDOM%
mkdir "%WORK%"
"%CSC%" /nologo /codepage:65001 /main:LanTalk.Test /resource:LanTalk.ico /out:"%WORK%\test.exe" %REFS% Core.cs Control.cs Ipc.cs Look.cs ControlUi.cs Ui.cs tests\Test.cs tests\ControlTest.cs || exit /b 1
"%WORK%\test.exe" "%WORK%\data" "%CD%\lt.exe" %1
set RC=%ERRORLEVEL%
rmdir /s /q "%WORK%"
exit /b %RC%
