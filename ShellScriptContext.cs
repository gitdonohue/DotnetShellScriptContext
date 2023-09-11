using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;
using CliWrap;
using CommandLine;
using Microsoft.Extensions.Logging;

namespace ShellScriptContext
{
	public sealed class ScriptContext<TScriptArguments> : IDisposable, ILogger
        where TScriptArguments : DefaultScriptArguments
    {
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;
        public TScriptArguments Arguments { get; init; }

        public static Dictionary<LogLevel, ConsoleColor> ConsoleColorsMap = new()
        {
            { LogLevel.Trace, ConsoleColor.Blue },
            { LogLevel.Debug, ConsoleColor.Green },
            { LogLevel.Information, ConsoleColor.Gray },
            { LogLevel.Warning, ConsoleColor.Yellow },
            { LogLevel.Error, ConsoleColor.Red },
            { LogLevel.Critical, ConsoleColor.Red },
            { LogLevel.None, ConsoleColor.White },
        };

        private ILoggerFactory _loggerFactory;
        private ILogger _logger { get; init; }
        private CancellationTokenSource _cts;

        private AsyncLocalContextStack _scopes = new();
        private bool UseLocalScopes => true;

        private LogLevel _logLevel { get; init; }
        private TextWriter ConsoleStdout { get; init; }
        private TextWriter ConsoleStderr { get; init; }

		private ReplayCapture.ReplayCaptureWriter? ReportWriter { get; init; } = null;
	    ReplayCapture.Color[] ReportLogColors = new ReplayCapture.Color[] { ReplayCapture.Color.Cyan, ReplayCapture.Color.LightBlue, ReplayCapture.Color.Blue, ReplayCapture.Color.OrangeRed, ReplayCapture.Color.Red, ReplayCapture.Color.Red, ReplayCapture.Color.Gray };
        private System.Timers.Timer ReportAutoFrameTick { get; init; } = new() { Interval = 10, AutoReset = true, Enabled = false };

        private ResourceMonitor ResourceMonitor { get; } = new(TimeSpan.FromMilliseconds(1000));

		private Stopwatch RunningTime { get; init; } = new();

	    public ScriptContext(string name, IEnumerable<string> args)
        {
            var scriptArguments = CommandLine.Parser.Default.ParseArguments<TScriptArguments>(args)
                .WithNotParsed((err) => throw new ArgumentException());
            Arguments = scriptArguments.Value;

            if (Arguments.JsonArguments != string.Empty)
            {
                var jsonString = System.Net.WebUtility.UrlDecode(Arguments.JsonArguments);
                var jsonArgs = JsonSerializer.Deserialize<TScriptArguments>(jsonString);
                if (jsonArgs != null)
                {
                    Arguments = jsonArgs;
                }
                else
                {
                    throw new ArgumentException($"Invalid json arguments: {jsonArgs}");
                }
            }

            RunningTime.Restart();

		    _logLevel = Arguments.Quiet ? LogLevel.Critical 
                : (Arguments.Verbose ? LogLevel.Trace : LogLevel.Information);
        
            if (Arguments.Report != string.Empty)
            {
                ReportWriter = new ReplayCapture.ReplayCaptureWriter(Arguments.Report);
                ReportWriter.RegisterEntity(this);
				
                ReportAutoFrameTick.Elapsed += (o,e) => 
                {
					ReportWriter.StepFrame((float)RunningTime.Elapsed.TotalSeconds); 
                };
			    ReportAutoFrameTick.Start();

                ResourceMonitor.CpuPct += (cpu) => ReportWriter.SetDynamicParam(this, "cpu", (float)cpu);
                ResourceMonitor.MemoryWorkingSetMBChanged += (memory) => ReportWriter.SetDynamicParam(this, "memory", memory);
                ResourceMonitor.ThreadCountChanged += (threads) => ReportWriter.SetDynamicParam(this, "threads", threads);
                ResourceMonitor.ChildProcessesChanged += (childprocessesString) => ReportWriter.SetDynamicParam(this, "processes", childprocessesString);

				_scopes.ContextPushed += (ctx, stack) => 
				{ 
					ReportWriter.RegisterEntity(ctx, name = string.Join('|',stack.Reverse()));
					ReportWriter.SetLog(ctx, "context", $"Context opened: {ctx}", ReplayCapture.Color.BlueViolet);
				};

                _scopes.ContextPopped += (ctx, _) => 
				{
					ReportWriter.SetLog(ctx, "context", $"Context closed: {ctx}", ReplayCapture.Color.BlueViolet);
					ReportWriter.UnRegisterEntity(ctx); 
				};
		    }

            // Note: When UseLocalScopes is true, we don't use M$'s logging solution.
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddFilter(name, _logLevel)
                    .AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = Arguments.Scopes;
                        options.TimestampFormat = Arguments.Times ? "HH:mm:ss:fff " : string.Empty;
                        options.SingleLine = true;
                    });
            });
            _logger = _loggerFactory.CreateLogger(name);

            _cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); Environment.ExitCode = -1; };

            ConsoleStdout = Console.Out;
            ConsoleStderr = Console.Error;
            Console.SetOut(new TextWriterFromILogger(this, LogLevel.Information));
            Console.SetError(new TextWriterFromILogger(this, LogLevel.Error));

            if (UseLocalScopes && Arguments.ScopeTimings)
            {
                _scopes.ContextPushed += (ctx, stack) =>
                {
                    this.LogDebug($"Context started: {ctx.Name}");
                };

                _scopes.ContextPopped += (ctx, stack) =>
                {
                    this.LogDebug($"Context stopped: {ctx.Name} Duration: {ctx.Lifetime.ToString(@"mm\:ss\:fff")}");
                };
            }
        }

		private void ResourceMonitor_ChildProcessesChanged(string obj)
		{
			throw new NotImplementedException();
		}

		public void Dispose()
        {
            _cts.Cancel();
            _loggerFactory?.Dispose();
            Console.ResetColor();
            Console.SetOut(ConsoleStdout);
            Console.SetError(ConsoleStderr);

			ResourceMonitor.Dispose();

			ReportAutoFrameTick.Stop();
			ReportWriter?.StepFrame((float)RunningTime.Elapsed.TotalSeconds);
		    ReportWriter?.Dispose();

		    if (Arguments.Pause)
            {
                Console.WriteLine("Press any key to continue");
                Console.ReadKey();
            }
        }

        public override string ToString() => "ScriptContext";

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

        public Task<ExecResult> Shell(string cmd, bool quiet = false, bool captureOutput = false, bool directToConsole = false)
            => Exec("cmd.exe", "/C " + cmd, quiet, captureOutput, directToConsole);

        public async Task<ExecResult> Exec(string cmd, string args, bool quiet = false, bool captureOutput = false, bool directToConsole = false)
        {
            try
            {
                var sb = new StringBuilder();

                var result = await Cli
                    .Wrap(cmd)
                    .WithArguments(args)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate((msg) => 
					{
						if (!quiet) 
						{
							if (directToConsole)
							{
								ConsoleStdout.WriteLine(msg);
							}
							else
							{
								this.LogInformation(msg); 
							}
						}
						if (captureOutput) 
						{ 
							sb.Append(msg); 
							sb.Append('\n'); 
						} 
					}))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate((msg) => 
					{
						if (!quiet) 
						{
							if (directToConsole)
							{
								ConsoleStderr.WriteLine(msg);
							}
							else
							{
								this.LogWarning(msg); 
							}
						}
					}))
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
                this.LogWarning("Task Cancelled");
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
                string msg = state!.ToString() ?? "";
                msg = msg.TrimEnd();

                string scopesFmt = "";
                if (Arguments.Scopes)
                {
                    var scopesStack = _scopes.GetStackNames().Reverse();
                    var scopes = string.Join('|', scopesStack);
                    scopesFmt = (scopes.Length > 0) ? $"[{scopes}] " : string.Empty;
			    }

			    if (ReportWriter != null)
			    {
                    object? scopeObject = _scopes.GetStack().FirstOrDefault();
                
				    ReportWriter.SetLog(scopeObject ?? this, logLevel.ToString(), msg, ReportLogColors[(int)logLevel]);
			    }

                if (logLevel < _logLevel) return;

                TextWriter consoleWriter = (logLevel >= LogLevel.Error) ? ConsoleStderr : ConsoleStdout;
			    ConsoleColor currentBackground = Console.BackgroundColor;
                ConsoleColor currentForeground = Console.ForegroundColor;
                try
                {
                    var timeFmt = Arguments.Times ? $"{DateTime.Now.ToString("HH:mm:ss:fff")} " : string.Empty;
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

		    public override string ToString() => Name;
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

	#region ReplayCapture
	namespace ReplayCapture
    {
	    public struct Point { public float X, Y, Z; }
	    public struct Quaternion
	    {
		    public float X, Y, Z, W;
		    public static readonly Quaternion Identity = new Quaternion() { W = 1 };
	    }

	    public struct Transform
	    {
		    public Point Translation;
		    public Quaternion Rotation;

		    public static readonly Transform Identity = new Transform() { Rotation = Quaternion.Identity };
	    }

	    // Same as System.Windows.Media.Colors, but without the dependency
	    public enum Color { AliceBlue, PaleGoldenrod, Orchid, OrangeRed, Orange, OliveDrab, Olive, OldLace, Navy, NavajoWhite, Moccasin, MistyRose, MintCream, MidnightBlue, MediumVioletRed, MediumTurquoise, MediumSpringGreen, MediumSlateBlue, LightSkyBlue, LightSlateGray, LightSteelBlue, LightYellow, Lime, LimeGreen, PaleGreen, Linen, Maroon, MediumAquamarine, MediumBlue, MediumOrchid, MediumPurple, MediumSeaGreen, Magenta, PaleTurquoise, PaleVioletRed, PapayaWhip, SlateGray, Snow, SpringGreen, SteelBlue, Tan, Teal, SlateBlue, Thistle, Transparent, Turquoise, Violet, Wheat, White, WhiteSmoke, Tomato, LightSeaGreen, SkyBlue, Sienna, PeachPuff, Peru, Pink, Plum, PowderBlue, Purple, Silver, Red, RoyalBlue, SaddleBrown, Salmon, SandyBrown, SeaGreen, SeaShell, RosyBrown, Yellow, LightSalmon, LightGreen, DarkRed, DarkOrchid, DarkOrange, DarkOliveGreen, DarkMagenta, DarkKhaki, DarkGreen, DarkGray, DarkGoldenrod, DarkCyan, DarkBlue, Cyan, Crimson, Cornsilk, CornflowerBlue, Coral, Chocolate, AntiqueWhite, Aqua, Aquamarine, Azure, Beige, Bisque, DarkSalmon, Black, Blue, BlueViolet, Brown, BurlyWood, CadetBlue, Chartreuse, BlanchedAlmond, DarkSeaGreen, DarkSlateBlue, DarkSlateGray, HotPink, IndianRed, Indigo, Ivory, Khaki, Lavender, Honeydew, LavenderBlush, LemonChiffon, LightBlue, LightCoral, LightCyan, LightGoldenrodYellow, LightGray, LawnGreen, LightPink, GreenYellow, Gray, DarkTurquoise, DarkViolet, DeepPink, DeepSkyBlue, DimGray, DodgerBlue, Green, Firebrick, ForestGreen, Fuchsia, Gainsboro, GhostWhite, Gold, Goldenrod, FloralWhite, YellowGreen }

	    public interface IReplayWriter : IDisposable
	    {
		    void RegisterEntity(object obj, string? name, string path, string typename, string categoryname, Transform? initialTransofrm, Dictionary<string, string>? staticParameters = null);
		    void UnRegisterEntity(object obj);

		    void SetLog(object obj, string category, string log, Color color);
		    void SetDynamicParam(object obj, string key, string val);
		    void SetDynamicParam(object obj, string key, float val);

		    void StepFrame(float totalTime);
	    }

	    public class ReplayCaptureWriter : IReplayWriter, IDisposable
	    {
		    public ReplayCaptureWriter(string filePath)
		    {
			    var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
			    Writer = new BinaryReplayWriter(stream);
		    }

		    public ReplayCaptureWriter(Stream stream)
		    {
			    Writer = new BinaryReplayWriter(stream);
		    }

		    public void Dispose()
		    {
			    Writer?.Dispose();
			    Writer = null;
		    }

		    public void StepFrame(float totalTime)
		    {
			    Writer?.WriteFrameStep(FrameCounter, totalTime);
			    ++FrameCounter;
		    }

		    public void RegisterEntity(object obj, string? name = null, string path = "", string typename = "", string categoryname = "", Transform? initialTransform = null, Dictionary<string, string>? staticParameters = null)
		    {
			     if (!EntityMapping.TryGetValue(obj, out Entity? entity))
			    {
				    EntityCounter++;
				    entity = new Entity() { Id = EntityCounter };
				    Entities.Add(entity);
				    EntityMapping[obj] = entity;
			    }

			    entity.CreationFrame = FrameCounter;
			    entity.Name = name ?? obj.ToString() ?? string.Empty;
			    entity.Path = path;
			    entity.TypeName = typename;
			    entity.CategoryName = categoryname;
			    entity.InitialTransform = initialTransform ?? Transform.Identity;
			    entity.StaticParameters = staticParameters ?? new Dictionary<string, string>();
			    Writer?.WriteEntityDef(entity, FrameCounter);
		    }

		    public void UnRegisterEntity(object obj)
		    {
			    Writer?.WriteEntityUndef(GetEntity(obj), FrameCounter);
			    EntityMapping.Remove(obj);
		    }

		    public void SetLog(object obj, string category, string log, Color color) => SetLog(GetEntity(obj), category, log, color);
		    public void SetDynamicParam(object obj, string key, string val) => SetDynamicParam(GetEntity(obj), key, val);
		    public void SetDynamicParam(object obj, string key, float val) => SetDynamicParam(GetEntity(obj), key, val);

		    #region private

		    private List<Entity> Entities = new List<Entity>();
		    private Dictionary<object, Entity> EntityMapping = new Dictionary<object, Entity>();
		    private int EntityCounter;
		    private int FrameCounter;

		    private Dictionary<Entity, Dictionary<string, float>> LastParams = new Dictionary<Entity, Dictionary<string, float>>();

		    private BinaryReplayWriter? Writer;

		    object NullEntityObj = new object();
		    private Entity GetEntity(object obj)
		    {
			    if (obj == null) { return GetEntity(NullEntityObj); }
			    if (EntityMapping.TryGetValue(obj, out Entity? entity))
			    {
				    return entity;
			    }
			    else
			    {
				    // Auto-create
				    RegisterEntity(obj);
				    return EntityMapping[obj];
			    }
		    }

		    private void SetDynamicParam(Entity entity, string key, float val)
		    {
			    if (!LastParams.TryGetValue(entity, out var paramDict)) { paramDict = new Dictionary<string, float>(); LastParams[entity] = paramDict; }
			    if (!paramDict.TryGetValue(key, out float lastValue) || val != lastValue)
			    {
				    paramDict[key] = val;
				    Writer?.WriteEntityValue(entity, FrameCounter, key, val);
			    }
		    }

		    private void SetLog(Entity entity, string category, string log, Color color) => Writer?.WriteEntityLog(entity, FrameCounter, category, log ?? string.Empty, color);
		    private void SetDynamicParam(Entity entity, string key, string val) => Writer?.WriteEntityParameter(entity, FrameCounter, key, val ?? string.Empty);

		    #endregion //private
	    }

	    internal class BinaryReplayWriter
	    {
		    public static System.Text.Encoding StringEncoding => System.Text.Encoding.ASCII;

		    BinaryWriterEx? _writer;

		    public BinaryReplayWriter(Stream stream)
		    {
			    stream = new System.IO.Compression.DeflateStream(stream, System.IO.Compression.CompressionLevel.Fastest);
			    _writer = new BinaryWriterEx(stream, StringEncoding);
		    }

		    public void Dispose()
		    {
				_writer?.Flush();
				_writer?.Dispose();
			    _writer = null;
		    }

		    public void WriteFrameStep(int frame, float totalTime)
		    {
			    lock (this)
			    {
				    _writer?.Write7BitEncodedInt((int)BlockType.FrameStep);
				    //_writer?.Write(frame);
				    _writer?.Write(totalTime);
			    }
		    }

		    private void WriteEntityHeader(BlockType blockType, Entity entity, int frame)
		    {
			    _writer?.Write7BitEncodedInt((int)blockType);
			    _writer?.Write7BitEncodedInt(frame);
			    _writer?.Write7BitEncodedInt(entity?.Id ?? 0);
		    }

		    public void WriteEntityDef(Entity entity, int frame)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntityDef, entity, frame);
				    _writer?.Write(entity);
			    }
		    }

		    public void WriteEntityUndef(Entity entity, int frame)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntityUndef, entity, frame);
			    }
		    }

		    public void WriteEntitySetPos(Entity entity, int frame, Point pos)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntitySetPos, entity, frame);
				    _writer?.Write(pos);
			    }
		    }

		    public void WriteEntitySetTransform(Entity entity, int frame, Transform xform)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntitySetTransform, entity, frame);
				    _writer?.Write(xform);
			    }
		    }

		    public void WriteEntityLog(Entity entity, int frame, string category, string message, Color color)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntityLog, entity, frame);
				    _writer?.Write(category);
				    _writer?.Write(message);
				    _writer?.Write(color);
			    }
		    }

		    public void WriteEntityParameter(Entity entity, int frame, string label, string value)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntityParameter, entity, frame);
				    _writer?.Write(label);
				    _writer?.Write(value);
			    }
		    }

		    public void WriteEntityValue(Entity entity, int frame, string label, float value)
		    {
			    lock (this)
			    {
				    WriteEntityHeader(BlockType.EntityValue, entity, frame);
				    _writer?.Write(label);
				    _writer?.Write(value);
			    }
		    }
	    }

	    internal enum BlockType
	    {
		    None,
		    FrameStep,
		    EntityDef,
		    EntityUndef,
		    EntitySetPos,
		    EntitySetTransform,
		    EntityLog,
		    EntityParameter,
		    EntityValue,

		    ReplayHeader = 0xFF
	    }

	    public class Entity
	    {
		    public int Id;
		    public string Name = "";
		    public string Path = "";
		    public string TypeName = "";
		    public string CategoryName = "";
		    public Transform InitialTransform;
		    public Dictionary<string, string> StaticParameters = new();
		    public int CreationFrame;
	    }

	    internal static class BinaryIOExtensions
	    {
		    public static void Write(this BinaryWriterEx w, Entity entity)
		    {
			    w.Write7BitEncodedInt(entity.Id);
			    w.Write(entity.Name);
			    w.Write(entity.Path);
			    w.Write(entity.TypeName);
			    w.Write(entity.CategoryName);
			    w.Write(entity.InitialTransform);
			    w.Write(entity.StaticParameters);
			    w.Write7BitEncodedInt(entity.CreationFrame);
		    }

		    public static void Write(this BinaryWriter w, Point point)
		    {
			    w.Write(point.X);
			    w.Write(point.Y);
			    w.Write(point.Z);
		    }

		    public static void Write(this BinaryWriter w, Quaternion quat)
		    {
			    w.Write(quat.X);
			    w.Write(quat.Y);
			    w.Write(quat.Z);
			    w.Write(quat.W);
		    }

		    public static void Write(this BinaryWriter w, Transform xform)
		    {
			    w.Write(xform.Translation);
			    w.Write(xform.Rotation);
		    }

		    public static void Write(this BinaryWriterEx w, Dictionary<string, string> stringDict)
		    {
			    w.Write7BitEncodedInt(stringDict.Count);
			    foreach (var item in stringDict)
			    {
				    w.Write(item.Key);
				    w.Write(item.Value);
			    }
		    }

		    public static void Write(this BinaryWriterEx w, Color color)
		    {
			    w.Write7BitEncodedInt((int)color);
		    }
	    }

	    // 7BitEncodedInt marked protected in prior versions of .net
	    internal class BinaryWriterEx : BinaryWriter
	    {
		    public BinaryWriterEx(Stream input, System.Text.Encoding encoding) : base(input, encoding) { }
		    new public void Write7BitEncodedInt(int val) => base.Write7BitEncodedInt(val);
	    }

    }
	#endregion ReplayCapture

    internal class ResourceMonitor : IDisposable 
    {
		Process Proc { get; init; }
        int CpuCount { get; } = Environment.ProcessorCount;
		PerformanceCounter CpuCounter { get; init; }

		System.Timers.Timer PollingTimer { get; init; }

        public event Action<double>? CpuPct;

		private int _memoryWorkingSetMB = 0;
		public int MemoryWorkingSetMB
		{
			get => _memoryWorkingSetMB;
			set
			{
				if (value != _memoryWorkingSetMB)
				{
					_memoryWorkingSetMB = value;
					MemoryWorkingSetMBChanged?.Invoke(_memoryWorkingSetMB);
				}
			}
		}
		public event Action<int>? MemoryWorkingSetMBChanged;

		private int _threadCount = 0;
		public int ThreadCount
		{
			get => _threadCount;
			set
			{
				if (value != _threadCount)
				{
					_threadCount = value;
					ThreadCountChanged?.Invoke(_threadCount);
				}
			}
		}
		public event Action<int>? ThreadCountChanged;

		private string _childProcessesString = "";
        public string ChildProcessesString
        {
            get => _childProcessesString;
            set
            {
                if (value != _childProcessesString)
                {
					_childProcessesString = value;
                    ChildProcessesChanged?.Invoke(_childProcessesString);
				}
            }
        }
        public event Action<string>? ChildProcessesChanged;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
		public ResourceMonitor(TimeSpan delay)
        {
			Proc = Process.GetCurrentProcess();
			CpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            CpuCounter.NextValue(); // always zero

			PollingTimer = new() {  Interval = delay.TotalMilliseconds, AutoReset = true };
            PollingTimer.Elapsed += (o, e) =>
            {
                PollResourceMonitor();
			};
			PollingTimer.Start();
		}

		public void Dispose()
        {
			PollingTimer.Stop();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
		public void PollResourceMonitor()
        {
            var childProcesses = Proc.GetChildProcessesRecursive().ToList();

			double cpu = CpuCounter.NextValue();
			CpuPct?.Invoke(cpu);

			ChildProcessesString = string.Join(",", childProcesses.Select(x=>x.ProcessName));
            MemoryWorkingSetMB = (int)(childProcesses.Sum(x=>x.WorkingSet64) / (1024 * 1024));
            ThreadCount = childProcesses.Sum(x => x.Threads.Count);
		}
	}

	public static class ProcessExtensions
	{
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
		public static IEnumerable<Process> GetChildProcesses(this Process process)
			=> new ManagementObjectSearcher($"Select * From Win32_Process Where ParentProcessID={process.Id}")
				.Get()
				.Cast<ManagementObject>()
				.Select(mo => Process.GetProcessById(Convert.ToInt32(mo["ProcessID"])))
				.ToList();

        public static IEnumerable<Process> GetChildProcessesRecursive(this Process process)
        {
            yield return process;
            foreach (var childProcess in process.GetChildProcesses())
            {
                //yield return childProcess;
                foreach (var subchildProcess in childProcess.GetChildProcessesRecursive())
                {
                    yield return subchildProcess;
                }
            }
        }
	}

	public class DefaultScriptArguments
    {
        [CommandLine.Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.", Hidden = true)]
        public bool Verbose { get; set; }

        [CommandLine.Option('q', "quiet", Required = false, HelpText = "Inhibit all output.", Hidden = true)]
        public bool Quiet { get; set; }

        [CommandLine.Option('s', "scopes", Required = false, HelpText = "Show context scopes.", Hidden = true)]
        public bool Scopes { get; set; }

        [CommandLine.Option('S', "scopetimings", Required = false, HelpText = "Show context scope timings.", Hidden = true)]
        public bool ScopeTimings { get; set; }

        [CommandLine.Option('t', "timestamps", Required = false, HelpText = "Show log times.", Hidden = true)]
        public bool Times { get; set; }

        [CommandLine.Option('p', "pause", Required = false, HelpText = "Pause at end of script.", Hidden = true)]
        public bool Pause { get; set; }

        [CommandLine.Option("json",Required = false, HelpText = "Pass arguments as a json string (urlencoded)", Hidden = true)]
        public string JsonArguments { get; set; } = string.Empty;

        [CommandLine.Option("report", Required = false, HelpText = "Generate a report file with the specified name, loadable with VisualReplayDebugger.", Hidden = true)]
        public string Report { get; set; } = string.Empty;
    }

}
