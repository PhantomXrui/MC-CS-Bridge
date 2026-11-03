@echo off
setlocal
call "D:\MC-CS-Bridge\runtime\VSBuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cd /d "D:\MC-CS-Bridge\source\cs2-bridge\build"
cl /nologo /O2 /EHsc /std:c++20 /MT /Fe:pose_checks.exe "..\native\pose_checks.cpp"
if errorlevel 1 exit /b 1
pose_checks.exe
