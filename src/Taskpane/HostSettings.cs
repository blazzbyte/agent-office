using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace AgentOffice.Taskpane
{
/// <summary>Host-side settings, persisted at %LOCALAPPDATA%\AgentOffice\settings.json.</summary>
    internal static class HostSettings
    {
        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentOffice");

        private static readonly string SettingsFile =
            Path.Combine(DataDirectory, "settings.json");

        private static readonly Lazy<Dictionary<string, object>> Values =
            new Lazy<Dictionary<string, object>>(LoadFresh);

/// <summary>Whether the chat taskpane was visible when Excel last closed (toggled via the Agent Office ribbon button or the pane's.</summary>
        public static bool PaneVisible
        {
            get
            {
                object raw;
                if (Values.Value != null &&
                    Values.Value.TryGetValue("paneVisible", out raw) && raw is bool)
                {
                    return (bool)raw;
                }
                return true;
            }
        }

/// <summary>Persist the pane visibility choice.</summary>
        public static void SavePaneVisible(bool visible)
        {
            SaveMutate(dict => dict["paneVisible"] = visible);
        }

/// <summary>Optional export target folder (settings.json "exportDir" key).</summary>
        public static string ExportDirectory
        {
            get
            {
                object raw;
                var fresh = LoadFresh();
                if (fresh != null &&
                    fresh.TryGetValue("exportDir", out raw) && raw is string)
                {
                    var value = Environment.ExpandEnvironmentVariables(
                        ((string)raw).Trim());
                    if (value.Length > 0) return value;
                }
                return null;
            }
        }

/// <summary>Standing instructions appended to every request (settings.json "customInstructions": string or array of strings).</summary>
        public static string CustomInstructions
        {
            get
            {
                object raw;
                var fresh = LoadFresh();
                if (fresh == null || !fresh.TryGetValue("customInstructions", out raw))
                    return null;

                var single = raw as string;
                if (single != null)
                {
                    single = single.Trim();
                    return single.Length > 0 ? single : null;
                }

                var items = raw as System.Collections.IEnumerable;
                if (items == null) return null;
                var lines = new List<string>();
                foreach (var item in items)
                {
                    var text = item as string;
                    if (text != null && text.Trim().Length > 0)
                        lines.Add("- " + text.Trim());
                }
                return lines.Count > 0 ? string.Join("\n", lines) : null;
            }
        }

        // ------------------------------------------------------------------
        // Shared file access (also used by Agent.ProviderSettings)
        // ------------------------------------------------------------------

/// <summary>Read settings.json fresh from disk (null when absent/invalid).</summary>
        internal static Dictionary<string, object> LoadFresh()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                var json = File.ReadAllText(SettingsFile);
                return new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Failed to read host settings (" + SettingsFile + ")", ex);
                return null;
            }
        }

/// <summary>Re-read the file, run on the dictionary and write it back — unrelated keys survive every writer.</summary>
        internal static void SaveMutate(Action<Dictionary<string, object>> mutate)
        {
            try
            {
                var dict = LoadFresh() ?? new Dictionary<string, object>();
                mutate(dict);
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(SettingsFile,
                    new JavaScriptSerializer().Serialize(dict));
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Failed to save host settings (" + SettingsFile + ")", ex);
            }
        }
    }
}
