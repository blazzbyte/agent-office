using System;
using System.IO;

namespace AgentOffice.Bridge
{
/// <summary>Export target resolution shared by every app's export actions.</summary>
    internal static class ExportPath
    {
/// <summary>Resolve the output path for an export.</summary>
        internal static string Resolve(string explicitPath, string baseName, string extension)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

            var dir = Taskpane.HostSettings.ExportDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "AgentOffice");
            }
            Directory.CreateDirectory(dir);

            var name = SanitizeFileName(baseName);
            if (name.Length == 0) name = "export";
            return Path.Combine(dir, name + extension);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
