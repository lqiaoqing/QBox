@echo off
setlocal
for /f "delims=" %%i in ('rustc --print sysroot') do set "SYSROOT=%%i"
"%SYSROOT%\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained\x86_64-w64-mingw32-gcc.exe" %*
