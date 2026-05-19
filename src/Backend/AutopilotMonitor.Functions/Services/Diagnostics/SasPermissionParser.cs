using System;
using System.Web;

namespace AutopilotMonitor.Functions.Services.Diagnostics
{
    /// <summary>
    /// Parses Azure Storage SAS permission strings (the <c>sp=</c> query parameter)
    /// and answers permission-presence questions. Used by the cascade-delete worker to
    /// decide whether it can clean up a customer's diagnostics blob — if the customer's
    /// SAS lacks the Delete (<c>d</c>) permission we log + skip; the customer remains
    /// responsible for their own lifecycle rules.
    /// <para>
    /// Pure static helper, no dependencies. Tolerant of malformed input — every
    /// parse-or-extract failure returns <c>false</c> so the caller's "skip-delete"
    /// branch is taken rather than throwing in the middle of a cascade.
    /// </para>
    /// </summary>
    public static class SasPermissionParser
    {
        /// <summary>
        /// Returns true when the SAS URL's <c>sp=</c> permission string contains the
        /// Delete flag (<c>d</c>). Returns false on null/empty input, missing query
        /// string, missing <c>sp</c> parameter, or any parse error.
        /// </summary>
        public static bool HasDelete(string? sasUrl)
            => HasPermission(sasUrl, 'd');

        /// <summary>
        /// Generic permission probe. Accepts a single character (one of
        /// <c>r w c d l a u</c> etc.) and answers case-insensitively. Exposed so
        /// future callers can ask about other permissions without re-implementing
        /// the parse.
        /// </summary>
        public static bool HasPermission(string? sasUrl, char permissionFlag)
        {
            if (string.IsNullOrEmpty(sasUrl)) return false;
            try
            {
                var queryIndex = sasUrl!.IndexOf('?');
                if (queryIndex < 0) return false;

                var query = HttpUtility.ParseQueryString(sasUrl.Substring(queryIndex));
                var sp = query["sp"];
                if (string.IsNullOrEmpty(sp)) return false;

                // SAS permissions are a flat character set: e.g. "rwdlac" for full,
                // "rwc" for write-only-with-create. Case-insensitive per Azure spec.
                var needle = char.ToLowerInvariant(permissionFlag);
                foreach (var c in sp)
                {
                    if (char.ToLowerInvariant(c) == needle) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
