@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
dotnet run --project "%SCRIPT_DIR%src\Kusto.Cli\Kusto.Cli.csproj" -- %*
exit /b %ERRORLEVEL%
