
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net.NetworkInformation;

public partial class Program
{
    const string DEFAULT_PROCESS = "OpenVPNConnect";
    const int DEFAULT_INTERVAL = 15;
    const int DEFAULT_FAILURES = 5;
    const int DEFAULT_TIMEOUTSECS = 3;
    const int DEFAULT_AUXSKIPPERIODS = 3;
    const int RESTART_DELAYSECS = 5;
    static readonly List<string> DEFAULT_DESTINATIONS = new List<string>()
    {
        "1.1.1.1",
        "8.8.8.8",
        "208.67.222.222",
    };

    static readonly string HEADER_ART = @" _____                       _         __          __   _       _               " + Environment.NewLine +
                             @"|  __ \                     | |        \ \        / /  | |     | |              " + Environment.NewLine +
                             @"| |__) |___ _ __   ___  __ _| |_ ___ _ _\ \  /\  / /_ _| |_ ___| |__   ___ _ __ " + Environment.NewLine +
                             @"|  _  // _ \ '_ \ / _ \/ _` | __/ _ \ '__\ \/  \/ / _` | __/ __| '_ \ / _ \ '__|" + Environment.NewLine +
                             @"| | \ \  __/ |_) |  __/ (_| | ||  __/ |   \  /\  / (_| | || (__| | | |  __/ |   " + Environment.NewLine +
                             @"|_|  \_\___| .__/ \___|\__,_|\__\___|_|    \/  \/ \__,_|\__\___|_| |_|\___|_|   " + Environment.NewLine +
                             @"            | |                                                                  " + Environment.NewLine +
                             @"            |_|                 ";

    internal static ILogger? _logger = null;

    internal static int _consecutiveFailures = 0;
    internal static long _intervalsPassed = 0;

