@echo off
cd /d "%~dp0"
where py >nul 2>nul
if not errorlevel 1 (
  py -3 audit.py
) else (
  python audit.py
)
if errorlevel 1 (
  echo Install Python 3.10+ with Tcl/Tk and add Python to PATH.
  pause
)
