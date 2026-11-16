@echo off
rem Drag a student campus package folder onto this file to publish it.
rem All user-facing text comes from the Node script: cmd.exe garbles UTF-8 and
rem treats full-width parentheses inside echo as grouping syntax.
setlocal
if "%~1"=="" (
  node "%~dp0publish-deployment-package.cjs" --help
  pause
  exit /b 1
)
node "%~dp0publish-deployment-package.cjs" --package-dir "%~1" --output-dir "%~dp0..\packages" --mirror-owner Ljc798 --mirror-repo veyon-campus-deployment
pause
