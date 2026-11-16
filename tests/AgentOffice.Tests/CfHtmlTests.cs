using System;
using System.Text;
using System.Text.RegularExpressions;
using AgentOffice.Bridge;

namespace AgentOffice.Tests
{
    internal static class CfHtmlTests
    {
        internal static void Run()
        {
            // Non-ASCII content: offsets must count UTF-8 bytes, not chars.
            var payload = CfHtml.FromOmml("<m:r>α√β</m:r>");
            var bytes = Encoding.UTF8.GetBytes(payload);

            var startHtml = Offset(payload, "StartHTML");
            var endHtml = Offset(payload, "EndHTML");
            var startFragment = Offset(payload, "StartFragment");
            var endFragment = Offset(payload, "EndFragment");

            Program.True("StartHTML points at <html",
                Slice(bytes, startHtml, endHtml).StartsWith("<html", StringComparison.Ordinal));
            Program.True("EndHTML points past </html>",
                Slice(bytes, startHtml, endHtml).EndsWith("</html>", StringComparison.Ordinal));

            var fragment = Slice(bytes, startFragment, endFragment);
            Program.True("fragment starts at its marker",
                fragment.StartsWith("<!--StartFragment-->", StringComparison.Ordinal));
            Program.True("fragment ends at its marker",
                fragment.EndsWith("<!--EndFragment-->", StringComparison.Ordinal));
            Program.True("fragment carries the equation", fragment.Contains("<m:r>α√β</m:r>"));
        }

        private static int Offset(string payload, string key)
        {
            return int.Parse(Regex.Match(payload, key + @":(\d{10})").Groups[1].Value);
        }

        private static string Slice(byte[] bytes, int start, int end)
        {
            return Encoding.UTF8.GetString(bytes, start, end - start);
        }
    }
}
