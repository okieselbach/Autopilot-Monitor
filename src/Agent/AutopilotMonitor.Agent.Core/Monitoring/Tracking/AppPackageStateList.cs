using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Manages a collection of app package states during enrollment.
    /// Tracks current app, ignore list, and dependency cascading.
    /// Adapted from EspOverlay PackageStatesList.
    /// </summary>
    public class AppPackageStateList : List<AppPackageState>
    {
        /// <summary>
        /// The ID of the app currently being processed by IME
        /// </summary>
        public string CurrentPackageId { get; set; }

        /// <summary>
        /// Apps to ignore (e.g., user-targeted apps during device phase)
        /// </summary>
        public List<string> IgnoreList { get; } = new List<string>();

        private readonly AgentLogger _logger;

        // States that should also cascade to dependents
        private static readonly HashSet<AppInstallationState> StatesToCascade =
            new HashSet<AppInstallationState>
            {
                AppInstallationState.Postponed,
                AppInstallationState.Error
            };

        public AppPackageStateList(AgentLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets a package by ID, optionally creating it if not found.
        /// Returns null if the ID is in the ignore list.
        /// </summary>
        public AppPackageState GetPackage(string id, bool createIfNotFound = false)
        {
            if (string.IsNullOrEmpty(id) || IgnoreList.Contains(id))
                return null;

            var result = this.SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (result == null && createIfNotFound)
            {
                result = new AppPackageState(id, Count);
                Add(result);
            }
            return result;
        }

        /// <summary>
        /// Adds/updates packages from a JSON policy list (from IME "Get policies" log line).
        /// Returns true if any package was updated.
        /// </summary>
        public bool AddUpdateFromJsonPolicies(List<Dictionary<string, object>> jsonPolicies, bool ignoreUserTargeted)
        {
            if (jsonPolicies == null) return false;

            var updated = false;
            foreach (var policy in jsonPolicies)
            {
                string id;
                object idObj;
                if (!policy.TryGetValue("Id", out idObj) || (id = idObj?.ToString()) == null)
                    continue;

                // Check RunAs - ignore user-targeted during device phase
                if (ignoreUserTargeted)
                {
                    var runAs = GetRunAsFromPolicy(policy);
                    if (runAs == AppRunAs.User)
                    {
                        updated |= AddToIgnoreList(id);
                        continue;
                    }
                }

                var pkg = GetPackage(id, createIfNotFound: true);
                if (pkg == null) continue;

                // Update name
                object nameObj;
                if (policy.TryGetValue("Name", out nameObj))
                    updated |= pkg.UpdateName(nameObj?.ToString());

                // Update intent
                object intentObj;
                if (policy.TryGetValue("Intent", out intentObj))
                {
                    AppIntent intent;
                    if (Enum.TryParse(intentObj.ToString(), true, out intent))
                        updated |= pkg.UpdateIntent(intent);
                }

                // Update targeted
                object targetObj;
                if (policy.TryGetValue("TargetType", out targetObj))
                {
                    AppTargeted targeted;
                    if (Enum.TryParse(targetObj.ToString(), true, out targeted))
                        updated |= pkg.UpdateTargeted(targeted);
                }

                // Update RunAs from InstallEx
                object installExObj;
                if (policy.TryGetValue("InstallEx", out installExObj))
                {
                    var runAs = ExtractRunAsFromInstallEx(installExObj);
                    if (runAs != AppRunAs.Unknown)
                        updated |= pkg.UpdateRunAs(runAs);
                }

                // Update dependencies from FlatDependencies
                object flatDepsObj;
                if (policy.TryGetValue("FlatDependencies", out flatDepsObj))
                {
                    var deps = ExtractDependencies(flatDepsObj, id);
                    if (deps != null)
                        updated |= pkg.UpdateDependsOn(deps);
                }
            }

            if (updated)
            {
                Sort();
                LogStates();
            }

            return updated;
        }

        /// <summary>
        /// Updates the name of a specific package
        /// </summary>
        public bool UpdateName(string id, string newName)
        {
            var pkg = GetPackage(id);
            if (pkg == null) return false;
            var updated = pkg.UpdateName(newName);
            if (updated) LogStates();
            return updated;
        }

        /// <summary>
        /// Updates the installation state of a specific package.
        /// Returns true if state actually changed.
        /// Optionally cascades to dependent packages for error/postpone states.
        /// </summary>
        public bool UpdateState(string id, AppInstallationState newState, int? progressPercent = null)
        {
            var packageIds = new HashSet<string> { id };

            // Cascade error/postpone to dependents
            if (StatesToCascade.Contains(newState))
                packageIds.UnionWith(GetDependentIdsDeep(id));

            var packages = packageIds.Select(x => GetPackage(x)).Where(x => x != null).ToList();

            var result = false;
            foreach (var pkg in packages)
                result |= pkg.UpdateState(newState, progressPercent);

            if (result)
            {
                AppPackageState.SortErrorsToTop = IsAllCompleted();
                Sort();
            }

            return result;
        }

        /// <summary>
        /// Updates state to Downloading with optional progress tracking
        /// </summary>
        public bool UpdateStateToDownloading(string id, string bytesDownloadedStr, string totalBytesStr)
        {
            int? progressPercent = null;
            long bytesDownloaded = 0;
            long bytesTotal = 0;

            try
            {
                bytesDownloaded = Convert.ToInt64(bytesDownloadedStr);
                bytesTotal = Convert.ToInt64(totalBytesStr);
                if (bytesTotal != 0)
                    progressPercent = (int)Math.Round((double)bytesDownloaded / bytesTotal * 100);
            }
            catch { }

            var pkg = GetPackage(id);
            if (pkg != null &&
                pkg.InstallationState != AppInstallationState.Skipped &&
                pkg.InstallationState != AppInstallationState.Installed)
            {
                var result = pkg.UpdateState(AppInstallationState.Downloading, progressPercent, false, bytesDownloaded, bytesTotal);
                if (result)
                {
                    AppPackageState.SortErrorsToTop = IsAllCompleted();
                    Sort();
                }
                return result;
            }

            return false;
        }

        /// <summary>
        /// Updates state from a Win32AppState string
        /// </summary>
        public bool UpdateStateFromWin32AppState(string id, string win32AppStateString)
        {
            var pkg = GetPackage(id);
            if (pkg == null) return false;
            var updated = pkg.UpdateStateFromWin32AppState(win32AppStateString);
            if (updated)
            {
                AppPackageState.SortErrorsToTop = IsAllCompleted();
                Sort();
            }
            return updated;
        }

        /// <summary>
        /// Sets the current package being processed
        /// </summary>
        public void SetCurrent(string id)
        {
            CurrentPackageId = id;
        }

        /// <summary>
        /// Adds an ID to the ignore list
        /// </summary>
        public bool AddToIgnoreList(string id)
        {
            if (string.IsNullOrEmpty(id) || IgnoreList.Contains(id))
                return false;

            IgnoreList.Add(id);
            return true;
        }

        /// <summary>
        /// Checks if all required apps are completed
        /// </summary>
        public bool IsAllCompleted()
        {
            if (!this.Any()) return false;

            var allRequiredComplete = this.Where(x => x.IsRequired).All(x => x.IsCompleted);
            if (allRequiredComplete)
            {
                // Mark dependency-only packages that IME never touched as Skipped
                foreach (var pkg in this.Where(x => !x.IsRequired && x.InstallationState == AppInstallationState.Unknown))
                    pkg.UpdateState(AppInstallationState.Skipped);
            }
            return allRequiredComplete;
        }

        /// <summary>
        /// Gets the first non-completed package (for display)
        /// </summary>
        public AppPackageState ActivePackage => this.FirstOrDefault(x => !x.IsCompleted);

        public int CountAll => Count;
        public int CountCompleted => this.Count(x => x.IsCompleted);
        public bool HasError => this.Any(x => x.IsError);
        public int ErrorCount => this.Count(x => x.IsError);

        /// <summary>
        /// Gets summary data for event emission
        /// </summary>
        public Dictionary<string, object> GetSummaryData()
        {
            return new Dictionary<string, object>
            {
                { "totalApps", CountAll },
                { "completedApps", CountCompleted },
                { "errorCount", ErrorCount },
                { "hasErrors", HasError },
                { "isAllCompleted", IsAllCompleted() },
                { "ignoredCount", IgnoreList.Count },
                { "apps", this.Select(x => x.ToEventData()).ToList() }
            };
        }

        /// <summary>
        /// Recursively gets all dependent package IDs (packages that depend on the given parent)
        /// </summary>
        private HashSet<string> GetDependentIdsDeep(string parentId, int level = 0)
        {
            var result = new HashSet<string>();
            try
            {
                level++;
                if (level > 10)
                {
                    _logger?.Warning("Dependency tree deeper than 10 levels - possible circular dependency");
                    return result;
                }

                foreach (var dependent in this.Where(x => x.DependsOn.Contains(parentId)).Select(x => x.Id))
                {
                    result.Add(dependent);
                    result.UnionWith(GetDependentIdsDeep(dependent, level));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Error resolving dependencies for {parentId}: {ex.Message}");
            }

            return result;
        }

        private static AppRunAs GetRunAsFromPolicy(Dictionary<string, object> policy)
        {
            try
            {
                object installExObj;
                if (policy.TryGetValue("InstallEx", out installExObj))
                    return ExtractRunAsFromInstallEx(installExObj);
            }
            catch { }
            return AppRunAs.Unknown;
        }

        private static AppRunAs ExtractRunAsFromInstallEx(object installExObj)
        {
            try
            {
                if (installExObj is Dictionary<string, object> installEx)
                {
                    object runAsObj;
                    if (installEx.TryGetValue("RunAs", out runAsObj))
                        return (AppRunAs)Convert.ToInt32(runAsObj);
                }
                // Handle as JSON string
                var str = installExObj?.ToString();
                if (!string.IsNullOrEmpty(str))
                {
                    var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(str);
                    if (json != null)
                    {
                        object runAsObj;
                        if (json.TryGetValue("RunAs", out runAsObj))
                            return (AppRunAs)Convert.ToInt32(runAsObj.ToString());
                    }
                }
            }
            catch { }
            return AppRunAs.Unknown;
        }

        private static HashSet<string> ExtractDependencies(object flatDepsObj, string appId)
        {
            try
            {
                if (flatDepsObj == null) return new HashSet<string>();

                var deps = flatDepsObj as List<object>;
                if (deps == null) return new HashSet<string>();

                var result = new HashSet<string>();
                foreach (var dep in deps)
                {
                    var depDict = dep as Dictionary<string, object>;
                    if (depDict == null) continue;

                    object actionObj, appIdObj, childIdObj, typeObj, levelObj;
                    if (depDict.TryGetValue("Action", out actionObj) &&
                        depDict.TryGetValue("AppId", out appIdObj) &&
                        depDict.TryGetValue("ChildId", out childIdObj) &&
                        depDict.TryGetValue("Type", out typeObj) &&
                        depDict.TryGetValue("Level", out levelObj))
                    {
                        if (Convert.ToInt32(actionObj) == 10 &&
                            string.Equals(appIdObj?.ToString(), appId, StringComparison.OrdinalIgnoreCase) &&
                            Convert.ToInt32(typeObj) == 0 &&
                            Convert.ToInt32(levelObj) == 0)
                        {
                            result.Add(childIdObj.ToString());
                        }
                    }
                }
                return result;
            }
            catch { }
            return new HashSet<string>();
        }

        private void LogStates()
        {
            if (_logger == null) return;
            var stateList = string.Join(", ", this.Select(x => $"{x.Name ?? x.Id}:{x.InstallationState}"));
            _logger.Debug($"App states [{Count}]: {stateList}");
        }
    }
}
