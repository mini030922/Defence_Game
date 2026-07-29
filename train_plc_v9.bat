@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion
cd /d C:\Dev\Defence_Game

REM ─────────────────────────────────────────────────────────────
REM  학습 자동 재개 스크립트
REM  PC가 꺼지거나 트레이너가 죽어도, 다시 실행하면 마지막 체크포인트부터 이어간다.
REM  최초 실행이면 --force, 체크포인트가 있으면 --resume 을 자동 선택.
REM
REM  ※ 사용 전 확인: Unity 를 닫았는가 / Builds\Defence_Game.exe 를 새로 빌드했는가
REM ─────────────────────────────────────────────────────────────

set RUN=plc_v9
set YAML=placement.yaml
set BRAIN=Placement
set ENVS=6
set TS=20

call conda activate mlagents
if errorlevel 1 (
  echo [오류] conda 환경 'mlagents' 활성화 실패. Anaconda Prompt 에서 실행했는지 확인.
  pause
  exit /b 1
)

set TRY=0

:loop
set /a TRY+=1
if exist "results\%RUN%\%BRAIN%\checkpoint.pt" (
  set MODE=--resume
) else (
  set MODE=--force
)

echo.
echo ============================================================
echo  [%date% %time%]  시도 !TRY!  ^|  %RUN%  !MODE!
echo ============================================================
echo.

mlagents-learn %YAML% --env=Builds\Defence_Game.exe --run-id=%RUN% !MODE! --num-envs=%ENVS% --no-graphics --time-scale=%TS%
set RC=!errorlevel!

echo.
echo [%date% %time%] mlagents-learn 종료 (코드 !RC!)

REM 코드 0 = 정상 완료 또는 Ctrl+C (윈도우에서 CTRL_C_EVENT 는 0)
if "!RC!"=="0" goto done

echo 비정상 종료. 30초 후 마지막 체크포인트부터 재개한다. (중단하려면 Ctrl+C 두 번)
timeout /t 30
goto loop

:done
echo.
echo ===== 학습 종료 =====
pause
