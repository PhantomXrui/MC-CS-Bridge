@echo off
setlocal
rem Optional F8 lab tool. Requires locally generated rig tables and VS capture files.
rem Run from an x64 Native Tools Command Prompt with Windows SDK installed.
where cl >nul 2>nul
if errorlevel 1 exit /b 1
for %%I in ("%~dp0..") do set "MCCS_DEPTH_ROOT=%%~fI"
if not exist "%MCCS_DEPTH_ROOT%\build-cache\pawn-dance\replay-test" mkdir "%MCCS_DEPTH_ROOT%\build-cache\pawn-dance\replay-test"
cd /d "%MCCS_DEPTH_ROOT%\build-cache\pawn-dance\replay-test"
cl /nologo /O2 /EHsc /std:c++20 /MT "%MCCS_DEPTH_ROOT%\tools\test-pawn-depth.cpp" /Fe:pawn-depth-check.exe /link d3d11.lib d3dcompiler.lib
if errorlevel 1 exit /b 1
pawn-depth-check.exe
exit /b %errorlevel%
