// Helpers shared by run-variance.cs and run-judge.cs, which bring it in with #:include - not run on its own.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

/// <summary>The command line of a script: "--name value" pairs and bare "--name" switches, anything else refused. An
/// empty value means the option's default.</summary>
sealed class ScriptOptions
{
    private readonly Dictionary<string, string> _values = [];
    private readonly HashSet<string> _switches = [];

    public ScriptOptions(string[] args, string[] valueNames, string[] switchNames)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (switchNames.Contains(name))
            {
                _switches.Add(name);
            }
            else if (!valueNames.Contains(name))
            {
                EvalRun.Stop($"Unknown option '{name}' - the options are listed at the top of the script.");
            }
            else if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                EvalRun.Stop($"{name} needs a value.");
            }
            else
            {
                _values[name] = args[++i];
            }
        }
    }

    public string Value(string name, string defaultValue) =>
        _values.TryGetValue(name, out string? value) && value.Length > 0 ? value : defaultValue;

    public int IntValue(string name, int defaultValue)
    {
        if (!_values.TryGetValue(name, out string? value) || value.Length == 0)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int parsed))
        {
            EvalRun.Stop($"{name} needs a whole number, not '{value}'.");
        }

        return parsed;
    }

    public bool Switch(string name) => _switches.Contains(name);
}

/// <summary>What a script does around each dotnet command: its log, its Evaluation__ overrides, the command's own log.</summary>
sealed class EvalRun
{
    /// <summary>The three question sets, named explicitly: an unset Sets would fall back to the file's value.</summary>
    public const string AllSets = "Main,HeldOut,AnswerSide";

    /// <summary>How long to wait for a command's output to end once it has exited.</summary>
    private static readonly TimeSpan OutputDrain = TimeSpan.FromSeconds(10);

    private static readonly string Repo =
        Path.GetDirectoryName(AppContext.GetData("EntryPointFileDirectoryPath") as string
            ?? throw new InvalidOperationException("Run this as a file-based app: dotnet tools/<script>.cs"))!;

    public static readonly string Project = Path.Combine(Repo, "RagFilingExplorer.Evaluation");

    private static readonly string Logs = Path.Combine(Repo, "eval", "v3-runs", "logs");

    private readonly string _runLog;
    private readonly Dictionary<string, string> _overrides = [];

    public EvalRun(string runLogName)
    {
        Directory.CreateDirectory(Logs);
        _runLog = Path.Combine(Logs, runLogName);
    }

    /// <summary>Ends the script on something its user can fix: the message alone, no stack trace.</summary>
    [DoesNotReturn]
    public static void Stop(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(2);
    }

    public void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        File.AppendAllLines(_runLog, [line]);
        Console.WriteLine(line);
    }

    /// <summary>evalsettings.json holds a run's defaults; an Evaluation__&lt;key&gt; variable overrides a key for this run.</summary>
    public void Override(string key, string value) => _overrides[$"Evaluation__{key}"] = value;

    /// <summary>Only has no explicit "every question" value, so a script asking every question needs the file's Only empty.</summary>
    public static void RequireNoOnlyInSettings()
    {
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Project, "evalsettings.json")));
        string? only = settings.RootElement.GetProperty("Evaluation").GetProperty("Only").GetString();
        if (!string.IsNullOrEmpty(only))
        {
            Stop($"evalsettings.json sets Only to '{only}' - empty it, or pass --only, so this run asks what it says.");
        }
    }

    /// <summary>Builds once, so editing code while the run goes on doesn't change what's measured. MSBuild's reused nodes
    /// would outlive the build holding its output open.</summary>
    public int Build(string logName) => Dotnet(["build", Project, "--nologo", "-v", "quiet", "-nodeReuse:false"], logName);

    /// <summary>The project's tests matching <paramref name="filter"/>, with no rebuild.</summary>
    public int Test(string filter, string logName, bool detailed = false)
    {
        List<string> arguments = ["test", Project, "--no-build", "--nologo", "--filter", filter];
        if (detailed)
        {
            arguments.AddRange(["--logger", "console;verbosity=detailed"]);
        }

        return Dotnet(arguments, logName);
    }

    /// <summary>
    /// Runs dotnet with its output in one log, and returns its exit code - which alone decides, since stderr lines aren't
    /// failures. Inherited Evaluation__ variables are dropped: a run does what its script says, not what the shell had set.
    /// The output is copied as bytes, never decoded: on Windows dotnet test writes in the console's code page, and reading
    /// that as UTF-8 would replace bytes it can't decode for good (docs/Decision-Log.md, "Console.OutputEncoding fixed").
    /// </summary>
    private int Dotnet(IEnumerable<string> arguments, string logName)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = Repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string inherited in start.Environment.Keys.Where(k => k.StartsWith("Evaluation__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(inherited);
        }

        foreach ((string key, string value) in _overrides)
        {
            start.Environment[key] = value;
        }

        using FileStream log = new(Path.Combine(Logs, logName), FileMode.Create, FileAccess.Write, FileShare.Read);
        using Process process = Process.Start(start)!;
        Task[] copies = [CopyToLog(process.StandardOutput.BaseStream), CopyToLog(process.StandardError.BaseStream)];

        // Not WaitForExit() alone: with redirected output it also waits for the output to end, and a build server the
        // command leaves running can hold that open indefinitely.
        while (!process.WaitForExit(500))
        {
        }

        Task.WaitAll(copies, OutputDrain);

        return process.ExitCode;

        async Task CopyToLog(Stream output)
        {
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await output.ReadAsync(buffer)) > 0)
            {
                lock (log)
                {
                    log.Write(buffer, 0, read);
                    log.Flush();
                }
            }
        }
    }
}
