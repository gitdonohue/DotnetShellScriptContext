@echo off
PUSHD "%~dp0"
dotnet run -- %*

rem Alternatively, if you want to avoid seeing the 'Terminate batch job (Y/N)?' prompt on cancel.
rem Unfortunately, this solution pushes a cmd context, which must be manually exited.
rem cmd /k dotnet run -- %*

POPD
rem exit /b
