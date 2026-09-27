@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title KwikKonvert build

rem ==========================================================================
rem  KwikKonvert build script
rem    1. checks for the .NET SDK (offers to install it with winget)
rem    2. cleans old half-finished builds
rem    3. downloads the .NET runtime packs it needs
rem    4. runs the core tests
rem    5. builds ONE self-contained KwikKonvert.exe (falls back to an app folder if needed)
rem    6. starts it once to prove it works, then cleans up:
rem         - build junk (bin, obj, publish) is always removed
rem         - source code is removed ONLY if you answer Y
rem
rem  Everything is logged to build.log; if the build fails the real errors are shown.
rem
rem  Usage:  double-click build.bat
rem          build.bat arm64        (for ARM PCs)
rem          build.bat keep         (keep build output, don't clean up)
rem ==========================================================================

cd /d "%~dp0"

set "PLATFORM=x64"
set "KEEP=0"
for %%A in (%*) do (
    if /i "%%~A"=="arm64" set "PLATFORM=ARM64"
    if /i "%%~A"=="keep" set "KEEP=1"
)
if /i "%PLATFORM%"=="ARM64" (set "RID=win-arm64") else (set "RID=win-x64")

set "ROOT=%~dp0"
set "LOG=%ROOT%build.log"
set "PUB_SINGLE=%ROOT%publish\single"
set "PUB_FOLDER=%ROOT%publish\%RID%"
set "FINAL_EXE=%ROOT%KwikKonvert.exe"
set "FINAL_DIR=%ROOT%KwikKonvert-app"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

echo.
echo   KWIKKONVERT  -  such build. very compile. wow.
echo   ---------------------------------------------
echo.

> "%LOG%" echo KwikKonvert build log  %DATE% %TIME%
>>"%LOG%" echo Folder: %ROOT%
>>"%LOG%" echo Platform: %PLATFORM%  RID: %RID%
>>"%LOG%" ver

rem ---------------------------------------------------------------- 0. sanity
if not exist "src\KwikKonvert\KwikKonvert.csproj" (
    echo [X] Can't find src\KwikKonvert\KwikKonvert.csproj next to this file.
    echo     Run build.bat from the KwikKonvert folder that contains KwikKonvert.sln.
    goto :stop
)
echo "%ROOT%" | findstr /i /c:".zip\\" >nul && (
    echo [X] This is running from inside a .zip file.
    echo     Right-click the zip ^> Extract All..., then run build.bat from the extracted folder.
    goto :stop
)

rem ---------------------------------------------------------------- 1. .NET SDK
echo [1/6] Checking the .NET SDK...
where dotnet >nul 2>&1
if errorlevel 1 goto :nosdk
set "SDKOK="
for /f "tokens=1 delims= " %%V in ('dotnet --list-sdks 2^>nul') do (
    for /f "tokens=1,2,3 delims=." %%a in ("%%V") do (
        if %%a GEQ 9 set "SDKOK=%%V"
        if %%a EQU 8 if %%c GEQ 300 set "SDKOK=%%V"
    )
)
>>"%LOG%" echo --- dotnet --list-sdks
dotnet --list-sdks >>"%LOG%" 2>&1
if not defined SDKOK goto :nosdk
echo       found .NET SDK %SDKOK%

rem ---------------------------------------------------------------- 2. clean
echo [2/6] Cleaning old build output...
taskkill /im KwikKonvert.exe /f >nul 2>&1
dotnet build-server shutdown >>"%LOG%" 2>&1
call :clean_build_output

rem ---------------------------------------------------------------- 3. restore
echo [3/6] Downloading packages (first time takes a few minutes)...
>>"%LOG%" echo.
>>"%LOG%" echo ===================== RESTORE =====================
dotnet restore "src\KwikKonvert\KwikKonvert.csproj" -p:Platform=%PLATFORM% -r %RID% -p:KwikSingleFile=true >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [X] Package download failed. Check your internet connection / proxy.
    goto :buildfailed
)

