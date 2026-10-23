@echo off
rem Candela Command Prompt: a console where the candela command-line tool can be run (Start menu shortcut).
title Candela Command Prompt
set "PATH=%~dp0;%PATH%"
cd /d "%USERPROFILE%"
echo Candela command-line tool. Examples:
echo   candela info "C:\Shows\scene.mvr"
echo   candela render "C:\Shows\scene.mvr" --out picture.png --look demo
echo   candela validate "C:\Shows\show.candela"
echo The samples are in "%~dp0samples". Settings: %%APPDATA%%\Candela, logs: %%LOCALAPPDATA%%\Candela\logs.
echo.
candela --help
