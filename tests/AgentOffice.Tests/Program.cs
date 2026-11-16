using System;
using System.Collections.Generic;

namespace AgentOffice.Tests
{
    internal static class Program
    {
        private static int _failed;
        private static int _passed;

        private static int Main()
        {
            var suites = new Dictionary<string, Action>
            {
                ["LatexConverter"] = LatexConverterTests.Run,
                ["Omml"] = OmmlTests.Run,
                ["CfHtml"] = CfHtmlTests.Run,
                ["Agent"] = AgentTests.Run,
            };

            foreach (var suite in suites)
            {
                try
                {
                    suite.Value();
                }
                catch (Exception ex)
                {
                    _failed++;
                    Console.WriteLine("FAIL " + suite.Key + " threw " + ex);
                }
            }

            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        internal static void Equal(string name, object expected, object actual)
        {
            Record(name, Equals(expected, actual),
                "expected <" + expected + "> but got <" + actual + ">");
        }

        internal static void True(string name, bool condition)
        {
            Record(name, condition, "condition was false");
        }

        private static void Record(string name, bool ok, string detail)
        {
            if (ok)
            {
                _passed++;
                return;
            }
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + detail);
        }
    }
}
