namespace AgentOffice.Bridge
{
/// <summary>Emits OMML in the HTML-encoded dialect that Office apps place on the clipboard (CF_HTML) and that Excel's.</summary>
    internal static class OmmlBuilder
    {
        private const string RunStyle =
            "font-size:11.0pt;line-height:107%;font-family:\"Cambria Math\",serif";

/// <summary>A single text run.</summary>
        public static string Run(string text, bool italic)
        {
            var span = "<span style='" + RunStyle + "'><m:r>" + Escape(text) + "</m:r></span>";
            return italic
                ? "<i style='mso-bidi-font-style:normal'>" + span + "</i>"
                : span;
        }

        public static string Sup(string baseXml, string supXml)
        {
            return "<m:sSup><m:e>" + baseXml + "</m:e><m:sup>" + supXml + "</m:sup></m:sSup>";
        }

        public static string Sub(string baseXml, string subXml)
        {
            return "<m:sSub><m:e>" + baseXml + "</m:e><m:sub>" + subXml + "</m:sub></m:sSub>";
        }

        public static string SubSup(string baseXml, string subXml, string supXml)
        {
            return "<m:sSubSup><m:e>" + baseXml + "</m:e><m:sub>" + subXml +
                   "</m:sub><m:sup>" + supXml + "</m:sup></m:sSubSup>";
        }

        public static string Frac(string numXml, string denXml)
        {
            return "<m:f><m:num>" + numXml + "</m:num><m:den>" + denXml + "</m:den></m:f>";
        }

/// <summary>Radical.</summary>
        public static string Rad(string degXml, string eXml)
        {
            if (string.IsNullOrEmpty(degXml))
            {
                return "<m:rad><m:radPr><m:degHide m:val=\"on\"/></m:radPr><m:deg/><m:e>" +
                       eXml + "</m:e></m:rad>";
            }
            return "<m:rad><m:deg>" + degXml + "</m:deg><m:e>" + eXml + "</m:e></m:rad>";
        }

/// <summary>Delimiter group with default parentheses (renders visible, stretchy parens).</summary>
        public static string Delim(string innerXml)
        {
            return "<m:d><m:e>" + innerXml + "</m:e></m:d>";
        }

        private static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
