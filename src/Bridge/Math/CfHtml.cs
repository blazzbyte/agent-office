using System;
using System.Text;

namespace AgentOffice.Bridge
{
/// <summary>Wraps an OMML island in a CF_HTML ("HTML Format") clipboard payload, the exact envelope Excel's TextRange2.Paste().</summary>
    internal static class CfHtml
    {
        private const string Placeholder = "0000000000";

        private const string HeaderTemplate =
            "Version:1.0\r\n" +
            "StartHTML:{0}\r\n" +
            "EndHTML:{1}\r\n" +
            "StartFragment:{2}\r\n" +
            "EndFragment:{3}\r\n";

        private const string HtmlOpen =
            "<html xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
            "xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
            "xmlns:w=\"urn:schemas-microsoft-com:office:word\" " +
            "xmlns:m=\"http://schemas.microsoft.com/office/2004/12/omml\" " +
            "xmlns=\"http://www.w3.org/TR/REC-html40\">\r\n" +
            "<head>\r\n" +
            "<meta http-equiv=Content-Type content=\"text/html; charset=utf-8\">\r\n" +
            "</head>\r\n" +
            "<body>\r\n";

        private const string FragOpen = "<!--StartFragment-->";
        private const string FragClose = "<!--EndFragment-->";
        private const string IslandOpen = "<!--[if gte msEquation 12]><m:oMathPara><m:oMath>";
        private const string IslandClose = "</m:oMath></m:oMathPara><![endif]>";
        private const string HtmlClose = "\r\n</body>\r\n</html>\r\n";

        public static string FromOmml(string omml)
        {
            var tail = HtmlOpen + FragOpen + IslandOpen + omml + IslandClose + FragClose + HtmlClose;

            // Build once with fixed-width placeholders so offsets can be measured.
            var doc = string.Format(HeaderTemplate, Placeholder, Placeholder, Placeholder, Placeholder) + tail;

            var startHtml = ByteOffset(doc, doc.IndexOf("<html", StringComparison.Ordinal));
            var startFrag = ByteOffset(doc, doc.IndexOf(FragOpen, StringComparison.Ordinal));
            var endFrag = ByteOffset(doc, doc.IndexOf(FragClose, StringComparison.Ordinal) + FragClose.Length);
            var endHtml = ByteOffset(doc, doc.IndexOf("</html>", StringComparison.Ordinal) + "</html>".Length);

            return string.Format(
                HeaderTemplate,
                Pad(startHtml), Pad(endHtml), Pad(startFrag), Pad(endFrag)) + tail;
        }

        private static int ByteOffset(string s, int charIndex)
        {
            return Encoding.UTF8.GetByteCount(s.Substring(0, charIndex));
        }

        private static string Pad(int value)
        {
            return value.ToString("D10");
        }
    }
}
