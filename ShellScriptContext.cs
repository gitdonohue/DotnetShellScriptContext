using System.Collections.Immutable;
using System.Text;
using CliWrap;
using CommandLine;
using Microsoft.Extensions.Logging;

namespace ShellScriptContext;

public sealed class ScriptContext<TScriptArguments> : IDisposable, ILogger
{
    public CancellationToken CancellationToken => _cts.Token;
    public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;
    public TScriptArguments Arguments { get; init; }


    private ILoggerFactory _loggerFactory;
    private ILogger _logger { get; init; }
    private CancellationTokenSource _cts;

    private AsyncLocalContextStack _scopes = new();
    private bool UseLocalScopes => true;

    private TextWriter ConsoleStdout { get; init; }
    private TextWriter ConsoleStderr { get; init; }

    private static Dictionary<LogLevel, ConsoleColor> ConsoleColorsMap = new()
    {
        { LogLevel.Trace, ConsoleColor.Blue },
        { LogLevel.Debug, ConsoleColor.Green },
        { LogLevel.Information, ConsoleColor.Gray },
        { LogLevel.Warning, ConsoleColor.Yellow },
        { LogLevel.Error, ConsoleColor.Red },
        { LogLevel.Critical, ConsoleColor.Red },
        { LogLevel.None, ConsoleColor.White },
    };

    public ScriptContext(string name, IEnumerable<string> args)
    {
        var scriptArguments = CommandLine.Parser.Default.ParseArguments<TScriptArguments>(args)
            .WithNotParsed((err) => throw new ArgumentException());
        Arguments = scriptArguments.Value;

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddFilter(name, LogLevel.Debug)
                .AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "HH:mm:ss:fff ";
                    options.SingleLine = true;
                });
        });
        _logger = _loggerFactory.CreateLogger(name);

        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { _cts.Cancel(); e.Cancel = true; Environment.ExitCode = -1; };

        ConsoleStdout = Console.Out;
        ConsoleStderr = Console.Error;
        Console.SetOut(new TextWriterFromILogger(this, LogLevel.Information));
        Console.SetError(new TextWriterFromILogger(this, LogLevel.Error));

        if (UseLocalScopes)
        {
            _scopes.ContextPushed += (ctx, stack) =>
            {
                this.LogDebug($"Context started: {ctx.Name}");
            };

            _scopes.ContextPopped += (ctx, stack) =>
            {
                this.LogDebug($"Context stopped: {ctx.Name} Duration: {ctx.Lifetime.ToString(@"mm\:ss\:fff")}");
            };

            //_scopes.AsyncContextChanged += (previous, current, threadChanged) =>
            //{
            //    this.LogTrace($"{previous?.FirstOrDefault()?.Name} => {current?.FirstOrDefault()?.Name} [threadchange:{threadChanged}]");
            //};
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _loggerFactory?.Dispose();
        Console.ResetColor();
    }

    public void Exit(int returnCode)
    {
        Dispose();
        Environment.Exit(returnCode);
    }

    public IDisposable BeginScope(string scopeName)
    {
        if (UseLocalScopes) return _scopes.BeginScope(scopeName);
        return _logger.BeginScope(scopeName)!;
    }

    public class ExecResult
    {
        public CommandResult CommandResult { get; init; }
        public string CommandOutput { get; init; }

        internal ExecResult(CommandResult commandResult, string commandOutput)
        {
            CommandResult = commandResult;
            CommandOutput = commandOutput;
        }

        public static implicit operator bool(ExecResult result)
            => result.CommandResult.ExitCode == 0;
    }

    public Task<ExecResult> Shell(string cmd, bool quiet = false, bool captureOutput = false)
        => Exec("cmd.exe", "/C " + cmd, quiet, captureOutput);

    public async Task<ExecResult> Exec(string cmd, string args, bool quiet = false, bool captureOutput = false)
    {
        try
        {
            var sb = new StringBuilder();

            var result = await Cli
                .Wrap(cmd)
                .WithArguments(args)
                .WithStandardOutputPipe(PipeTarget.ToDelegate((msg) => { if (!quiet) this.LogInformation(msg); if (captureOutput) sb.Append(msg); sb.Append('\n'); }))
                .WithStandardErrorPipe(PipeTarget.ToDelegate((msg) => { if (!quiet) this.LogWarning(msg); }))
                .ExecuteAsync(_cts.Token);

            return new ExecResult(result, sb.ToString());
        }
        catch (Exception e)
        {
            this.LogError($"({e}) {e.Message}");
            return new ExecResult(new CommandResult(-1, DateTimeOffset.Now, DateTimeOffset.Now), string.Empty);
        }
    }

    public async Task<ExecResult> Exec(string cmd, IEnumerable<string> args, bool quiet = false, bool captureOutput = false)
        => await Exec(cmd, string.Join(' ', args), quiet, captureOutput);

    public async Task RunTasks(bool parallel, IEnumerable<Task> tasksToRun)
    {
        try
        {
            if (parallel)
            {
                //await Task.WhenAll(tasksToRun);

                // We want to cancel all running tasks for any uncaught exception
                await Task.WhenAll(tasksToRun.Select(async (x) =>
                {
                    try
                    {
                        await x;
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        this.LogError($"Task failed: {e.Message}");
                        this._cts.Cancel();
                        Environment.ExitCode = -1;
                    }
                }));
            }
            else
            {
                foreach (var task in tasksToRun)
                {
                    await task;
                    if (CancellationToken.IsCancellationRequested) break;
                }
            }
        }
        catch (TaskCanceledException)
        {
            this.LogWarning("Cancelled");
        }
    }

    public async Task RunTasks(bool parallel, params Task[] tasksToRun) => await RunTasks(parallel, tasksToRun.AsEnumerable());
    public async Task RunTasks(params Task[] tasksToRun) => await RunTasks(true, tasksToRun);
    public async Task RunTasks(bool parallel, params Func<Task>[] tasksToRun) => await RunTasks(parallel, tasksToRun.Select(x => x.Invoke()));
    public async Task RunTasks(params Func<Task>[] tasksToRun) => await RunTasks(true, tasksToRun);

    public void WriteLine(string msg) => this.LogInformation(msg);

    #region ILogger
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (UseLocalScopes)
        {
            TextWriter consoleWriter = (logLevel >= LogLevel.Error) ? ConsoleStderr : ConsoleStdout;
            string msg = state!.ToString() ?? "";
            msg = msg.TrimEnd();

            var timeFmt = $"{DateTime.Now.ToString("HH:mm:ss:fff")} ";

            var scopes = string.Join('|', _scopes.GetStackNames().Reverse());
            var scopesFmt = (scopes.Length > 0) ? $"[{scopes}] " : string.Empty;

            ConsoleColor currentBackground = Console.BackgroundColor;
            ConsoleColor currentForeground = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColorsMap[logLevel];
                consoleWriter.WriteLine($"{timeFmt}{scopesFmt}{msg}");
            }
            finally
            {
                Console.BackgroundColor = currentBackground;
                Console.ForegroundColor = currentForeground;
            }
        }
        else
        {
            _logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return this.BeginScope(state.ToString() ?? string.Empty);
    }
    #endregion

    private class TextWriterFromILogger : TextWriter
    {
        ILogger _logger { get; init; }
        LogLevel _logLevel { get; init; }

        StringBuilder _sb = new StringBuilder();

        public TextWriterFromILogger(ILogger logger, LogLevel logeLevel)
        {
            _logger = logger;
            _logLevel = logeLevel;
        }

        public override Encoding Encoding => Encoding.Default;
        public override void Write(string? value) => _sb.Append(value);

        public override void WriteLine(string? value)
        {
            if (value == null) return;
            _sb.AppendLine(value);
            _logger.Log(_logLevel, _sb.ToString());
            _sb.Clear();
        }
    }
}

