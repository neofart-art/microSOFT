@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Outlook AutoDiscover Repair

:: ------------------------------------------------------------
:: БЕЗУСЛОВНАЯ ПАУЗА В САМОМ НАЧАЛЕ — ОКНО НЕ ЗАКРОЕТСЯ
:: ------------------------------------------------------------
echo Press any key to start the script...
pause >nul

chcp 437 >nul 2>&1

set "MODE_SILENT=0"
set "MODE_UNDO=0"
set "MODE_NOKILL=0"
set "ACTION=Apply"

set /a SUCCESS=0
set /a SKIPPED=0
set /a FAILED=0

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="/silent" ( set "MODE_SILENT=1" & shift & goto parse_args )
if /i "%~1"=="/undo" ( set "MODE_UNDO=1" & set "ACTION=Undo" & shift & goto parse_args )
if /i "%~1"=="/nokill" ( set "MODE_NOKILL=1" & shift & goto parse_args )
if /i "%~1"=="/?" goto usage
if /i "%~1"=="-h" goto usage
if /i "%~1"=="--help" goto usage
echo [ERROR] Unknown argument: %~1
goto usage

:args_done

echo ============================================================
echo   Outlook AutoDiscover Repair
echo   Mode : %ACTION%
echo ============================================================
echo.

reg query "HKU\S-1-5-19" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Administrator rights required.
    goto finish
)
echo [OK] Administrator rights confirmed.
echo.

set "HAS_WOW64=0"
reg query "HKLM\SOFTWARE\WOW6432Node" >nul 2>&1
if not errorlevel 1 set "HAS_WOW64=1"

set "VERSIONS="
for %%r in ("" "\WOW6432Node") do (
    for %%v in (16.0 15.0 14.0) do (
        reg query "HKLM\SOFTWARE%%r\Microsoft\Office\%%v\Outlook\InstallRoot" >nul 2>&1
        if !errorlevel! equ 0 call :add_version "%%v"
    )
    reg query "HKLM\SOFTWARE%%r\Microsoft\Office\ClickToRun" >nul 2>&1
    if !errorlevel! equ 0 call :add_version "16.0"
)
if not defined VERSIONS (
    for %%v in (16.0 15.0 14.0) do (
        reg query "HKLM\SOFTWARE\Microsoft\Office\%%v" >nul 2>&1
        if !errorlevel! equ 0 call :add_version "%%v"
    )
)
if not defined VERSIONS (
    echo [WARNING] No Outlook detected, using defaults.
    set "VERSIONS=16.0 15.0 14.0"
)
echo [INFO] Outlook versions: !VERSIONS!
echo.

set "PARAMS=disableautodiscoverv2service excludeexplicito365endpoint excludehttpredirect excludehttpsautodiscoverdomain excludehttpsrootdomain excludelastknowngoodurl excludescplookup excludesrvrecord"

if "!MODE_UNDO!"=="1" (
    echo [STEP] Removing settings...
    for %%v in (!VERSIONS!) do (
        call :undo_hive "HKCU" "%%v"
        call :undo_hive "HKLM" "%%v"
        if "!HAS_WOW64!"=="1" call :undo_hive "HKLM\SOFTWARE\WOW6432Node" "%%v"
    )
    goto finish
)

echo [STEP] Applying settings...
for %%v in (!VERSIONS!) do (
    call :apply_hive "HKCU" "%%v"
    call :apply_hive "HKLM" "%%v"
    if "!HAS_WOW64!"=="1" call :apply_hive "HKLM\SOFTWARE\WOW6432Node" "%%v"
)

if !FAILED! equ 0 ( echo [OK] Applied ) else ( echo [WARNING] Errors )

if "!MODE_NOKILL!"=="0" (
    echo.
    echo Closing Outlook...
    tasklist /FI "IMAGENAME eq OUTLOOK.EXE" 2>nul | find /I "OUTLOOK.EXE" >nul
    if !errorlevel! equ 0 (
        taskkill /IM OUTLOOK.EXE /F >nul 2>&1
        if !errorlevel! equ 0 ( echo   Terminated ) else ( echo   Failed to terminate )
    ) else ( echo   Not running )
)

goto finish

:add_version
set "VER=%~1"
for %%x in (!VERSIONS!) do if /i "%%x"=="!VER!" goto :eof
set "VERSIONS=!VERSIONS! !VER!"
goto :eof

:build_paths
set "HIVE=%~1"
set "VER=%~2"
if /i "!HIVE!"=="HKLM\SOFTWARE\WOW6432Node" (
    set "KEY1=!HIVE!\Microsoft\Office\!VER!\Outlook\AutoDiscover"
    set "KEY2="
) else (
    set "KEY1=!HIVE!\Software\Microsoft\Office\!VER!\Outlook\AutoDiscover"
    set "KEY2=!HIVE!\Software\Policies\Microsoft\Office\!VER!\Outlook\AutoDiscover"
)
goto :eof

:apply_hive
call :build_paths "%~1" "%~2"
echo   [%~1] Office %~2
reg add "!KEY1!" /f >nul 2>&1 || ( echo   ! Failed to create key: !KEY1! & set /a FAILED+=1 & goto :eof )
if defined KEY2 (
    reg add "!KEY2!" /f >nul 2>&1 || ( echo   ! Failed to create key: !KEY2! & set /a FAILED+=1 & goto :eof )
)
call :set_dword "!KEY1!" "excludeexplicito365endpoint" "1"
if defined KEY2 (
    for %%p in (!PARAMS!) do call :set_dword "!KEY2!" "%%p" "1"
)
goto :eof

:undo_hive
call :build_paths "%~1" "%~2"
echo   [%~1] Office %~2
call :delete_value "!KEY1!" "excludeexplicito365endpoint"
if defined KEY2 (
    for %%p in (!PARAMS!) do call :delete_value "!KEY2!" "%%p"
)
goto :eof

:delete_value
set "KEY=%~1"
set "VAL=%~2"
reg query "!KEY!" /v "!VAL!" >nul 2>&1
if errorlevel 1 (
    echo      . Not found: !VAL! (skipped)
    set /a SKIPPED+=1
    goto :eof
)
reg delete "!KEY!" /v "!VAL!" /f >nul 2>&1
if !errorlevel! equ 0 (
    echo      - Removed: !VAL!
    set /a SUCCESS+=1
) else (
    echo      ! Failed to remove: !VAL!
    set /a FAILED+=1
)
goto :eof

:set_dword
reg add "%~1" /v "%~2" /t REG_DWORD /d "%~3" /f >nul 2>&1
if !errorlevel! equ 0 ( set /a SUCCESS+=1 ) else ( echo      ! Failed to set: %~2 & set /a FAILED+=1 )
goto :eof

:finish
echo.
echo ============================================================
echo   Success : !SUCCESS!
echo   Skipped : !SKIPPED!
echo   Failed  : !FAILED!
echo ============================================================
echo Restart Outlook to apply changes.

if "!MODE_SILENT!"=="0" (
    echo.
    echo Press any key to exit...
    pause
)

set "RC=0"
if !FAILED! gtr 0 set "RC=1"
endlocal & exit /b %RC%

:usage
echo.
echo Usage: %~nx0 [/silent] [/undo] [/nokill]
echo.
echo   (no args)   Apply settings
echo   /silent     Silent mode (no prompts, no pause)
echo   /undo       Remove settings created by this script
echo   /nokill     Do not terminate Outlook
echo.
goto finish