@echo off
setlocal enabledelayedexpansion
cd /d C:\Dev\Defence_Game

REM ===========================================================
REM  ML-Agents training launcher with auto-resume.
REM  Change RUN below for each new run id.
REM  If the PC crashes, just run this file again.
REM  Requires: Unity closed, Builds\Defence_Game.exe rebuilt.
REM
REM  2026-08-05 FIX: completion is decided by the FINAL EXPORTED
REM  MODEL, not by the exit code.  mlagents-learn can exit with
REM  code 0 after a worker crash or Ctrl+C, and the old script
REM  then printed "TRAINING FINISHED" on an unfinished run.
REM ===========================================================

set RUN=plc_v14
set YAML=placement.yaml
set BRAIN=Placement
set ENVS=6
set TS=20
set MAXTRY=30

set FINAL=results\%RUN%\%BRAIN%.onnx

call conda activate mlagents
if errorlevel 1 goto condafail

if exist "%FINAL%" (
  echo.
  echo [INFO] "%FINAL%" already exists. This run is already complete.
  echo        Change RUN above, or delete that file to train more.
  pause
  exit /b 0
)

set TRY=0

:loop
set /a TRY+=1
if !TRY! GTR %MAXTRY% goto toomany

if exist "results\%RUN%\%BRAIN%\checkpoint.pt" (set MODE=--resume) else (set MODE=--force)

echo.
echo ============================================================
echo   %date% %time%   run-id=%RUN%   mode=!MODE!   attempt !TRY!
echo ============================================================
echo.

mlagents-learn %YAML% --env=Builds\Defence_Game.exe --run-id=%RUN% !MODE! --num-envs=%ENVS% --no-graphics --time-scale=%TS%

echo.
echo [%time%] mlagents-learn exited.

REM --- The only reliable proof of completion is the exported model. ---
if exist "%FINAL%" goto done

echo [WARN] Final model "%FINAL%" was NOT produced - the run is unfinished.
echo        Latest checkpoint:
dir /b /o-d "results\%RUN%\%BRAIN%\*.onnx" 2>nul | findstr /n "^" | findstr "^1:"
echo        Resuming in 30 seconds.  Press Ctrl+C then Y to stop.
timeout /t 30 > nul
goto loop

:toomany
echo.
echo [ERROR] Gave up after %MAXTRY% attempts. Something is wrong beyond a simple crash.
echo         Check results\%RUN%\run_logs\Player-0.log
pause
exit /b 1

:condafail
echo.
echo [ERROR] Could not activate conda env "mlagents".
echo         Run this from Anaconda Prompt.
pause
exit /b 1

:done
echo.
echo ===== TRAINING FINISHED - final model written =====
dir "%FINAL%"
pause