internal sealed class AsyncLocalContextStack
{
    public class StackContext
    {
        public string Name { get; init; } = "noname";
        public DateTime CreationTime { get; init; } = DateTime.Now;
        public DateTime DestructionTime { get; set; } = DateTime.Now;
        public TimeSpan Lifetime => DestructionTime - CreationTime;
    }

    public event Action<StackContext, IEnumerable<StackContext>>? ContextPushed;
    public event Action<StackContext, IEnumerable<StackContext>>? ContextPopped;

    public event Action<IEnumerable<StackContext>?, IEnumerable<StackContext>?, bool>? AsyncContextChanged;

    private AsyncLocal<ImmutableStack<StackContext>> _asyncLocalStack = new();

    public AsyncLocalContextStack()
    {
        _asyncLocalStack = new(OnAsyncLocalChange);
    }

    private void OnAsyncLocalChange(AsyncLocalValueChangedArgs<ImmutableStack<StackContext>> change)
    {
        AsyncContextChanged?.Invoke(change.PreviousValue, change.CurrentValue, change.ThreadContextChanged);
    }

    ImmutableStack<StackContext> LocalStack
    {
        get
        {
            if (_asyncLocalStack.Value == null)
            {
                _asyncLocalStack.Value = ImmutableStack<StackContext>.Empty;
            }
            return _asyncLocalStack.Value;
        }
        set { _asyncLocalStack.Value = value; }
    }

    private void PushContext(string name)
    {
        var ctx = new StackContext() { Name = name };
        LocalStack = LocalStack.Push(ctx);
        ContextPushed?.Invoke(ctx, LocalStack);
    }

    private void PopContext()
    {
        var ctx = LocalStack.Peek();
        ctx.DestructionTime = DateTime.Now;
        ContextPopped?.Invoke(ctx, LocalStack);
        LocalStack = LocalStack.Pop();
    }

    private class ScopedDisposable : IDisposable
    {
        public Action? OnDispose { get; init; }
        public void Dispose() => OnDispose?.Invoke();
    }

    public IDisposable BeginScope(string name)
    {
        PushContext(name);
        return new ScopedDisposable() { OnDispose = PopContext };
    }

    public IEnumerable<StackContext> GetStack() => _asyncLocalStack.Value ?? Enumerable.Empty<StackContext>();
    public IEnumerable<string> GetStackNames() => GetStack().Select(x => x.Name);
}