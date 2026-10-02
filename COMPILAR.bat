@echo off
setlocal
cd /d "%~dp0"
dotnet restore STUDIO-V01.sln
if errorlevel 1 goto error
dotnet build STUDIO-V01.sln -c Release --no-restore
if errorlevel 1 goto error
echo BUILD OK
echo src\Studio.UI\bin\Release\net8.0-windows\Studio.UI.exe
goto end
:error
echo BUILD ERROR
exit /b 1
:end
endlocal
