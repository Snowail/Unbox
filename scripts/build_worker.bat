@echo off
call "C:\WorkSpace\RoboCopyUI\.tools\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1

cl.exe /nologo /O2 /std:c++20 /EHsc /DUNICODE /D_UNICODE /I"C:\WorkSpace\unzip\src\UnzipWorker" ^
    "C:\WorkSpace\unzip\src\UnzipWorker\main.cpp" ^
    /Fe"C:\WorkSpace\unzip\bin\UnzipWorker.exe" ^
    /link ole32.lib oleaut32.lib bcrypt.lib advapi32.lib shell32.lib

if %errorlevel% neq 0 (
    echo [ERROR] Build failed!
    exit /b %errorlevel%
)
echo [OK] UnzipWorker.exe built successfully!
