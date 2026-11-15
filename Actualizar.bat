@echo off
chcp 65001 >nul
cd /d "%~dp0"
move /y "Notas Escritorio.new" "Notas Escritorio.exe" >nul 2>&1
if errorlevel 1 (
  echo Cierra primero Notas Escritorio con la X y vuelve a abrir este archivo.
  pause
  exit /b
)
start "" "Notas Escritorio.exe"
