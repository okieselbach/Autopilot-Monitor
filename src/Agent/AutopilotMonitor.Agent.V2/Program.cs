using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// V2-Agent entry point. Plan §4.x M4.5.b.
    /// <para>
    /// Start sequence:
    /// </para>
    /// <list type="number">
    ///   <item>Parse CLI args (<c>--help</c>, <c>--version</c>, <c>--console</c>, <c>--new-session</c>,
    ///     <c>--api-url</c>, <c>--bootstrap-token</c>, <c>--await-enrollment</c>, <c>--ime-log-path</c>,
    ///     <c>--ime-match-log</c>)</item>
    ///   <item>Initialise <see cref="AgentLogger"/> on <c>%ProgramData%\AutopilotMonitor\Logs</c></item>
    ///   <item>Resolve TenantId via <see cref="TenantIdResolver"/></item>
    ///   <item>Load/create SessionId via <see cref="SessionIdPersistence"/></item>
    ///   <item>(Optional) Wait for MDM certificate in await-enrollment mode</item>
    ///   <item>Build mTLS-<see cref="HttpClient"/> via <see cref="MtlsHttpClientFactory"/></item>
    ///   <item>Fetch remote config via <see cref="BackendApiClient"/> + <see cref="RemoteConfigService"/></item>
    ///   <item>Wire <see cref="BackendTelemetryUploader"/> for telemetry batch uploads</item>
    ///   <item>Construct <see cref="DefaultComponentFactory"/> + <see cref="EnrollmentOrchestrator"/></item>
    ///   <item><c>orchestrator.Start()</c> and wait for <c>Ctrl+C</c> / <see cref="AppDomain.ProcessExit"/></item>
    ///   <item><c>orchestrator.Stop()</c>, return 0</item>
    /// </list>
    /// </summary>
    public static class Program
    {
        private const string DefaultStateDirectory = @"%ProgramData%\AutopilotMonitor";
        private const string DefaultLogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";
        private const string DefaultV2StateSubdirectory = "V2";
        private const string DefaultTransportSubdirectory = "V2Transport";

        public static int Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintUsage();
                return 0;
            }

            if (args.Contains("--version"))
            {
                PrintVersion();
                return 0;
            }

            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            var dataDirectory = Environment.ExpandEnvironmentVariables(DefaultStateDirectory);
            var logDirectory = Environment.ExpandEnvironmentVariables(DefaultLogDirectory);

            var logger = new AgentLogger(logDirectory) { EnableConsoleOutput = consoleMode };
            logger.Info($"AutopilotMonitor.Agent.V2 starting (version {GetAgentVersion()}).");

            try
            {
                return RunAgent(args, logger, dataDirectory, consoleMode);
            }
            catch (Exception ex)
            {
                logger.Error("V2 agent startup failed.", ex);
                if (consoleMode) Console.Error.WriteLine($"FATAL: {ex.Message}");
                return 1;
            }
        }

        // ---------------------------------------------------------------- Orchestration

        private static int RunAgent(string[] args, AgentLogger logger, string dataDirectory, bool consoleMode)
        {
            if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);

            var tenantId = TenantIdResolver.ResolveFromEnrollmentRegistry(logger);
            if (string.IsNullOrEmpty(tenantId))
            {
                logger.Error("V2 agent cannot start: TenantId could not be resolved from the enrollment registry. Device is not MDM-enrolled.");
                return 2;
            }

            if (args.Contains("--new-session"))
            {
                new SessionIdPersistence(dataDirectory).Delete(logger);
                logger.Info("--new-session: cleared persisted SessionId.");
            }

            var sessionId = new SessionIdPersistence(dataDirectory).GetOrCreate(logger);

            var agentConfig = BuildAgentConfiguration(args, tenantId, sessionId);

            if (agentConfig.AwaitEnrollment)
            {
                logger.Info($"--await-enrollment: polling for MDM certificate (timeout: {agentConfig.AwaitEnrollmentTimeoutMinutes}min).");
                using (var cts = new CancellationTokenSource())
                {
                    var cert = EnrollmentAwaiter
                        .WaitForMdmCertificateAsync(
                            thumbprint: null,
                            timeoutMinutes: agentConfig.AwaitEnrollmentTimeoutMinutes,
                            logger: logger,
                            cancellationToken: cts.Token)
                        .GetAwaiter().GetResult();
                    if (cert == null)
                    {
                        logger.Error("Await-enrollment: timed out waiting for MDM certificate — exiting.");
                        return 3;
                    }
                }
            }

            var backendApiClient = new BackendApiClient(
                baseUrl: agentConfig.ApiBaseUrl,
                configuration: agentConfig,
                logger: logger,
                agentVersion: GetAgentVersion());

            var remoteConfigService = new RemoteConfigService(backendApiClient, tenantId, logger);
            var remoteConfig = remoteConfigService.FetchConfigAsync().GetAwaiter().GetResult();

            logger.SetLogLevel(agentConfig.LogLevel);

            HttpClient mtlsHttpClient;
            BackendTelemetryUploader uploader;
            try
            {
                mtlsHttpClient = MtlsHttpClientFactory.Create(
                    resolver: new DefaultCertificateResolver(),
                    logger: logger);
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("mTLS HttpClient creation failed — cannot upload telemetry.", ex);
                return 4;
            }

            try
            {
                var hardware = HardwareInfo.GetHardwareInfo(logger);
                uploader = new BackendTelemetryUploader(
                    httpClient: mtlsHttpClient,
                    baseUrl: agentConfig.ApiBaseUrl,
                    tenantId: tenantId,
                    manufacturer: hardware.Manufacturer,
                    model: hardware.Model,
                    serialNumber: hardware.SerialNumber,
                    bootstrapToken: agentConfig.UseBootstrapTokenAuth ? agentConfig.BootstrapToken : null,
                    agentVersion: GetAgentVersion());
            }
            catch (Exception ex)
            {
                logger.Error("BackendTelemetryUploader construction failed.", ex);
                return 5;
            }

            var stateSubdir = Path.Combine(dataDirectory, DefaultV2StateSubdirectory);
            var transportDir = Path.Combine(dataDirectory, DefaultTransportSubdirectory);

            var classifiers = new IClassifier[]
            {
                new WhiteGloveSealingClassifier(),
                new WhiteGlovePart2CompletionClassifier(),
            };

            var componentFactory = new DefaultComponentFactory(
                agentConfig: agentConfig,
                remoteConfig: remoteConfig,
                networkMetrics: backendApiClient.NetworkMetrics,
                agentVersion: GetAgentVersion(),
                stateDirectory: stateSubdir);

            var whiteGloveSealingPatternIds = (System.Collections.Generic.IReadOnlyCollection<string>)remoteConfig.WhiteGloveSealingPatternIds
                ?? Array.Empty<string>();

            using (var orchestrator = new EnrollmentOrchestrator(
                sessionId: sessionId,
                tenantId: tenantId,
                stateDirectory: stateSubdir,
                transportDirectory: transportDir,
                clock: SystemClock.Instance,
                logger: logger,
                uploader: uploader,
                classifiers: classifiers,
                componentFactory: componentFactory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds))
            {
                using (var shutdown = new ManualResetEventSlim(false))
                {
                    ConsoleCancelEventHandler cancelHandler = (s, e) =>
                    {
                        // Prevent the process from terminating immediately — let the orchestrator drain first.
                        e.Cancel = true;
                        logger.Info("Ctrl+C received — initiating graceful shutdown.");
                        shutdown.Set();
                    };
                    EventHandler processExitHandler = (s, e) =>
                    {
                        logger.Info("ProcessExit — initiating graceful shutdown.");
                        shutdown.Set();
                    };

                    Console.CancelKeyPress += cancelHandler;
                    AppDomain.CurrentDomain.ProcessExit += processExitHandler;

                    try
                    {
                        orchestrator.Start();
                        logger.Info($"V2 agent runtime ready (session={sessionId}, tenant={tenantId}).");
                        if (consoleMode)
                        {
                            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 running. Press Ctrl+C to stop.");
                        }

                        shutdown.Wait();
                    }
                    finally
                    {
                        Console.CancelKeyPress -= cancelHandler;
                        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

                        try { orchestrator.Stop(); }
                        catch (Exception ex) { logger.Error("Orchestrator stop failed.", ex); }

                        try { mtlsHttpClient.Dispose(); } catch { }
                        try { backendApiClient.Dispose(); } catch { }
                    }
                }
            }

            logger.Info("AutopilotMonitor.Agent.V2 stopped cleanly.");
            return 0;
        }

        // ---------------------------------------------------------------- Configuration

        private static AgentConfiguration BuildAgentConfiguration(string[] args, string tenantId, string sessionId)
        {
            var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;
            var imeLogPathOverride = GetArgValue(args, "--ime-log-path");
            var imeMatchLogPath = GetArgValue(args, "--ime-match-log");
            var bootstrapToken = GetArgValue(args, "--bootstrap-token");
            var awaitEnrollment = args.Contains("--await-enrollment");
            var rebootOnComplete = args.Contains("--reboot-on-complete");
            var disableGeoLocation = args.Contains("--disable-geolocation");
            var keepLogFile = args.Contains("--keep-logfile");

            var awaitTimeoutRaw = GetArgValue(args, "--await-enrollment-timeout");
            var awaitTimeoutMinutes = 480;
            if (!string.IsNullOrEmpty(awaitTimeoutRaw) && int.TryParse(awaitTimeoutRaw, out var parsedTimeout))
                awaitTimeoutMinutes = parsedTimeout;

            var useBootstrapTokenAuth = !string.IsNullOrEmpty(bootstrapToken);

            return new AgentConfiguration
            {
                ApiBaseUrl = apiBaseUrl,
                SessionId = sessionId,
                TenantId = tenantId,
                SpoolDirectory = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                LogDirectory = Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                UploadIntervalSeconds = Constants.DefaultUploadIntervalSeconds,
                MaxBatchSize = Constants.MaxBatchSize,
                LogLevel = AgentLogLevel.Info,
                UseClientCertAuth = !useBootstrapTokenAuth,
                BootstrapToken = bootstrapToken,
                UseBootstrapTokenAuth = useBootstrapTokenAuth,
                AwaitEnrollment = awaitEnrollment,
                AwaitEnrollmentTimeoutMinutes = awaitTimeoutMinutes,
                RebootOnComplete = rebootOnComplete,
                EnableGeoLocation = !disableGeoLocation,
                ImeLogPathOverride = imeLogPathOverride,
                ImeMatchLogPath = imeMatchLogPath,
                KeepLogFile = keepLogFile,
                CommandLineArgs = FormatArgsForLog(args),
            };
        }

        private static string GetArgValue(string[] args, params string[] names)
        {
            if (args == null || args.Length < 2 || names == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                foreach (var name in names)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                }
            }
            return null;
        }

        private static string FormatArgsForLog(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;

            var parts = new System.Collections.Generic.List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--bootstrap-token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    parts.Add(a);
                    parts.Add("[redacted]");
                    i++;
                    continue;
                }
                parts.Add(a);
            }
            return string.Join(" ", parts);
        }

        // ---------------------------------------------------------------- Info

        private static void PrintUsage()
        {
            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 — Autopilot-Monitor V2 agent.");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Usage:");
            Console.Out.WriteLine("  AutopilotMonitor.Agent.V2.exe [options]");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Options:");
            Console.Out.WriteLine("  --help, -h, -?             Show this message and exit");
            Console.Out.WriteLine("  --version                  Print version and exit");
            Console.Out.WriteLine("  --console                  Mirror log output to stdout");
            Console.Out.WriteLine("  --new-session              Discard any persisted SessionId and create a fresh one");
            Console.Out.WriteLine("  --api-url <url>            Override backend base URL (alias: --backend-api)");
            Console.Out.WriteLine("  --bootstrap-token <tok>    Use pre-MDM bootstrap token auth instead of client cert");
            Console.Out.WriteLine("  --await-enrollment         Wait for MDM certificate before starting");
            Console.Out.WriteLine("  --await-enrollment-timeout <min>  Await-enrollment timeout (default: 480min)");
            Console.Out.WriteLine("  --ime-log-path <dir>       Override IME log folder (testing/replay)");
            Console.Out.WriteLine("  --ime-match-log <path>     Write IME pattern matches to this file");
            Console.Out.WriteLine("  --reboot-on-complete       Reboot device after successful enrollment");
            Console.Out.WriteLine("  --disable-geolocation      Disable external IP geo-location lookup");
            Console.Out.WriteLine("  --keep-logfile             Preserve the agent log file on self-destruct");
        }

        private static void PrintVersion()
        {
            Console.Out.WriteLine(GetAgentVersion());
        }

        private static string GetAgentVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;

                var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(file)) return file;

                return asm.GetName().Version?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }
    }
}
