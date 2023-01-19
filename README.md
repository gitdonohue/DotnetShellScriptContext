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


## Install

The Nuget package (https://www.nuget.org/packages/ShellScriptContext/) can be installed via the following command:
```
dotnet add package ShellScriptContext
```
But the preferred way to us it is by creating a project using a dotent template, as explained below.

## Template
There is a dotnet template for a base script with the boiler-plate code ready.
The template can be installed by calling (only once):
```
dotnet new install ShellScriptContext.Template
```
A new script can then be created by calling:
```
dotnet new dotnetscript
```

## Usage

Typical usage for a C# top-level script (console app).

```
using Microsoft.Extensions.Logging;
using ShellScriptContext;

using var scriptContext = new ScriptContext<ScriptArguments>("MyScript", args);
Console.WriteLine("MyScript Started.");
Console.WriteLine("Press Ctrl+C to cancel.");
```

## Logging
The ScriptContext implements Microsoft.Extensions.Logging, exposing a Log() method with different severity levels (Trace,Debug,Information,Warning,Error,Critical).
Once instanciated, the ScriptContext captures all Console.Write/WriteLine calls, which get logged at the LogLevel.Information level. 

Example:
```
scriptContext.LogError("This is fine.");
```

## Arguments
The ScriptContext is created with a arguments class, which must inherit from DefaultScriptArguments. Under the hood it uses the CommandLineParser package (https://github.com/commandlineparser/commandline).  The arguments are made available through the Arguments property of the ScriptContext.

The following arguments are provided by default:
```
  -v, --verbose         Set output to verbose messages.

  -q, --quiet           Inhibit all output.

  -s, --scopes          Show context scopes.

  -S, --scopetimings    Show context scope timings.

  -t, --timestamps      Show log times.

  -p, --pause           Pause at end of script.

  --json                Pass arguments as a json string (urlencoded)

  --help                Display this help screen.

  --version             Display version information.
```

## Cancellation
The ScriptContext handles Ctrl+C break events, and exposes the CancellationToken property, which should be used to terminate any ongoing work.  This token should be passed to any async calls, and it should be polled in non-async loops to exit as soon as possible.  The ScriptContext also exposes an Exit() method, for graceful termination.

Example:
```
await Task.Delay(1000,scriptContext.CancellationToken);
```

## Scopes
The ScriptContext provides a BeginScope() method, that returns an IDisposable, as prescribed in the ILogger interface. Scopes pushed onto a stack, and popped when the object gets disposed.

Example:
```
using (scriptContext.BeginScope("WorkStuff"))
{
    // Do work
    scriptContext.LogDebug("Work done.");
}
```

## External Calls
The ScriptContext provides Shell() and Exec() methods to start external processes.  The ScriptContext's CancellationToken is passed internally.  Under the hood this uses the CliWrap package (https://github.com/Tyrrrz/CliWrap).

Example:
```
await scriptContext.Shell("mkdir testdir");

var resp = scriptContext.Shell("dir", quiet:true, captureOutput:true);
if (resp)
{
    scriptContext.LogInformation(resp.CommandOutput);
}

await scriptContext.Exec("program.exe");
```


## Task Helpers
The ScriptContext provides a RunTasks() method, to make running parallel tasks more elegant.

Example:
```
var task1 = async () =>
{
    for (int i = 0; i < 6; ++i)
    {
        Console.WriteLine($"Test step {i}.");
        await Task.Delay(1000, scriptContext.CancellationToken);
    }
};

var task2 = async () => await scriptContext.Exec("program.exe");

await scriptContext.RunTasks(task1, task2);
```

