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

    if (scriptContext.Arguments.RunTests)
    {
        // Example script logic
        using (scriptContext.BeginScope("Example"))
        {
            for (int i = 0; i < 6; ++i)
            {
                using (scriptContext.BeginScope($"step{i}"))
                {
                    Console.WriteLine($"Test step {i}.");
                    await Task.Delay(1000, scriptContext.CancellationToken);
                }

            }
        }

        // Example for parallel tasks
        using (scriptContext.BeginScope("Parallel"))
        {
            var task1 = async () =>
            {
                using (scriptContext.BeginScope("task1"))
                {
                    for (int i = 0; i < 6; ++i)
                    {
                        using (scriptContext.BeginScope($"step{i}"))
                        {
                            Console.WriteLine($"task1_step{i}.");
                            if (i == 2)
                            {
                                scriptContext.LogCritical("Ouch!");
                                throw new Exception("Forced failure test");
                            }
                            await Task.Delay(1000, scriptContext.CancellationToken);
                        }
                    }
                }
            };

            var task2 = async () =>
            {
                using (scriptContext.BeginScope("task2"))
                {
                    for (int i = 0; i < 5; ++i)
                    {
                        using (scriptContext.BeginScope($"step{i}"))
                        {
                            Console.WriteLine($"task2_step{i}.");
                            await Task.Delay(1200, scriptContext.CancellationToken);
                        }

                    }
                }
            };

            var task3 = async () =>
            {
                using (scriptContext.BeginScope("task3"))
                {
                    for (int i = 0; i < 5; ++i)
                    {
                        using (scriptContext.BeginScope($"step{i}"))
                        {
                            Console.WriteLine($"task3_step{i}.");
                            await Task.Delay(1300, scriptContext.CancellationToken);
                        }

                    }
                }
            };

            await scriptContext.RunTasks(task1, task2, task3);
        }
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
    [CommandLine.Option('x', "test", Required = false, HelpText = "Run test.")] public bool RunTests { get; set; }

    //
    // The following options are defined by default:
    //
    //[CommandLine.Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")] public bool Verbose { get; set; }
    //[CommandLine.Option('q', "quiet", Required = false, HelpText = "Inhibit all output.")] public bool Quiet { get; set; }
    //[CommandLine.Option('s', "scopes", Required = false, HelpText = "Show context scopes.")] public bool Scopes { get; set; }
    //[CommandLine.Option('S', "scopetimings", Required = false, HelpText = "Show context scope timings.")] public bool ScopeTimings { get; set; }
    //[CommandLine.Option('t', "timestamps", Required = false, HelpText = "Show log times.")] public bool Times { get; set; }
}
