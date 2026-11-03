@echo off
setlocal
where cl >nul 2>nul
if errorlevel 1 exit /b 1
for %%I in ("%~dp0..\..") do set "MCCS_TEST_ROOT=%%~fI"
if not exist "%MCCS_TEST_ROOT%\build-cache\public-tests" mkdir "%MCCS_TEST_ROOT%\build-cache\public-tests"
pushd "%MCCS_TEST_ROOT%\build-cache\public-tests"
cl /nologo /O2 /EHsc /std:c++20 /MT "%~dp0readback-queue-check.cpp" /Fe:readback-queue-check.exe /link d3d11.lib
if errorlevel 1 (popd & exit /b 1)
readback-queue-check.exe
set "MCCS_TEST_RESULT=%errorlevel%"
popd
exit /b %MCCS_TEST_RESULT%
