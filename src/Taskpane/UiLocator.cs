using System;
using System.IO;
using System.Reflection;

namespace AgentOffice.Taskpane
{
/// <summary>Locates the bundled web UI (the built React app) on disk.</summary>
    internal static class UiLocator
    {
        public static string Find()
        {
            var overrideDir = Environment.GetEnvironmentVariable("AGENT_OFFICE_UI_DIR");
            if (!string.IsNullOrEmpty(overrideDir) && IsUiDir(overrideDir))
                return Path.GetFullPath(overrideDir);

            var addinDir = AddinDirectory();
            var installed = Path.Combine(addinDir, "ui");
            if (IsUiDir(installed))
                return installed;

            var dir = new DirectoryInfo(addinDir);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "ui", "dist");
                if (IsUiDir(candidate))
                    return candidate;
            }

            // Nothing found — return the installed path anyway so the caller
            // can log a precise "UI not found" error instead of a null ref.
            return installed;
        }

        public static string AddinDirectory()
        {
            var codebase = new Uri(Assembly.GetExecutingAssembly().CodeBase);
            return Path.GetDirectoryName(codebase.LocalPath);
        }

        private static bool IsUiDir(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   Directory.Exists(path) &&
                   File.Exists(Path.Combine(path, "index.html"));
        }
    }
}
