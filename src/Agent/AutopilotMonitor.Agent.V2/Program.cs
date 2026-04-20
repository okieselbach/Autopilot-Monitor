using System;
using System.Linq;
using System.Reflection;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// V2-Agent entry point — M2 skeleton.
    /// <para>
    /// Plan §4 M2 release gate: "V2-Agent-Exe startet + terminiert sauber (ohne echtes Enrollment).
    /// Harness durchläuft leeren Signal-Stream deterministisch."
    /// </para>
    /// <para>
    /// Real startup wiring (orchestrator, collectors, signal ingress, transport) lands in M4.
    /// </para>
    /// </summary>
    public static class Program
    {
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

            // M2 skeleton: exits cleanly. No enrollment logic, no collectors, no IO.
            // Additional modes (--install, --run-gather-rules, --run-ime-matching, normal runtime)
            // will be added as the corresponding V2 subsystems come online in M3–M6.
            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 skeleton — M2. No operational mode wired yet.");
            Console.Out.WriteLine("Run with --help for available flags.");
            return 0;
        }

        private static void PrintUsage()
        {
            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 — Autopilot-Monitor V2 agent (kernel-only refactor).");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Usage:");
            Console.Out.WriteLine("  AutopilotMonitor.Agent.V2.exe [--help | --version]");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Operational modes (install, gather-rules, ime-matching, runtime) are added in M3–M6.");
        }

        private static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version?.ToString() ?? "0.0.0.0";
            Console.Out.WriteLine(version);
        }
    }
}
