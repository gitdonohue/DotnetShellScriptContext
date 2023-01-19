# DotnetShellScriptContext
A helper library to make dotnet-based shell scripts more elegant.

## Features

- 'Dotnet new' template to setup a basic script
- Logging, with colors by severity
- Arguments handling (using CommandLineParser)
- Cancellation handling (Ctrl+C)
- Scopes, for log clarity and timings
- External calls helper (using CliWrap)
- Parallel/sequential async tasks helpers

## Template
There is a dotnet template for a base script with the boiler-plate code ready.
The template can be installed by calling (only once):
```dotnet new install ShellScriptContext.Template```
A new script can then be created by calling:
```dotnet new dotnetscript```.
