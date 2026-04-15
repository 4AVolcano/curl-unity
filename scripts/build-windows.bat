@echo off
REM ============================================================
REM libcurl Windows build script (MSVC)
REM
REM Usage:
REM   Run from "x64 Native Tools Command Prompt" for 64-bit
REM   Run from "x86 Native Tools Command Prompt" for 32-bit
REM
REM Options:
REM   --clean              Clean build directory before building
REM   --win-min <hex>      Minimum Windows version (default: 0x0601 = Win7 SP1)
REM
REM Prerequisites:
REM   Visual Studio 2017+ (C++ desktop workload)
REM   CMake, Ninja, Perl (for OpenSSL), Git
REM ============================================================
setlocal enabledelayedexpansion

REM ============================================================
REM Project paths
REM ============================================================
set "SCRIPT_DIR=%~dp0"
for %%i in ("%SCRIPT_DIR%\..") do set "PROJECT_ROOT=%%~fi"
set "DEPS_SRC=%PROJECT_ROOT%\deps"
set "CURL_SRC=%DEPS_SRC%\curl"
set "BRIDGE_SRC=%PROJECT_ROOT%\bridge\curl_unity_bridge.c"
set "OUTPUT_DIR=%PROJECT_ROOT%\output"

REM ============================================================
REM Minimum Windows version (default: Windows 7 SP1)
REM ============================================================
set "WIN_MIN_VER=0x0601"

REM ============================================================
REM Detect MSVC target architecture
REM ============================================================
set "ARCH="

REM Method 1: VSCMD_ARG_TGT_ARCH (set by vcvarsall.bat, locale-independent)
if "%VSCMD_ARG_TGT_ARCH%"=="x64" set "ARCH=x64"
if "%VSCMD_ARG_TGT_ARCH%"=="x86" set "ARCH=x86"

REM Method 2: parse cl.exe output (fallback, works across locales)
if not defined ARCH (
    cl 2>&1 | findstr /C:"x64" >nul 2>&1 && set "ARCH=x64"
)
if not defined ARCH (
    cl 2>&1 | findstr /C:"x86" >nul 2>&1 && set "ARCH=x86"
)

if not defined ARCH (
    echo [ERROR] Cannot detect MSVC target architecture.
    echo Run this script from "x64 Native Tools Command Prompt" or "x86 Native Tools Command Prompt".
    exit /b 1
)

if "%ARCH%"=="x64" (
    set "PLATFORM=windows-x64"
    set "OPENSSL_TARGET=VC-WIN64A"
    set "OUTPUT_ARCH=x86_64"
) else if "%ARCH%"=="x86" (
    set "PLATFORM=windows-x86"
    set "OPENSSL_TARGET=VC-WIN32"
    set "OUTPUT_ARCH=x86"
) else (
    echo [ERROR] Unsupported architecture: %ARCH%
    exit /b 1
)

set "PREFIX=%PROJECT_ROOT%\build\%PLATFORM%\install"

echo ========================================
echo   libcurl Windows build
echo   Arch: %ARCH% (%PLATFORM%)
echo   Min:  %WIN_MIN_VER%
echo   Root: %PROJECT_ROOT%
echo ========================================

REM ============================================================
REM Parse options
REM ============================================================
set "CLEAN=0"
set "_NEXT_IS_WINMIN=0"
for %%a in (%*) do (
    if "!_NEXT_IS_WINMIN!"=="1" (
        set "WIN_MIN_VER=%%a"
        set "_NEXT_IS_WINMIN=0"
    ) else if "%%a"=="--clean" (
        set "CLEAN=1"
    ) else if "%%a"=="--win-min" (
        set "_NEXT_IS_WINMIN=1"
    )
)
if "%CLEAN%"=="1" (
    echo Cleaning build\%PLATFORM% ...
    if exist "%PROJECT_ROOT%\build\%PLATFORM%" rd /s /q "%PROJECT_ROOT%\build\%PLATFORM%"
)

if not exist "%PREFIX%\lib" mkdir "%PREFIX%\lib"
if not exist "%PREFIX%\include" mkdir "%PREFIX%\include"