rem ---------------------------------------------------------------- 4. tests (not blocking)
echo [4/6] Running core tests...
>>"%LOG%" echo.
>>"%LOG%" echo ===================== TESTS =====================
dotnet run --project "tests\KwikKonvert.Core.Tests" -c Release >>"%LOG%" 2>&1
if errorlevel 1 (
    echo       [!] some core tests failed - see build.log. Continuing with the app build.
) else (
    echo       all core tests passed. wow.
)

rem ---------------------------------------------------------------- 5. publish
echo [5/6] Building one-file KwikKonvert.exe (%RID%)...
>>"%LOG%" echo.
>>"%LOG%" echo ===================== PUBLISH (single file) =====================
set "MODE=single"
dotnet publish "src\KwikKonvert\KwikKonvert.csproj" -c Release -p:Platform=%PLATFORM% -r %RID% -p:KwikSingleFile=true -o "%PUB_SINGLE%" -v:normal >>"%LOG%" 2>&1
if errorlevel 1 (
    rem A compile error fails both ways - report it now instead of building twice.
    findstr /r /c:"error CS[0-9]" /c:"error NETSDK[0-9]" "%LOG%" >nul && goto :buildfailed
    echo       single-file build failed - building a normal app folder instead...
    goto :publish_folder
)
if not exist "%PUB_SINGLE%\KwikKonvert.exe" goto :publish_folder

rem Anything besides the exe (and debug symbols) means it isn't truly one file.
set "EXTRA=0"
for %%F in ("%PUB_SINGLE%\*") do (
    if /i not "%%~nxF"=="KwikKonvert.exe" if /i not "%%~xF"==".pdb" set /a EXTRA+=1
)
for /d %%D in ("%PUB_SINGLE%\*") do set /a EXTRA+=1
if not "%EXTRA%"=="0" (
    >>"%LOG%" echo Single-file publish left %EXTRA% extra files - using it as an app folder.
    set "MODE=folder"
    set "PUB_FOLDER=%PUB_SINGLE%"
)
goto :smoke

:publish_folder
set "MODE=folder"
>>"%LOG%" echo.
>>"%LOG%" echo ===================== PUBLISH (app folder) =====================
dotnet publish "src\KwikKonvert\KwikKonvert.csproj" -c Release -p:Platform=%PLATFORM% -r %RID% --self-contained true -o "%PUB_FOLDER%" -v:normal >>"%LOG%" 2>&1
if errorlevel 1 goto :buildfailed
if not exist "%PUB_FOLDER%\KwikKonvert.exe" (
    echo [X] Build said it succeeded but KwikKonvert.exe is missing.
    goto :buildfailed
)

rem ---------------------------------------------------------------- 6. prove it starts, then tidy up
:smoke
if "%MODE%"=="single" (set "TEST_EXE=%PUB_SINGLE%\KwikKonvert.exe") else (set "TEST_EXE=%PUB_FOLDER%\KwikKonvert.exe")
echo [6/6] Test-starting KwikKonvert...
call :smoke_test "%TEST_EXE%"
if errorlevel 1 (
    if "%MODE%"=="single" (
        >>"%LOG%" echo Single-file exe did not stay running - falling back to an app folder.
        echo       the one-file exe didn't start properly - building a normal app folder instead...
        goto :publish_folder
    )
    echo [X] KwikKonvert built but closed straight away when started.
    goto :buildfailed
)
echo       it starts. wow.

if "%KEEP%"=="1" (
    echo.
    echo   WOW. BUILT.  ^(kept build output: %TEST_EXE%^)
    start "" "%TEST_EXE%"
    echo.
    pause
    exit /b 0
)

rem Put the finished app next to this script.
if exist "%FINAL_EXE%" del /f /q "%FINAL_EXE%" >nul 2>&1
if exist "%FINAL_DIR%" rmdir /s /q "%FINAL_DIR%" >nul 2>&1
if "%MODE%"=="single" (
    copy /y "%PUB_SINGLE%\KwikKonvert.exe" "%FINAL_EXE%" >nul
    set "RESULT=%FINAL_EXE%"
) else (
    robocopy "%PUB_FOLDER%" "%FINAL_DIR%" /E /NFL /NDL /NJH /NJS /NP >nul
    set "RESULT=%FINAL_DIR%\KwikKonvert.exe"
)
if not exist "!RESULT!" (
    echo [X] Couldn't copy the finished app next to build.bat.
    goto :buildfailed
)

