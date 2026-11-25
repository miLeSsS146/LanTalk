@echo off
rem Loopback only (127.0.0.1), uses a temp folder, doesn't touch the real LanTalk data.
cd /d "%~dp0.."
call "%~dp0..\build.cmd" || exit /b 1
set WORK=%TEMP%\LanTalkTest-%RANDOM%%RANDOM%
mkdir "%WORK%"
"%CSC%" /nologo /codepage:65001 /main:LanTalk.Test %RES% /out:"%WORK%\test.exe" %REFS% Core.cs Lang.cs Control.cs Remote.cs Work.cs Input.cs Ipc.cs Look.cs ControlUi.cs InputUi.cs Ui.cs tests\Test.cs tests\ControlTest.cs tests\RemoteTest.cs tests\WorkTest.cs tests\InputTest.cs tests\LangTest.cs || exit /b 1
"%WORK%\test.exe" "%WORK%\data" "%CD%\lt.exe" %1
set RC=%ERRORLEVEL%
rmdir /s /q "%WORK%"
exit /b %RC%
