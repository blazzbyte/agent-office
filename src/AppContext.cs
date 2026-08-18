using System;
using System.Diagnostics;

namespace AgentOffice
{
/// <summary>Which Office app hosts this add-in instance.</summary>
    internal enum HostApp
    {
        Excel,
        Word,
        PowerPoint,
    }

/// <summary>Host-app detection.</summary>
    internal static class AppContext
    {
        internal static readonly HostApp Current = Detect();

        private static HostApp Detect()
        {
            try
            {
                var name = Process.GetCurrentProcess().ProcessName;
                if (name.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
                    return HostApp.Word;
                if (name.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase))
                    return HostApp.PowerPoint;
            }
            catch
            {
                // Fall through to Excel — the historical default.
            }
            return HostApp.Excel;
        }

/// <summary>Stable lowercase id for wire payloads ("excel" | "word" | "powerpoint").</summary>
        internal static string CurrentId
        {
            get
            {
                switch (Current)
                {
                    case HostApp.Word: return "word";
                    case HostApp.PowerPoint: return "powerpoint";
                    default: return "excel";
                }
            }
        }

/// <summary>Human-facing name for prompts and logs.</summary>
        internal static string CurrentDisplayName
        {
            get
            {
                switch (Current)
                {
                    case HostApp.Word: return "Microsoft Word";
                    case HostApp.PowerPoint: return "Microsoft PowerPoint";
                    default: return "Microsoft Excel";
                }
            }
        }
    }
}
