@echo off
setlocal
cd /d "%~dp0"
echo ========================================
echo STUDIO V01 - COMPILACION
echo ========================================
echo.
dotnet restore STUDIO-V01.sln
if errorlevel 1 goto error
echo.
dotnet build STUDIO-V01.sln -c Release --no-restore
if errorlevel 1 goto error
echo.
echo BUILD OK
echo Ejecutable generado en:
echo %~dp0src\Studio.UI\bin\Release\net8.0-windows\Studio.UI.exe
echo.
echo La compilacion termino correctamente.
goto end
:error
echo.
echo BUILD ERROR: revisa los mensajes de arriba.
echo La ventana quedara abierta para que puedas copiar el error.
echo.
pause
endlocal
exit /b 1
:end
echo.
pause
endlocal
