@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "ROOT=%~dp0.."
set "RES=/res:"%~dp0DiskMonitor.ps1",engine.ps1 /res:"%~dp0Update-Task.ps1",updatetask.ps1 /res:"%~dp0config.json",defaultconfig.json"
if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:"%ROOT%\bin\DiskMonCli.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll %RES% "%~dp0Common.cs" "%~dp0Cli.cs"
if errorlevel 1 exit /b 1
echo CLI-OK
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%ROOT%\bin\DiskMonitorGUI.exe" /win32manifest:"%~dp0app.manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll %RES% "%~dp0Common.cs" "%~dp0GUI.cs"
if errorlevel 1 exit /b 1
echo GUI-OK
