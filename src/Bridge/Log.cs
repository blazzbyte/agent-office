using System;
using System.IO;

namespace AgentOffice.Bridge
{
/// <summary>Tiny append-only file logger.</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();

        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentOffice");

        private static string LogFile
        {
            get { return Path.Combine(LogDirectory, "host.log"); }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}{4}",
                        DateTime.Now,
                        level,
                        message,
                        ex != null ? " | " + ex : string.Empty,
                        Environment.NewLine);
                    File.AppendAllText(LogFile, line);
                }
            }
            catch
            {
                // Logging must never throw inside the add-in.
            }
        }
    }
}
