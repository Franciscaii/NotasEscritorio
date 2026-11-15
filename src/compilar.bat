@echo off
REM Compila NotasEscritorio.exe (requiere el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0)
cd /d "%~dp0"
dotnet publish -c Release -o "%~dp0salida"
echo.
echo Listo. El programa esta en la carpeta "salida".
pause
