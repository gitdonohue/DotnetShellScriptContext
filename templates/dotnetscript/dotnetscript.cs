//////////////////////////////////////////////////////////////////////////////////////////
//
// dotnetscript
//
//////////////////////////////////////////////////////////////////////////////////////////

using Microsoft.Extensions.Logging;
using ShellScriptContext;

using var scriptContext = new ScriptContext<ScriptArguments>("dotnetscript", args);
Console.WriteLine("dotnetscript Started.");
Console.WriteLine("Press Ctrl+C to cancel.");

try
{

    //////////////////////////////////////////////////////////////////////////////////////////
    //
    // Your script code here
    //
    //////////////////////////////////////////////////////////////////////////////////////////
    using (scriptContext.BeginScope("test"))
    {
        await Task.Delay(1000,scriptContext.CancellationToken);
    }
}
catch (OperationCanceledException)
{
    scriptContext.LogWarning("dotnetscript cancelled.");
    scriptContext.Exit(1);
}
catch (Exception e)
{
    scriptContext.LogError($"dotnetscript error: {e.Message}");
    scriptContext.Exit(2);
}

Console.WriteLine("dotnetscript Finished.");

//
// Script End
//

//////////////////////////////////////////////////////////////////////////////////////////
//
// Your script arguments here
// see https://github.com/commandlineparser/commandline
//
//////////////////////////////////////////////////////////////////////////////////////////

class ScriptArguments : DefaultScriptArguments
{
    //
    // The following options are defined by default:
    //
    //[CommandLine.Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")] public bool Verbose { get; set; }
    //[CommandLine.Option('q', "quiet", Required = false, HelpText = "Inhibit all output.")] public bool Quiet { get; set; }
    //[CommandLine.Option('s', "scopes", Required = false, HelpText = "Show context scopes.")] public bool Scopes { get; set; }
    //[CommandLine.Option('S', "scopetimings", Required = false, HelpText = "Show context scope timings.")] public bool ScopeTimings { get; set; }
    //[CommandLine.Option('t', "timestamps", Required = false, HelpText = "Show log times.")] public bool Times { get; set; }
}
