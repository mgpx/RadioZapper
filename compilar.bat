@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

:menu
cls
echo ========================================
echo            RADIO ZAPPER
echo ========================================
echo.
echo  1 - Compilar projeto (Debug)
echo  2 - Publicar sem .NET embutido (Windows x64)
echo  S - Sair
echo.
echo Feche o Radio Zapper pelo menu da bandeja antes de compilar.
echo.
choice /c 12S /n /m "Escolha uma opção [1, 2, S]: "
if errorlevel 3 goto fim
if errorlevel 2 goto publicar
if errorlevel 1 goto compilar
goto menu

:compilar
echo.
dotnet build "RadioZapper.csproj"
if errorlevel 1 (
    echo.
    echo Falha na compilação. Consulte as mensagens acima.
) else (
    echo.
    echo Compilação concluída.
    echo Saída: bin\Debug\net10.0-windows10.0.19041.0\win-x64\
)
echo.
pause
goto menu

:publicar
echo.
dotnet publish "RadioZapper.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:VlcWindowsX64Enabled=true -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -p:DebugType=None -p:DebugSymbols=false -o "bin\publish-win-x64-sem-runtime"
if errorlevel 1 (
    echo.
    echo Falha na publicação. Consulte as mensagens acima.
) else (
    if exist "bin\publish-win-x64-sem-runtime\libSkiaSharp.pdb" del /q "bin\publish-win-x64-sem-runtime\libSkiaSharp.pdb"
    if exist "bin\publish-win-x64-sem-runtime\libHarfBuzzSharp.pdb" del /q "bin\publish-win-x64-sem-runtime\libHarfBuzzSharp.pdb"
    echo.
    echo Publicação concluída.
    echo Saída: bin\publish-win-x64-sem-runtime\
    echo Esta versão requer o runtime .NET 10 x64 na máquina de destino.
    echo Confira os arquivos da pasta e teste a reprodução antes de distribuir.
)
echo.
pause
goto menu

:fim
endlocal
exit /b 0
