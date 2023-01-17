@echo off
PUSHD "%~dp0"
dotnet run --project dotnetscript.csproj -- --verbose --timestamps --scopes --scopetimings %*
POPD
