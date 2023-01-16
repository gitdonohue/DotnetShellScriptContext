@echo off
PUSHD "%~dp0"
dotnet new install ./ --force
POPD
