@echo off
chcp 936 >nul
setlocal enabledelayedexpansion

rem ============================================================
rem  一键推送（单仓库版）
rem  用法：双击本文件，输入本次更新说明，自动 add -> commit -> push
rem  本文件放在项目根目录，只会推送它所在的那个仓库
rem  注意：本文件为 GBK 编码，用记事本修改后请保存为 ANSI
rem ============================================================

pushd "%~dp0" >nul 2>nul
for %%I in ("%CD%") do set "NAME=%%~nxI"
title 一键推送 - !NAME!

echo ==================================================
echo   一键推送：!NAME!
echo ==================================================
echo.

where git >nul 2>nul
if errorlevel 1 goto :NOGIT
if not exist ".git" goto :NOREPO

set "MSG="
set /p "MSG=请输入本次更新说明（留空则取消）: "
if not defined MSG goto :CANCEL
echo.

git add -A
git --no-pager diff --cached --quiet
if not errorlevel 1 goto :NOCHANGE

echo   本次改动：
git --no-pager diff --cached --stat
echo.
git -c i18n.logOutputEncoding=gbk commit -m "!MSG!"
if errorlevel 1 goto :CMDFAIL

:NOCHANGE
git rev-parse --abbrev-ref "@{u}" >nul 2>nul
if errorlevel 1 (
    echo   首次推送，正在设置上游分支...
    git push -u origin HEAD
) else (
    git push
)
if errorlevel 1 goto :PUSHFAIL

echo.
echo   [完成] 已推送到远程仓库
goto :END

:CANCEL
echo.
echo   已取消，没有做任何改动。
goto :END

:NOGIT
echo [错误] 没有找到 git，请先安装 Git 并加入 PATH。
goto :END

:NOREPO
echo [错误] 当前目录不是 Git 仓库
goto :END

:CMDFAIL
echo.
echo   [失败] 提交失败，请看上面的 Git 报错
goto :END

:PUSHFAIL
echo.
echo   [失败] 推送失败，请看上面的 Git 报错
goto :END

:END
echo.
popd >nul 2>nul
pause
exit /b 0