dotnet build-server shutdown >nul 2>&1
call :clean_build_output

echo.
echo   WOW. BUILT.
echo   !RESULT!
if "%MODE%"=="folder" (
    echo.
    echo   Note: this PC needed the normal app layout, so KwikKonvert lives in the
    echo   KwikKonvert-app folder. Keep the whole folder together.
)
echo.
echo   -------------------------------------------------------------
echo   Delete the source code too, leaving ONLY the app?
echo   This can't be undone - you'd need the zip again to rebuild or change it.
echo   -------------------------------------------------------------
choice /c YN /n /m "  Delete source code? [Y/N] "
if errorlevel 2 goto :done_keep_source

echo.
echo   deleting source... such tidy.
for %%D in (src tests tools publish) do if exist "%ROOT%%%D" rmdir /s /q "%ROOT%%%D" >nul 2>&1
for %%F in (KwikKonvert.sln README.md .gitignore build.ps1 build.log) do if exist "%ROOT%%%F" del /f /q "%ROOT%%%F" >nul 2>&1
start "" "!RESULT!"
echo   done. only KwikKonvert is left. wow.
echo.
pause
rem Delete this script last ("goto" off the end of the file first so cmd doesn't trip over it).
(goto) 2>nul & del /f /q "%~f0"

:done_keep_source
if exist "%LOG%" del /f /q "%LOG%" >nul 2>&1
start "" "!RESULT!"
echo.
echo   Source kept. Run build.bat again any time to rebuild.
echo.
pause
exit /b 0

rem ======================================================================
:smoke_test
rem Starts the exe, waits, and checks it is still running (a crash on start means it exits).
start "" "%~1"
timeout /t 10 /nobreak >nul
tasklist /fi "imagename eq KwikKonvert.exe" 2>nul | find /i "KwikKonvert.exe" >nul
if errorlevel 1 exit /b 1
taskkill /im KwikKonvert.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
exit /b 0

:clean_build_output
for %%D in ("src\KwikKonvert\bin" "src\KwikKonvert\obj" "src\KwikKonvert.Core\bin" "src\KwikKonvert.Core\obj" "tests\KwikKonvert.Core.Tests\bin" "tests\KwikKonvert.Core.Tests\obj" "publish") do (
    if exist "%%~D" rmdir /s /q "%%~D" >nul 2>&1
)
exit /b 0

rem ======================================================================
:nosdk
echo.
echo [X] KwikKonvert needs the .NET 8 SDK (8.0.300 or newer), .NET 9 or .NET 10 SDK.
echo.
where winget >nul 2>&1
if errorlevel 1 (
    echo     Install it from: https://dotnet.microsoft.com/download
    echo     ^(choose "SDK", Windows x64^), then run build.bat again.
    goto :stop
)
choice /c YN /m "    Install the .NET 8 SDK now with winget"
if errorlevel 2 goto :stop
winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
echo.
echo     Done. CLOSE this window and double-click build.bat again.
goto :stop

rem ======================================================================
:buildfailed
echo.
echo   much error. build went bonk.
echo   -------------------------------------------------------------
echo   Errors from build.log:
echo.
findstr /i /r /c:"error [A-Z]*[0-9]" /c:": error" /c:"Exception:" "%LOG%" > "%TEMP%\kwik_errors.txt"
powershell -NoProfile -Command "$l = Get-Content -LiteralPath '%TEMP%\kwik_errors.txt' | Sort-Object -Unique | Select-Object -First 30; if ($l) { $l } else { '  (no error lines found - last lines of the log:)'; Get-Content -LiteralPath '%LOG%' -Tail 25 }"

echo.
echo   -------------------------------------------------------------
echo   Nothing was deleted. Full log: %LOG%
echo   Send build.log to Claude and doge will fix it.
echo.
pause
exit /b 1

:stop
echo.
pause
exit /b 1
