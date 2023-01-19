@echo off
PUSHD "%~dp0"
dotnet run --project dotnetscript.csproj --no-launch-profile -- --verbose --timestamps --scopes --scopetimings %*
POPD
