using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Installation state of an app package during enrollment.
    /// Order matters: states are compared numerically for upgrade-only transitions.
    /// </summary>
    public enum AppInstallationState
    {
        Unknown = 0,
        NotInstalled = 1,
        InProgress = 2,
        Downloading = 3,
        Installing = 4,
        Installed = 5,
        Skipped = 6,
        Postponed = 7,
        Error = 8
    }

    /// <summary>
    /// How the app should run
    /// </summary>
    public enum AppRunAs
    {
        Unknown = -1,
        User = 0,
        System = 1
    }

    /// <summary>
    /// App install intent from Intune policy
    /// </summary>
    public enum AppIntent
    {
        Unknown = -1,
        NotTargeted = 0,
        Available = 1,
        Install = 3,
        Uninstall = 4
    }

    /// <summary>
    /// How the app is targeted
    /// </summary>
    [Flags]
    public enum AppTargeted
    {
        Unknown = 128,
        Dependency = 0,
        User = 1,
        Device = 2
    }

    /// <summary>
    /// Win32 app state values from IME
    /// </summary>
    public enum Win32AppState
    {
        Unknown = 0,
        NotInstalled = 1,
        InProgress = 2,
        Completed = 3,
        Error = 4
    }

    /// <summary>
    /// Represents the state of a single app package during enrollment.
    /// Adapted from EspOverlay PackageState with state transition protection.
    /// </summary>
    public class AppPackageState : IComparable<AppPackageState>
    {
        public static bool SortErrorsToTop = false;

        public string Id { get; }
        public int ListPos { get; }
        public string Name { get; private set; }
        public AppRunAs RunAs { get; private set; } = AppRunAs.Unknown;
        public AppIntent Intent { get; private set; } = AppIntent.Unknown;
        public AppTargeted Targeted { get; private set; } = AppTargeted.Unknown;
        public HashSet<string> DependsOn { get; private set; } = new HashSet<string>();
        public AppInstallationState InstallationState { get; private set; } = AppInstallationState.Unknown;
        public long InstallationStateLastChangedTicks { get; private set; } = 0;
        public bool DownloadingOrInstallingSeen { get; private set; } = false;
        public int? ProgressPercent { get; private set; } = null;
        public long BytesDownloaded { get; private set; } = 0;
        public long BytesTotal { get; private set; } = 0;

        private static readonly Dictionary<Win32AppState, AppInstallationState> Win32StateMap =
            new Dictionary<Win32AppState, AppInstallationState>
            {
                { Win32AppState.Unknown, AppInstallationState.Unknown },
                { Win32AppState.InProgress, AppInstallationState.InProgress },
                { Win32AppState.Completed, AppInstallationState.Installed },
                { Win32AppState.Error, AppInstallationState.Error }
            };

        private static readonly Dictionary<AppInstallationState, int> SortOrderOverrides =
            new Dictionary<AppInstallationState, int>
            {
                { AppInstallationState.Skipped, (int)AppInstallationState.Installed },
                { AppInstallationState.Postponed, (int)AppInstallationState.Installed },
                { AppInstallationState.Error, (int)AppInstallationState.Installed }
            };

        public AppPackageState(string id, int listPos)
        {
            Id = id;
            ListPos = listPos;
        }

        /// <summary>
        /// Updates the app name. Only updates if the new name is not a truncated version of the current name.
        /// </summary>
        public bool UpdateName(string newName)
        {
            if (string.IsNullOrEmpty(newName) || string.Equals(Name, newName))
                return false;

            // Avoid overwriting with a truncated version
            if (Name != null && newName.Length > 0 && Name.StartsWith(newName))
                return false;

            Name = newName;
            return true;
        }

        /// <summary>
        /// Updates RunAs property
        /// </summary>
        public bool UpdateRunAs(AppRunAs newRunAs)
        {
            if (RunAs == newRunAs) return false;
            RunAs = newRunAs;
            return true;
        }

        /// <summary>
        /// Updates Intent property
        /// </summary>
        public bool UpdateIntent(AppIntent newIntent)
        {
            if (Intent == newIntent) return false;
            Intent = newIntent;
            return true;
        }

        /// <summary>
        /// Updates Targeted property
        /// </summary>
        public bool UpdateTargeted(AppTargeted newTargeted)
        {
            if (Targeted == newTargeted) return false;
            Targeted = newTargeted;
            return true;
        }

        /// <summary>
        /// Updates DependsOn set
        /// </summary>
        public bool UpdateDependsOn(HashSet<string> newDependsOn)
        {
            if (newDependsOn == null) newDependsOn = new HashSet<string>();
            if (DependsOn.SetEquals(newDependsOn)) return false;
            DependsOn = newDependsOn;
            return true;
        }

        /// <summary>
        /// Updates the installation state with transition protection.
        /// Returns true if the state actually changed.
        /// </summary>
        public bool UpdateState(AppInstallationState newState, int? newProgressPercent = null, bool upgradeOnly = false, long bytesDownloaded = 0, long bytesTotal = 0)
        {
            // upgradeOnly: only allow "higher" states (used by Win32AppState mapping)
            if (upgradeOnly && newState < InstallationState)
                return false;

            // Cannot downgrade from Installed to Skipped or Postponed
            if (InstallationState == AppInstallationState.Installed &&
                (newState == AppInstallationState.Skipped || newState == AppInstallationState.Postponed))
                return false;

            // Postponed cannot go back to Downloading or lower active states
            if (InstallationState == AppInstallationState.Postponed && newState <= AppInstallationState.Downloading)
                return false;

            // If "Installed" without ever seeing download/install -> auto-downgrade to Skipped
            // This handles apps with "inverse" detection rules (e.g., uninstall packages marked as
            // installed when old software is NOT detected)
            if (newState == AppInstallationState.Installed && !DownloadingOrInstallingSeen)
                newState = AppInstallationState.Skipped;

            // Skip if no actual change
            if (newState == InstallationState && newProgressPercent == ProgressPercent && bytesDownloaded == BytesDownloaded && bytesTotal == BytesTotal)
                return false;

            var oldState = InstallationState;
            InstallationState = newState;
            InstallationStateLastChangedTicks = Stopwatch.GetTimestamp();
            DownloadingOrInstallingSeen |= (InstallationState >= AppInstallationState.Downloading &&
                                            InstallationState <= AppInstallationState.Installing);
            // For Installed state, set progress to 100% if no explicit value was given
            if (newState == AppInstallationState.Installed && newProgressPercent == null)
                ProgressPercent = 100;
            else
                ProgressPercent = newProgressPercent;
            // Only overwrite download bytes if new values are provided (non-zero),
            // otherwise preserve the last known download progress
            if (bytesDownloaded > 0 || bytesTotal > 0)
            {
                BytesDownloaded = bytesDownloaded;
                BytesTotal = bytesTotal;
            }

            // For successfully installed apps, ensure bytesDownloaded reflects completion.
            // WinGet always reports "bytes 0/<total>" so bytesDownloaded stays 0 even after success.
            if (InstallationState == AppInstallationState.Installed && BytesTotal > 0 && BytesDownloaded < BytesTotal)
                BytesDownloaded = BytesTotal;

            return true;
        }

        /// <summary>
        /// Updates state from a Win32AppState string or integer string (e.g., "2" or "InProgress").
        /// Uses upgrade-only mode to prevent "InProgress" from destroying more detailed states.
        /// </summary>
        public bool UpdateStateFromWin32AppState(string win32AppStateStringOrIntString)
        {
            Win32AppState win32State;
            if (Enum.TryParse(win32AppStateStringOrIntString, true, out win32State))
                return UpdateStateFromWin32AppState(win32State);
            return false;
        }

        /// <summary>
        /// Updates state from Win32AppState enum value.
        /// </summary>
        public bool UpdateStateFromWin32AppState(Win32AppState win32State)
        {
            AppInstallationState newState;
            if (Win32StateMap.TryGetValue(win32State, out newState))
                return UpdateState(newState, upgradeOnly: true);
            return false;
        }

        /// <summary>
        /// Whether this app requires installation (Install or Uninstall intent)
        /// </summary>
        public bool IsRequired => Intent == AppIntent.Install || Intent == AppIntent.Uninstall;

        /// <summary>
        /// Whether the app is currently being processed (InProgress, Downloading, or Installing)
        /// </summary>
        public bool IsActive => InstallationState >= AppInstallationState.InProgress &&
                                InstallationState <= AppInstallationState.Installing;

        /// <summary>
        /// Whether the app has reached a terminal state (Installed, Skipped, Postponed, Error)
        /// </summary>
        public bool IsCompleted => InstallationState >= AppInstallationState.Installed;

        /// <summary>
        /// Whether the app is in an error state
        /// </summary>
        public bool IsError => InstallationState == AppInstallationState.Error;

        /// <summary>
        /// Comparison for sorting: active apps first, then by state change time, then by list position.
        /// </summary>
        public int CompareTo(AppPackageState other)
        {
            // First: descending on sort order (active apps at top)
            var result = -(SortOrder.CompareTo(other.SortOrder));
            if (result == 0)
            {
                // Then: by installation state last changed
                result = InstallationStateLastChangedTicks.CompareTo(other.InstallationStateLastChangedTicks);
            }
            if (result == 0)
            {
                // Then: by original position in policy list
                result = ListPos.CompareTo(other.ListPos);
            }
            return result;
        }

        private int SortOrder
        {
            get
            {
                if (SortErrorsToTop && InstallationState == AppInstallationState.Error)
                    return -1;

                int sortOrder;
                if (SortOrderOverrides.TryGetValue(InstallationState, out sortOrder))
                    return sortOrder;

                return (int)InstallationState;
            }
        }

        /// <summary>
        /// Returns a summary dictionary for event data
        /// </summary>
        public Dictionary<string, object> ToEventData()
        {
            return new Dictionary<string, object>
            {
                { "appId", Id },
                { "name", Name ?? Id },
                { "appName", Name ?? Id },
                { "state", InstallationState.ToString() },
                { "intent", Intent.ToString() },
                { "targeted", Targeted.ToString() },
                { "runAs", RunAs.ToString() },
                { "progressPercent", ProgressPercent ?? 0 },
                { "bytesDownloaded", BytesDownloaded },
                { "bytesTotal", BytesTotal },
                { "isError", IsError },
                { "isCompleted", IsCompleted }
            };
        }
    }
}
