@echo off
rem Builds QuestCastRx.exe using the C# compiler that ships with Windows.

setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the C# compiler ^(csc.exe^) in %WINDIR%\Microsoft.NET.
  exit /b 1
)

"%CSC%" /nologo /optimize+ /out:"%~dp0QuestCastRx.exe" "%~dp0src\QuestCastRx.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0QuestCastRx.exe
