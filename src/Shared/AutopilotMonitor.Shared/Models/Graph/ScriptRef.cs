using System;

namespace AutopilotMonitor.Shared.Models.Graph
{
    /// <summary>
    /// Kind of Intune script the agent saw. Maps to Graph endpoint:
    /// <list type="bullet">
    ///   <item><see cref="Platform"/> → <c>/deviceManagement/deviceManagementScripts/{id}</c></item>
    ///   <item><see cref="Remediation"/> → <c>/deviceManagement/deviceHealthScripts/{id}</c></item>
    /// </list>
    /// </summary>
    public enum ScriptKind
    {
        Platform = 0,
        Remediation = 1,
    }

    /// <summary>
    /// A typed reference to a single Intune script. Used as the key for display-name lookups,
    /// cache rows, and the API contract between Web -> Backend. Defined as a struct so it can
    /// be used as a dictionary key with structural equality, while staying compatible with
    /// netstandard2.0 + C# 9 (no record struct).
    /// </summary>
    public readonly struct ScriptRef : IEquatable<ScriptRef>
    {
        public ScriptKind Kind { get; }
        public string Id { get; }

        public ScriptRef(ScriptKind kind, string id)
        {
            Kind = kind;
            Id = id ?? string.Empty;
        }

        /// <summary>Serialised form used in URL query parameters and cache row keys.</summary>
        public override string ToString() => Kind + ":" + Id;

        public bool Equals(ScriptRef other) => Kind == other.Kind
            && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is ScriptRef other && Equals(other);

        public override int GetHashCode() => unchecked(
            ((int)Kind * 397) ^ (Id == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Id)));

        public static bool operator ==(ScriptRef left, ScriptRef right) => left.Equals(right);
        public static bool operator !=(ScriptRef left, ScriptRef right) => !left.Equals(right);

        /// <summary>
        /// Parses the canonical <c>"{Kind}:{Id}"</c> form. Returns false on malformed input.
        /// Accepts <c>"Platform"</c>/<c>"platform"</c>/<c>"Remediation"</c>/<c>"remediation"</c> case-insensitively.
        /// </summary>
        public static bool TryParse(string? input, out ScriptRef value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var idx = input!.IndexOf(':');
            if (idx <= 0 || idx == input.Length - 1) return false;

            var kindStr = input.Substring(0, idx).Trim();
            var idStr = input.Substring(idx + 1).Trim();
            if (idStr.Length == 0) return false;

            ScriptKind kind;
            if (string.Equals(kindStr, "Platform", StringComparison.OrdinalIgnoreCase)) kind = ScriptKind.Platform;
            else if (string.Equals(kindStr, "Remediation", StringComparison.OrdinalIgnoreCase)) kind = ScriptKind.Remediation;
            else return false;

            value = new ScriptRef(kind, idStr);
            return true;
        }
    }
}
