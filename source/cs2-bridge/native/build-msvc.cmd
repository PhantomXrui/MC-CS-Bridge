@echo off
setlocal
rem Run from an x64 Native Tools Command Prompt for Visual Studio (C++ workload + Windows SDK).
where cl >nul 2>nul
if errorlevel 1 (
 echo ERROR: MSVC cl.exe not found. Open an x64 Native Tools Command Prompt.
 exit /b 1
)
for %%I in ("%~dp0..\..\..") do set "MCCS_BUILD_ROOT=%%~fI"
if not defined ReShadeIncludeDir set "ReShadeIncludeDir=%MCCS_BUILD_ROOT%\source\cs2-bridge\third_party\reshade"
if not exist "%ReShadeIncludeDir%\reshade.hpp" (
 echo ERROR: Set ReShadeIncludeDir to the ReShade SDK include directory containing reshade.hpp.
 exit /b 1
)
set "MCCS_DANCE_DEFINE="
if "%MCCS_ENABLE_EXPERIMENTAL_DANCE%"=="1" (
 for %%F in (pawn_dance_cache.h pawn_dance_agent_data.h pawn_dance_weapon_data.h pawn_dance_glove_data.h) do (
  if not exist "%~dp0%%F" (
   echo ERROR: Experimental F8 requires locally generated %%F.
   exit /b 1
  )
 )
 set "MCCS_DANCE_DEFINE=/DMCCS_ENABLE_EXPERIMENTAL_DANCE"
)
if not exist "%MCCS_BUILD_ROOT%\source\cs2-bridge\build" mkdir "%MCCS_BUILD_ROOT%\source\cs2-bridge\build"
cd /d "%MCCS_BUILD_ROOT%\source\cs2-bridge\build"
cl /nologo /LD /O2 /EHsc /std:c++20 /MT /W3 /DWIN32_LEAN_AND_MEAN /DNOMINMAX %MCCS_DANCE_DEFINE% ^
 /I "%ReShadeIncludeDir%" ^
 "%MCCS_BUILD_ROOT%\source\cs2-bridge\native\adapter.cpp" ^
 "%MCCS_BUILD_ROOT%\source\cs2-bridge\native\compositor.cpp" ^
 /Fe:MinecraftCS2.addon64 /link user32.lib shell32.lib d3dcompiler.lib
exit /b %errorlevel%
