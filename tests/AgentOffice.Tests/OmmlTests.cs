using AgentOffice.Bridge;

namespace AgentOffice.Tests
{
    internal static class OmmlTests
    {
        internal static void Run()
        {
            Program.Equal("frac", "<m:f><m:num>a</m:num><m:den>b</m:den></m:f>",
                OmmlBuilder.Frac("a", "b"));
            Program.Equal("sup", "<m:sSup><m:e>x</m:e><m:sup>2</m:sup></m:sSup>",
                OmmlBuilder.Sup("x", "2"));
            Program.Equal("delim", "<m:d><m:e>x</m:e></m:d>", OmmlBuilder.Delim("x"));
            Program.True("run escapes markup",
                OmmlBuilder.Run("a<b&c", false).Contains("<m:r>a&lt;b&amp;c</m:r>"));
            Program.True("italic run is wrapped", OmmlBuilder.Run("x", true).StartsWith("<i "));
            Program.True("square root hides degree", OmmlBuilder.Rad(null, "x").Contains("m:degHide"));
            Program.True("nth root keeps degree",
                OmmlBuilder.Rad("3", "x").Contains("<m:deg>3</m:deg>"));

            Program.Equal("blank linear", null, LinearToOmml.Convert(" "));
            Program.True("linear fraction", LinearToOmml.Convert("a/b").Contains("<m:f>"));
            Program.True("linear superscript", LinearToOmml.Convert("x^2").Contains("<m:sSup>"));
            Program.True("linear subscript", LinearToOmml.Convert("a_i").Contains("<m:sSub>"));
            Program.True("linear root", LinearToOmml.Convert("√(x)").Contains("<m:rad>"));
        }
    }
}