REM ============================================================
REM Build zlib
REM ============================================================
if exist "%PREFIX%\lib\zs.lib" (
    echo [SKIP] zlib already built
    goto :build_openssl
)

echo.
echo ========================================
echo   [%PLATFORM%] Building zlib
echo ========================================
echo.

set "ZLIB_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\zlib"
if exist "%ZLIB_BUILD%" rd /s /q "%ZLIB_BUILD%"

cmake -B "%ZLIB_BUILD%" -S "%DEPS_SRC%\zlib" -G Ninja ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCMAKE_INSTALL_PREFIX="%PREFIX%" ^
    -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
    -DBUILD_SHARED_LIBS=OFF
if errorlevel 1 goto :error

cmake --build "%ZLIB_BUILD%"
if errorlevel 1 goto :error

cmake --install "%ZLIB_BUILD%"
if errorlevel 1 goto :error

echo   zlib done.

REM ============================================================
REM Build OpenSSL
REM ============================================================
:build_openssl
if exist "%PREFIX%\lib\libssl.lib" if exist "%PREFIX%\lib\libcrypto.lib" (
    echo [SKIP] OpenSSL already built
    goto :build_nghttp2
)

echo.
echo ========================================
echo   [%PLATFORM%] Building OpenSSL
echo ========================================
echo.

set "OPENSSL_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\openssl"
if exist "%OPENSSL_BUILD%" rd /s /q "%OPENSSL_BUILD%"
mkdir "%OPENSSL_BUILD%"
pushd "%OPENSSL_BUILD%"

perl "%DEPS_SRC%\openssl\Configure" %OPENSSL_TARGET% ^
    --prefix=%PREFIX% ^
    --libdir=lib ^
    no-shared ^
    no-tests ^
    no-apps ^
    /FS ^
    -D_WIN32_WINNT=%WIN_MIN_VER%
if errorlevel 1 ( popd & goto :error )

