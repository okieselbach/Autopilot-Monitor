using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class RegistryCollector : IGatherRuleCollector
    {
        public string CollectorType => "registry";

        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var data = new Dictionary<string, object>();
            var path = rule.Target;

            if (string.IsNullOrEmpty(path))
                return data;

            // Determine hive
            RegistryKey hive = null;
            string subPath = path;

            if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.LocalMachine;
                subPath = path.Substring(path.IndexOf('\\') + 1);
            }
            else if (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.CurrentUser;
                subPath = path.Substring(path.IndexOf('\\') + 1);
            }
            else
            {
                // Default to HKLM
                hive = Registry.LocalMachine;
            }

            // Guard: only allow enrollment-relevant registry paths
            if (!GatherRuleGuards.IsRegistryPathAllowed(subPath, context.UnrestrictedMode))
                return context.EmitSecurityWarning(rule, "registry", path);

            // Determine explicit valueName from parameters
            string explicitValueName = null;
            rule.Parameters?.TryGetValue("valueName", out explicitValueName);

            // Check if subkey enumeration is requested
            string listSubkeysStr = null;
            rule.Parameters?.TryGetValue("listSubkeys", out listSubkeysStr);
            bool listSubkeys = string.Equals(listSubkeysStr, "true", StringComparison.OrdinalIgnoreCase);

            // Optional conditional severity based on existence
            string severityIfExists = null;
            string severityIfNotExists = null;
            rule.Parameters?.TryGetValue("severityIfExists", out severityIfExists);
            rule.Parameters?.TryGetValue("severityIfNotExists", out severityIfNotExists);

            try
            {
                using (var key = hive.OpenSubKey(subPath, false))
                {
                    if (key == null)
                    {
                        // The key doesn't exist as a sub-key. If no explicit valueName was given,
                        // check whether the last path segment is a value name in the parent key.
                        // This handles the common case where the user specifies the full
                        // "HKLM\...\KeyName\ValueName" path directly in Target.
                        if (string.IsNullOrEmpty(explicitValueName) && !listSubkeys)
                        {
                            var lastBackslash = subPath.LastIndexOf('\\');
                            if (lastBackslash > 0)
                            {
                                var parentSubPath = subPath.Substring(0, lastBackslash);
                                var inferredValueName = subPath.Substring(lastBackslash + 1);

                                using (var parentKey = hive.OpenSubKey(parentSubPath, false))
                                {
                                    if (parentKey != null)
                                    {
                                        var valueNames = parentKey.GetValueNames();
                                        if (Array.Exists(valueNames, n => string.Equals(n, inferredValueName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            var value = parentKey.GetValue(inferredValueName);
                                            data["exists"] = true;
                                            data["path"] = path.Substring(0, path.LastIndexOf('\\'));
                                            data[inferredValueName] = value?.ToString();
                                            return data;
                                        }
                                    }
                                }
                            }
                        }

                        data["exists"] = false;
                        data["path"] = path;
                        return data;
                    }

                    data["exists"] = true;
                    data["path"] = path;

                    if (listSubkeys)
                    {
                        // Enumerate subkey names (max 100)
                        var subKeyNames = key.GetSubKeyNames().Take(100).ToArray();
                        data["subkey_count"] = subKeyNames.Length;
                        for (int i = 0; i < subKeyNames.Length; i++)
                        {
                            data[$"subkey_{i + 1}"] = subKeyNames[i];
                        }
                    }
                    else if (!string.IsNullOrEmpty(explicitValueName))
                    {
                        // Read specific value if specified in parameters
                        var value = key.GetValue(explicitValueName);
                        data[explicitValueName] = value?.ToString();
                    }
                    else
                    {
                        // Read all values
                        foreach (var name in key.GetValueNames().Take(50)) // Limit to 50 values
                        {
                            try
                            {
                                var value = key.GetValue(name);
                                var displayName = string.IsNullOrEmpty(name) ? "(Default)" : name;
                                data[displayName] = value?.ToString();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            // Apply conditional severity based on existence
            if (data.ContainsKey("exists"))
            {
                var exists = data["exists"] is bool b ? b : data["exists"]?.ToString() == "True";
                if (exists && !string.IsNullOrEmpty(severityIfExists))
                    data["_severityOverride"] = severityIfExists;
                else if (!exists && !string.IsNullOrEmpty(severityIfNotExists))
                    data["_severityOverride"] = severityIfNotExists;
            }

            return data;
        }
    }
}
