@echo off
setlocal

set MSI=%~dp0RunAsHelper.Installer\bin\Release\RunAsHelper-Setup.msi
set LOG=%TEMP%\RunAsHelper-Setup.log

if not exist "%MSI%" (
    echo ERROR: MSI not found at:
    echo   %MSI%
    echo Build the solution in Release configuration first.
    pause
    exit /b 1
)

echo Installing RunAS Helper with verbose logging...
echo Log will be written to:
echo   %LOG%
echo.

msiexec /i "%MSI%" /l*v "%LOG%"

echo.
echo Installation finished. Opening log...
notepad "%LOG%"
endlocal
