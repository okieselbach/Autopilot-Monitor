using System.Reflection;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Exposes the backend build identity (version, commit hash, build time) surfaced
    /// via /api/health and /api/health/detailed. Values come from the assembly's
    /// InformationalVersion attribute which MSBuild populates from &lt;Version&gt; and
    /// -p:SourceRevisionId (the latter is set by the deploy workflow to github.sha).
    /// </summary>
    public sealed class BackendBuildInfo
    {
        public string Version { get; }
        public string CommitHash { get; }
        public DateTime BuildUtc { get; }

        public BackendBuildInfo()
        {
            var asm = typeof(BackendBuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";

            (Version, CommitHash) = ParseInformationalVersion(info);

            try
            {
                var path = asm.Location;
                BuildUtc = !string.IsNullOrEmpty(path) && File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.UtcNow;
            }
            catch
            {
                BuildUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Splits an InformationalVersion ("1.0.0+abc1234567...") into a semver part
        /// and a 7-char short commit hash. No '+' → empty commit hash.
        /// </summary>
        internal static (string Version, string CommitHash) ParseInformationalVersion(string informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return ("0.0.0", string.Empty);
            }

            var plus = informationalVersion.IndexOf('+');
            if (plus < 0)
            {
                return (informationalVersion, string.Empty);
            }

            var version = informationalVersion.Substring(0, plus);
            var sha = informationalVersion.Substring(plus + 1);
            var shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;
            return (version, shortSha);
        }
    }
}
