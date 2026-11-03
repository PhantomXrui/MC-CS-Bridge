@echo off
setlocal
rem Optional F8 lab tool. Requires locally generated rig tables and VS capture files.
rem Run from an x64 Native Tools Command Prompt with Windows SDK installed.
where cl >nul 2>nul
if errorlevel 1 exit /b 1
for %%I in ("%~dp0..") do set "MCCS_CACHE_ROOT=%%~fI"
if not exist "%MCCS_CACHE_ROOT%\build-cache\pawn-dance\replay-test" mkdir "%MCCS_CACHE_ROOT%\build-cache\pawn-dance\replay-test"
cd /d "%MCCS_CACHE_ROOT%\build-cache\pawn-dance\replay-test"
cl /nologo /O2 /EHsc /std:c++20 /MT "%MCCS_CACHE_ROOT%\tools\build-dance-shader-cache.cpp" /Fe:build-dance-shader-cache.exe /link d3dcompiler.lib
if errorlevel 1 exit /b 1
build-dance-shader-cache.exe "%MCCS_CACHE_ROOT%\logs\pawn-dance-capture" "%MCCS_CACHE_ROOT%\source\cs2-bridge\native\pawn_dance_cache.h"
exit /b %errorlevel%
