@echo off
REM Build DeepSeekTray.exe with the in-box .NET Framework compiler - no SDK, no
REM NuGet, no toolchain install. Every Windows 10/11 machine already has csc.exe.
REM
REM Output goes to %1, or to <repo>\bin when no argument is given.
REM
REM NOTE: this file is deliberately pure ASCII. cmd.exe reads batch files in the
REM console's OEM codepage, so non-ASCII text here breaks the parser on any
REM machine whose codepage differs from the one that wrote the file.
setlocal
set "ROOT=%~dp0.."
set "OUTDIR=%~1"
if "%OUTDIR%"=="" set "OUTDIR=%ROOT%\bin"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: .NET Framework compiler csc.exe not found.
  echo Enable the .NET Framework 4.x feature and try again.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"%ROOT%\assets\app.ico" /out:"%OUTDIR%\DeepSeekTray.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll "%ROOT%\src\DeepSeekTray.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

copy /y "%ROOT%\assets\tray.ico" "%OUTDIR%\tray.ico" >nul
copy /y "%ROOT%\assets\tray-off.ico" "%OUTDIR%\tray-off.ico" >nul
copy /y "%ROOT%\assets\app.ico" "%OUTDIR%\app.ico" >nul
echo Built "%OUTDIR%\DeepSeekTray.exe"
endlocal
