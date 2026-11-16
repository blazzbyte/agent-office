using AgentOffice.Bridge;

namespace AgentOffice.Tests
{
    internal static class LatexConverterTests
    {
        internal static void Run()
        {
            Program.Equal("blank input", null, LatexConverter.ToLinear("  "));
            Program.Equal("display wrapper", "x^(2)", LatexConverter.ToLinear("$$x^{2}$$"));
            Program.Equal("inline wrapper", "a_(i)", LatexConverter.ToLinear("$a_{i}$"));
            Program.Equal("bracket wrapper", "x", LatexConverter.ToLinear("\\[ x \\]"));
            Program.Equal("fraction", "(a)/(b)", LatexConverter.ToLinear("\\frac{a}{b}"));
            Program.Equal("fraction single tokens", "(1)/(2)", LatexConverter.ToLinear("\\frac12"));
            Program.Equal("nested fraction", "((a)/(b))/(c)",
                LatexConverter.ToLinear("\\frac{\\frac{a}{b}}{c}"));
            Program.Equal("square root", "√(x)", LatexConverter.ToLinear("\\sqrt{x}"));
            Program.Equal("nth root", "\\root(3\\of(x))", LatexConverter.ToLinear("\\sqrt[3]{x}"));
            Program.Equal("root inside fraction", "(1)/(√(f))",
                LatexConverter.ToLinear("\\frac{1}{\\sqrt{f}}"));
            Program.Equal("symbols", "α + β", LatexConverter.ToLinear("\\alpha + \\beta"));
            Program.Equal("unknown command kept", "\\foo", LatexConverter.ToLinear("\\foo"));
            Program.Equal("literal slash escaped", "a\\/b", LatexConverter.ToLinear("a/b"));
            Program.Equal("text command", "speed", LatexConverter.ToLinear("\\text{speed}"));
            Program.Equal("left right", "(x)", LatexConverter.ToLinear("\\left(x\\right)"));
        }
    }
}
