@echo off
setlocal
cd /d C:\Dev\Defence_Game

REM ===========================================================
REM  ML-Agents training launcher with auto-resume.
REM  Change RUN below for each new run id.
REM  If the PC crashes, just run this file again.
REM  Requires: Unity closed, Builds\Defence_Game.exe rebuilt.
REM ===========================================================

set RUN=plc_v13
set YAML=placement.yaml
set BRAIN=Placement
set ENVS=6
set TS=20

call conda activate mlagents
if errorlevel 1 goto condafail

:loop
if exist "results\%RUN%\%BRAIN%\checkpoint.pt" (set MODE=--resume) else (set MODE=--force)

echo.
echo ============================================================
echo   %date% %time%   run-id=%RUN%   mode=%MODE%
echo ============================================================
echo.

mlagents-learn %YAML% --env=Builds\Defence_Game.exe --run-id=%RUN% %MODE% --num-envs=%ENVS% --no-graphics --time-scale=%TS%
if errorlevel 1 goto retry
goto done

:retry
echo.
echo [%time%] Abnormal exit. Resuming from last checkpoint in 30 seconds.
echo          Press Ctrl+C twice to stop.
timeout /t 30 > nul
goto loop

:condafail
echo.
echo [ERROR] Could not activate conda env "mlagents".
echo         Run this from Anaconda Prompt.
pause
exit /b 1

:done
echo.
echo ===== TRAINING FINISHED =====
pause