    internal static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning)
                .AddFilter("NonHostConsoleApp.Program", LogLevel.Debug)
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "MM/dd/yyyy hh:mm:ss ";
                })
                .AddFile("RepeaterWatcher.log", fileLoggerOptions =>
                {
                    fileLoggerOptions.MaxRollingFiles = 7;
                    fileLoggerOptions.FileSizeLimitBytes = 10485760; // 10 MB
                });
        });
        _logger = loggerFactory.CreateLogger(Environment.MachineName);

        int returnCode = 0;

        Console.WriteLine(HEADER_ART);

        _logger?.LogInformation("RepeaterWatcher by M0XDR");

        RootCommand cmd = new RootCommand("Repeater watcher performs ICMP ping commands at set intervals and kills and restarts a process if failures reach a specified threshold.");

        Option<string> processOption = new Option<string>(new[] { "-p", "--process" }, 
            () => DEFAULT_PROCESS, 
            "The process to kill and restart.");

        Option<IEnumerable<string>> destinationOption = new Option<IEnumerable<string>>(new[] { "-d", "--destinations" }, 
            () => DEFAULT_DESTINATIONS, 
            "One or more destination IP or addresses to ping.") { AllowMultipleArgumentsPerToken = true };

        Option<int> intervalOption = new Option<int>(new[] { "-i", "--interval" }, 
            () => DEFAULT_INTERVAL, 
            "The number of seconds between tests.");

        Option<int> failuresOption = new Option<int>(new[] { "-f", "--failures" }, 
            () => DEFAULT_FAILURES, 
            "The number consecutive failures before restarting.");

        Option<int> timeoutOption = new Option<int>(new[] { "-t", "--timeout" }, 
            () => DEFAULT_TIMEOUTSECS, 
            "The ping timeout in seconds.");

        Option<FileInfo> auxInfoOption = new Option<FileInfo>(new[] { "-a", "--aux" }, "The path to an auxillary process to run each period.");

        Option<int> auxSkipPeriods = new Option<int>(new[] { "-s", "--skip" },
            () => DEFAULT_AUXSKIPPERIODS,
            "The number of intervals to skip before running the aux process.");

        Option<string> auxArguments = new Option<string>(new[] { "-x", "--auxargs" },
            "An argument string for the auxillary process.");

        Argument<IEnumerable<string>> argumentsOption = new Argument<IEnumerable<string>>("restartArguments", "The arguments which will be passed to the restarted process.")
        {
            Arity = ArgumentArity.OneOrMore
        };

        cmd.AddArgument(argumentsOption);
        cmd.AddOption(processOption);
        cmd.AddOption(destinationOption);
        cmd.AddOption(intervalOption);
        cmd.AddOption(failuresOption);
        cmd.AddOption(timeoutOption);
        cmd.AddOption(auxInfoOption);
        cmd.AddOption(auxSkipPeriods);
        cmd.AddOption(auxArguments);

        cmd.SetHandler(async (IEnumerable<string> restartArguments, string process, IEnumerable<string> destinations, int interval, int failures, int timeout, FileInfo auxInfo, int auxSkipPeriods, string auxArguments, CancellationToken token) =>
        {
            returnCode = await HandleCommandLineAsync(restartArguments, process, destinations, interval, failures, timeout, auxInfo, auxSkipPeriods, auxArguments, token);
        }, argumentsOption, processOption, destinationOption, intervalOption, failuresOption, timeoutOption, auxInfoOption, auxSkipPeriods, auxArguments);

        return await cmd.InvokeAsync(args);
    }

    private static async Task<int> HandleCommandLineAsync(
        IEnumerable<string> restartArguments,
        string process,
        IEnumerable<string> destinations,
        int interval,
        int failures,
        int timeout,
        FileInfo auxInfo, 
        int auxSkipPeriods,
        string auxArguments,
        CancellationToken token)
    {
        List<string> testAddresses = destinations.ToList();

        AuxInfo aux = new AuxInfo()
        {
            ProcessFileInfo = auxInfo,
            SkipPeriods = auxSkipPeriods,
            Arguments = auxArguments
        };

        _consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await TestLoopAsync(restartArguments, process, interval, timeout, failures, testAddresses, aux, token);
            }
            catch (TaskCanceledException)
            {
                _logger?.LogError("Test loop has been cancelled.");

                return 1;
            }
            catch (Exception ex)
            {
                _logger?.LogError("An exception {exception} occured. Sleeping for 30 seconds.", ex);

                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }

            _intervalsPassed++;
        }

        return 0;
    }

    /// <summary>
    /// Perform a test and then any remedial action
    /// </summary>
    private static async Task TestLoopAsync(IEnumerable<string> restartArguments, string process, int interval, int timeout, int maxFailures, List<string> testAddresses, AuxInfo aux, CancellationToken token)
    {
        (int SuccessCount, int FailCount) pingResults = await PerformPingChecksAsync(timeout, testAddresses, token);

        if (pingResults.SuccessCount == 0)
        {
            // We increment failures only if NONE of the tests succeeded
            _consecutiveFailures++;
            _logger?.LogWarning("Consecutive failures now stands at {failures}", _consecutiveFailures);

            if (_consecutiveFailures >= maxFailures)
            {
                // We need to take some action as the maximum number of failures has been reached.
                _consecutiveFailures = 0;

                _logger?.LogError("Consecutive failures limit {maxFailures} has been reached.", maxFailures);

                if (await RestartProcessAsync(process, restartArguments))
                {
                    _logger?.LogInformation($"Giving process time to start up before monitoring is resumed...");

                    await Task.Delay(TimeSpan.FromSeconds(RESTART_DELAYSECS));
                }
            }
        }
        else
        {
            if (_consecutiveFailures > 0)
            {
                _logger?.LogInformation("At least one ping test succeeded. Resetting consecutive failure count.");
                _consecutiveFailures = 0;
            }
        }

        _logger?.LogInformation($"Sleeping for {interval} {(interval == 1 ? "second" : "seconds")}", interval);

        // Deal with aux process
        if (aux.ProcessFileInfo?.Exists == true)
        {
            if (_intervalsPassed % aux.SkipPeriods == 0)
            {
                try
                {
                    _logger?.LogInformation("Auxilary process {auxillaryProcess} found and running this period.", aux.ProcessFileInfo.Name);

                    ProcessStartInfo auxProcStartInfo = new ProcessStartInfo(aux.ProcessFileInfo.FullName, aux?.Arguments ?? string.Empty);

                    Process parent = Process.GetCurrentProcess();
                    ProcessPriorityClass original = parent.PriorityClass;

                    try
                    {
                        // Start using a lower priority
                        parent.PriorityClass = ProcessPriorityClass.BelowNormal;

                        Process? auxProcess = Process.Start(auxProcStartInfo);

                        if (auxProcess != null)
                        {
                            _logger?.LogInformation("Auxilary started with ID {processId}", auxProcess.Id);
                        }
                        else
                        {
                            _logger?.LogError("Auxilary process at could not be started", aux?.ProcessFileInfo.Name ?? string.Empty);
                        }
                    }
                    finally
                    {
                        parent.PriorityClass = original;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError("Auxilary process at {processFileInfo} could not be started. Exception {exception}", aux.ProcessFileInfo.Name, ex);
                }
            }
        }
        else
        {
            _logger?.LogWarning("Auxilary process specified {auxillaryProcess} can not be found", aux?.ProcessFileInfo?.FullName ?? "<unknown>");
        }

        await Task.Delay(interval * 1000, token);
    }

    /// <summary>
    /// Restart a process
    /// </summary>
    /// <param name="process">process name e.g. openvpnconnect</param>
    /// <param name="restartArguments">args to pass to process</param>
    private static async Task<bool> RestartProcessAsync(string process, IEnumerable<string> restartArguments)
    {
        Process[] managedProcesses = Process.GetProcessesByName(process);

        if (managedProcesses.Length > 0)
        {
            _logger?.LogWarning($"There is {managedProcesses.Length} matching {(managedProcesses.Length == 1 ? "process" : "processes")} found", managedProcesses.Length);

            // Find most likely image path
            string? imagePath = managedProcesses.Select(p => p.MainModule?.FileName)
                .GroupBy(p => p)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault()?.Key;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                _logger?.LogError("No image path found for processes matching name {process}. The process will not be killed or restarted.", process);
                return false;
            }

            foreach (Process candidate in managedProcesses)
            {
                _logger?.LogWarning("Performing kill of process {candidate}", candidate.Id);

                candidate.Kill();
            }

            _logger?.LogInformation("Restarting process at {imagePath} in {restartDelay} seconds", imagePath, RESTART_DELAYSECS);

            await Task.Delay(TimeSpan.FromSeconds(RESTART_DELAYSECS));

            string commandLineArgs = string.Join(" ", restartArguments.Select(arg => arg));

            try
            {
                Process? startedProcess = Process.Start(new ProcessStartInfo(imagePath, commandLineArgs));

                if (startedProcess != null)
                {
                    _logger?.LogInformation("Started process {imagePath} successfully. Process ID is {processId}", imagePath, startedProcess?.Id);

                    return true;
                }
            }
            catch (Exception exception)
            {
                _logger?.LogError("Process could not be started using executable at {imagePath}. An exception occured {exception}", imagePath, exception);

                return false;
            }

            _logger?.LogError("Process could not be started using executable at {imagePath}", imagePath);

            return false;
        }
        else
        {
            _logger?.LogWarning("No processes matching name {name} found. No processes will be closed or killed", process);
        }

        return true;
    }


    /// <summary>
    /// Ping a number of hosts and return the results
    /// </summary>
    private static async Task<(int SuccessCount, int FailCount)> PerformPingChecksAsync(int timeout, List<string> testAddresses, CancellationToken token)
    {
        _logger?.LogInformation($"Performing ping checks to {testAddresses.Count} {(testAddresses.Count == 1 ? "host" : "hosts")}...");

        List<Task<PingReply>> pingChecks = new List<Task<PingReply>>();

        foreach (string testAddress in testAddresses)
        {
            Task<PingReply> pingTest = Task.Run(async () =>
            {
                Ping pingSender = new Ping();

                PingReply pingReply = await pingSender.SendPingAsync(testAddress, timeout * 1000);

                _logger?.LogInformation($"{testAddress} - {pingReply.Status}{(pingReply.Status == IPStatus.Success ? " - Round Trip: " + pingReply.RoundtripTime + "ms" : string.Empty)}");

                return pingReply;
            }, token);

            pingChecks.Add(pingTest);
        }

        PingReply[] pingResults = await Task.WhenAll(pingChecks);

        int successCount = pingResults.Count(result => result.Status == IPStatus.Success);
        int failCount = pingResults.Length - successCount;

        if (failCount > 0 && successCount > 0)
        {
            _logger?.LogWarning($"{successCount} {(successCount == 1 ? "ping" : "pings")} were successful, but {failCount} {(failCount == 1 ? "ping" : "pings")} failed", successCount, failCount);
        }
        else if (failCount > 0 && successCount == 0)
        {
            _logger?.LogError($"{successCount} {(successCount == 1 ? "ping" : "pings")} were successful, but {failCount} {(failCount == 1 ? "ping" : "pings")} failed", successCount, failCount);
        }
        else
        {
            _logger?.LogInformation($"{successCount} {(successCount == 1 ? "ping" : "pings")} were successful", successCount);
        }

        return (successCount, failCount);
    }

    internal class AuxInfo
    {
        public FileInfo? ProcessFileInfo { get; set; }

        public string? Arguments { get; set; }

        public int SkipPeriods { get; set; }
    }
}