REM Use jom for parallel compilation, nmake for install (jom has
REM file-rename conflicts with OpenSSL's depend/install targets)
set "MAKE_CMD=nmake"
where jom >nul 2>&1 && set "MAKE_CMD=jom -j%NUMBER_OF_PROCESSORS%"
echo   Build tool: %MAKE_CMD%

%MAKE_CMD% build_sw
if errorlevel 1 ( popd & goto :error )

nmake install_sw
if errorlevel 1 ( popd & goto :error )

popd
echo   OpenSSL done.

REM ============================================================
REM Build nghttp2
REM ============================================================
:build_nghttp2
if exist "%PREFIX%\lib\nghttp2.lib" (
    echo [SKIP] nghttp2 already built
    goto :build_nghttp3
)

echo.
echo ========================================
echo   [%PLATFORM%] Building nghttp2
echo ========================================
echo.

REM nghttp2: ENABLE_LIB_ONLY mode does not need nested submodules

set "NGHTTP2_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\nghttp2"
if exist "%NGHTTP2_BUILD%" rd /s /q "%NGHTTP2_BUILD%"

cmake -B "%NGHTTP2_BUILD%" -S "%DEPS_SRC%\nghttp2" -G Ninja ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCMAKE_INSTALL_PREFIX="%PREFIX%" ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON ^
    -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
    -DENABLE_LIB_ONLY=ON ^
    -DBUILD_TESTING=OFF ^
    -DBUILD_SHARED_LIBS=OFF ^
    -DBUILD_STATIC_LIBS=ON
if errorlevel 1 goto :error

cmake --build "%NGHTTP2_BUILD%"
if errorlevel 1 goto :error

cmake --install "%NGHTTP2_BUILD%"
if errorlevel 1 goto :error

echo   nghttp2 done.

REM ============================================================
REM Build nghttp3
REM ============================================================
:build_nghttp3
if exist "%PREFIX%\lib\nghttp3.lib" (
    echo [SKIP] nghttp3 already built
    goto :build_ngtcp2
)

echo.
echo ========================================
echo   [%PLATFORM%] Building nghttp3
echo ========================================
echo.

call :ensure_submodules "%DEPS_SRC%\nghttp3" "lib\sfparse"

set "NGHTTP3_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\nghttp3"
if exist "%NGHTTP3_BUILD%" rd /s /q "%NGHTTP3_BUILD%"

cmake -B "%NGHTTP3_BUILD%" -S "%DEPS_SRC%\nghttp3" -G Ninja ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCMAKE_INSTALL_PREFIX="%PREFIX%" ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON ^
    -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
    -DENABLE_LIB_ONLY=ON ^
    -DBUILD_TESTING=OFF ^
    -DENABLE_SHARED_LIB=OFF ^
    -DENABLE_STATIC_LIB=ON
if errorlevel 1 goto :error

cmake --build "%NGHTTP3_BUILD%"
if errorlevel 1 goto :error

cmake --install "%NGHTTP3_BUILD%"
if errorlevel 1 goto :error

echo   nghttp3 done.

REM ============================================================
REM Build ngtcp2
REM ============================================================
:build_ngtcp2
if exist "%PREFIX%\lib\ngtcp2.lib" (
    echo [SKIP] ngtcp2 already built
    goto :build_curl
)

echo.
echo ========================================
echo   [%PLATFORM%] Building ngtcp2
echo ========================================
echo.

REM ngtcp2: ENABLE_LIB_ONLY mode does not need nested submodules

set "NGTCP2_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\ngtcp2"
if exist "%NGTCP2_BUILD%" rd /s /q "%NGTCP2_BUILD%"

cmake -B "%NGTCP2_BUILD%" -S "%DEPS_SRC%\ngtcp2" -G Ninja ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCMAKE_INSTALL_PREFIX="%PREFIX%" ^
    -DCMAKE_PREFIX_PATH="%PREFIX%" ^
    -DOPENSSL_ROOT_DIR="%PREFIX%" ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON ^
    -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
    -DENABLE_OPENSSL=ON ^
    -DENABLE_LIB_ONLY=ON ^
    -DBUILD_TESTING=OFF ^
    -DENABLE_SHARED_LIB=OFF ^
    -DENABLE_STATIC_LIB=ON
if errorlevel 1 goto :error

cmake --build "%NGTCP2_BUILD%"
if errorlevel 1 goto :error

cmake --install "%NGTCP2_BUILD%"
if errorlevel 1 goto :error

echo   ngtcp2 done.

REM ============================================================
REM Build libcurl (static)
REM ============================================================
:build_curl
echo.
echo ========================================
echo   [%PLATFORM%] Building libcurl
echo ========================================
echo.

REM Clean cmake package configs that conflict with curl's FindXXX modules
if exist "%PREFIX%\lib\cmake\nghttp2" rd /s /q "%PREFIX%\lib\cmake\nghttp2"
if exist "%PREFIX%\lib\cmake\nghttp3" rd /s /q "%PREFIX%\lib\cmake\nghttp3"
if exist "%PREFIX%\lib\cmake\ngtcp2" rd /s /q "%PREFIX%\lib\cmake\ngtcp2"

set "CURL_BUILD=%PROJECT_ROOT%\build\%PLATFORM%\curl"
if exist "%CURL_BUILD%" rd /s /q "%CURL_BUILD%"

cmake -B "%CURL_BUILD%" -S "%CURL_SRC%" -G Ninja ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCMAKE_INSTALL_PREFIX="%PREFIX%" ^
    -DCMAKE_PREFIX_PATH="%PREFIX%" ^
    -DOPENSSL_ROOT_DIR="%PREFIX%" ^
    -DBUILD_CURL_EXE=OFF ^
    -DBUILD_SHARED_LIBS=OFF ^
    -DBUILD_STATIC_LIBS=ON ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON ^
    -DCMAKE_C_FLAGS="/DNGHTTP2_STATICLIB /DNGTCP2_STATICLIB /DNGHTTP3_STATICLIB /D_WIN32_WINNT=%WIN_MIN_VER%" ^
    -DCURL_TARGET_WINDOWS_VERSION=%WIN_MIN_VER% ^
    -DCURL_ENABLE_SSL=ON ^
    -DCURL_USE_OPENSSL=ON ^
    -DUSE_NGHTTP2=ON ^
    -DUSE_NGTCP2=ON ^
    -DHTTP_ONLY=ON ^
    -DCURL_USE_LIBPSL=OFF ^
    -DCURL_USE_LIBSSH2=OFF ^
    -DCURL_USE_LIBSSH=OFF ^
    -DCURL_BROTLI=OFF ^
    -DCURL_ZSTD=OFF ^
    -DUSE_LIBIDN2=OFF ^
    -DCURL_DISABLE_LDAP=ON ^
    -DCURL_DISABLE_LDAPS=ON ^
    -DCURL_DISABLE_AWS=ON ^
    -DCURL_DISABLE_KERBEROS_AUTH=ON ^
    -DCURL_DISABLE_NEGOTIATE_AUTH=ON ^
    -DCURL_DISABLE_VERBOSE_STRINGS=OFF ^
    -DCURL_LTO=ON
if errorlevel 1 goto :error

cmake --build "%CURL_BUILD%"
if errorlevel 1 goto :error

cmake --install "%CURL_BUILD%"
if errorlevel 1 goto :error

echo   libcurl done.

REM ============================================================
REM Build bridge + collect output -> libcurl_unity.dll
REM ============================================================
echo.
echo ========================================
echo   [%PLATFORM%] Building bridge + DLL
echo ========================================
echo.

set "DLL_OUT=%OUTPUT_DIR%\Windows\%OUTPUT_ARCH%"
if not exist "%DLL_OUT%" mkdir "%DLL_OUT%"

cl /O2 /LD /MD ^
    /D_WIN32_WINNT=%WIN_MIN_VER% ^
    /I"%PREFIX%\include" ^
    "%BRIDGE_SRC%" ^
    /Fo:"%DLL_OUT%\curl_unity_bridge.obj" ^
    /Fe:"%DLL_OUT%\libcurl_unity.dll" ^
    /link ^
    /DEF:"%PROJECT_ROOT%\bridge\exports.def" ^
    /IMPLIB:"%DLL_OUT%\libcurl_unity.lib" ^
    /WHOLEARCHIVE:"%PREFIX%\lib\libcurl.lib" ^
    "%PREFIX%\lib\libssl.lib" ^
    "%PREFIX%\lib\libcrypto.lib" ^
    "%PREFIX%\lib\nghttp2.lib" ^
    "%PREFIX%\lib\ngtcp2.lib" ^
    "%PREFIX%\lib\ngtcp2_crypto_ossl.lib" ^
    "%PREFIX%\lib\nghttp3.lib" ^
    "%PREFIX%\lib\zs.lib" ^
    ws2_32.lib crypt32.lib advapi32.lib bcrypt.lib user32.lib iphlpapi.lib
if errorlevel 1 goto :error

REM Clean intermediate files (keep .lib for linking)
del /q "%DLL_OUT%\curl_unity_bridge.obj" 2>nul
del /q "%DLL_OUT%\libcurl_unity.exp" 2>nul

echo   -^> %DLL_OUT%\libcurl_unity.dll
echo.
echo ========================================
echo   Build complete! (%PLATFORM%)
echo ========================================
echo   Install:  %PREFIX%
echo   Output:   %DLL_OUT%\libcurl_unity.dll
exit /b 0

REM ============================================================
REM Helper functions
REM ============================================================

:ensure_submodules
set "_repo=%~1"
set "_subpath=%~2"
if not exist "%_repo%\.gitmodules" goto :eof
pushd "%_repo%"
if defined _subpath (
    if not exist "%_subpath%\.git" (
        echo   Initializing submodule: %~nx1\%_subpath%
        git submodule update --init --depth 1 -- "%_subpath%"
    )
) else (
    git submodule status | findstr /B /C:"-" >nul 2>&1 && (
        echo   Initializing submodules: %~nx1
        git submodule update --init --depth 1
    )
)
popd
goto :eof

:error
echo.
echo [ERROR] Build failed!
exit /b 1
