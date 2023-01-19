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

## Logging
The ScriptContext implements Microsoft.Extensions.Logging, exposing a Log() method with different severity levels (Trace,Debug,Information,Warning,Error,Critical).
Once instanciated, the ScriptContext captures all Console.Write/WriteLine calls, which get logged at the LogLevel.Information level. 

## Arguments
The ScriptContext is created with a arguments class, which must inherit from DefaultScriptArguments. Under the hood it uses the CommandLineParser package (https://github.com/commandlineparser/commandline).  The arguments are made available through the Arguments property of the ScriptContext.
