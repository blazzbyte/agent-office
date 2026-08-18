using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOffice.Bridge
{
/// <summary>Best-effort LaTeX to Word linear format (UnicodeMath) converter.</summary>
    internal static class LatexConverter
    {
/// <summary>Sentinel standing in for a protected backslash (survives the symbol pass).</summary>
        private const char BackslashSentinel = '';

/// <summary>Shared with LinearToOmml (\command lookup in direct linear input).</summary>
        internal static readonly Dictionary<string, string> Symbols = new Dictionary<string, string>
        {
            // Greek (lowercase)
            ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
            ["epsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ",
            ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
            ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ",
            ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ",
            ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
            // Greek variants
            ["varepsilon"] = "ε", ["varphi"] = "φ", ["vartheta"] = "ϑ", ["varrho"] = "ϱ",
            ["varsigma"] = "ς", ["varpi"] = "ϖ", ["varkappa"] = "ϰ",
            // Letter-like symbols
            ["ell"] = "ℓ", ["hbar"] = "ℏ", ["imath"] = "ı", ["jmath"] = "ȷ",
            ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ", ["wp"] = "℘",
            // Greek (uppercase)
            ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
            ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ",
            ["Psi"] = "Ψ", ["Omega"] = "Ω",
            // Operators & relations
            ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["cdot"] = "⋅", ["div"] = "÷",
            ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥",
            ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["equiv"] = "≡", ["propto"] = "∝",
            ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",
            ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
            ["sum"] = "∑", ["prod"] = "∏", ["sqrt"] = "√",
            ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["supset"] = "⊃",
            ["subseteq"] = "⊆", ["supseteq"] = "⊇", ["cup"] = "∪", ["cap"] = "∩",
            ["forall"] = "∀", ["exists"] = "∃", ["emptyset"] = "∅",
            ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←",
            ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐", ["Leftrightarrow"] = "⇔",
            ["mapsto"] = "↦", ["degree"] = "°", ["prime"] = "′",
            ["ldots"] = "…", ["cdots"] = "⋯", ["dots"] = "…",
            // Geometry, logic & more operators
            ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥", ["mid"] = "∣",
            ["sim"] = "∼", ["simeq"] = "≃", ["cong"] = "≅",
            ["otimes"] = "⊗", ["oplus"] = "⊕", ["odot"] = "⊙",
            ["circ"] = "∘", ["bullet"] = "∙", ["star"] = "⋆",
            ["dagger"] = "†", ["ddagger"] = "‡",
            ["land"] = "∧", ["wedge"] = "∧", ["lor"] = "∨", ["vee"] = "∨",
            ["neg"] = "¬", ["lnot"] = "¬", ["setminus"] = "∖",
            ["models"] = "⊨", ["vdash"] = "⊢", ["top"] = "⊤", ["bot"] = "⊥",
            ["therefore"] = "∴", ["because"] = "∵",
        };

        public static string ToLinear(string latex)
        {
            if (string.IsNullOrWhiteSpace(latex)) return null;
            var s = latex.Trim();

            // Strip display-math wrappers: $$..$$, \[..\], $..$
            if (s.StartsWith("$$") && s.EndsWith("$$") && s.Length > 4) s = s.Substring(2, s.Length - 4).Trim();
            if (s.StartsWith("\\[") && s.EndsWith("\\]") && s.Length > 4) s = s.Substring(2, s.Length - 4).Trim();
            if (s.StartsWith("$") && s.EndsWith("$") && s.Length > 2 && !s.StartsWith("$$")) s = s.Substring(1, s.Length - 2).Trim();

            // Text/style commands: keep the content
            s = Regex.Replace(s, @"\\(?:text|mathrm|mathbf|mathit|mathsf|mathcal|operatorname)\s*\{([^{}]*)\}", "$1");
            s = Regex.Replace(s, @"\\(?:displaystyle|limits)\b", "");

            // \left / \right: drop the command, keep the delimiter that follows
            s = Regex.Replace(s, @"\\left\s*", "");
            s = Regex.Replace(s, @"\\right\s*", "");

            // Spacing commands
            s = Regex.Replace(s, @"\\[,;!]", "");
            s = Regex.Replace(s, @"\\quad|\\qquad", " ");

            // Structural constructs (\frac, \sqrt) via the scanning parser (recursion-safe).
            // The parser emits sentinel-protected \root / \of for nth roots.
            s = ConvertStructural(s);
            s = Regex.Replace(s, @"\\([A-Za-z]+)", delegate (Match m)
            {
                string sym;
                return Symbols.TryGetValue(m.Groups[1].Value, out sym) ? sym : m.Value;
            });

            // Restore protected backslashes (\root, \of).
            s = s.Replace(BackslashSentinel, '\\');

            // Scripts with braced groups: x^{2} -> x^(2), a_{i} -> a_(i)
            s = Regex.Replace(s, @"\^\s*\{([^{}]+)\}", "^($1)");
            s = Regex.Replace(s, @"_\s*\{([^{}]+)\}", "_($1)");

            // Remaining braces act as grouping -> parentheses
            s = s.Replace("{", "(").Replace("}", ")");

            // Tidy spacing
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        // ------------------------------------------------------------------
        // Structural parser: \frac{..}{..} and \sqrt[..]{..} / \sqrt{..} / \sqrt x
        // ------------------------------------------------------------------

        private static string ConvertStructural(string s)
        {
            var sb = new StringBuilder(s.Length);
            var i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\\')
                {
                    var rest = s.Substring(i);

                    if (rest.StartsWith("\\frac") || rest.StartsWith("\\dfrac") || rest.StartsWith("\\tfrac"))
                    {
                        var cmdLen = rest.StartsWith("\\frac") ? 5 : 6;
                        int pos = SkipWhitespace(s, i + cmdLen);
                        string num, den;
                        int afterNum, afterDen;
                        if (TryReadGroup(s, pos, out num, out afterNum))
                        {
                            pos = SkipWhitespace(s, afterNum);
                            if (TryReadGroup(s, pos, out den, out afterDen))
                            {
                                sb.Append('(').Append(ConvertStructural(num)).Append(")/(")
                                  .Append(ConvertStructural(den)).Append(')');
                                i = afterDen;
                                continue;
                            }
                        }
                        // Not followed by two groups: emit literally and move on.
                        sb.Append(s, i, cmdLen);
                        i += cmdLen;
                        continue;
                    }

                    if (rest.StartsWith("\\sqrt"))
                    {
                        int pos = SkipWhitespace(s, i + 5);
                        string index = null;
                        if (pos < s.Length && s[pos] == '[')
                        {
                            var close = s.IndexOf(']', pos);
                            if (close > pos)
                            {
                                index = s.Substring(pos + 1, close - pos - 1);
                                pos = SkipWhitespace(s, close + 1);
                            }
                        }
                        string arg;
                        int afterArg;
                        if (TryReadGroup(s, pos, out arg, out afterArg))
                        {
                            if (index != null)
                            {
                                // nth root: \root(n\of(x)) with protected backslashes
                                sb.Append(BackslashSentinel).Append("root(")
                                  .Append(ConvertStructural(index))
                                  .Append(BackslashSentinel).Append("of(")
                                  .Append(ConvertStructural(arg)).Append("))");
                            }
                            else
                            {
                                sb.Append("√(").Append(ConvertStructural(arg)).Append(')');
                            }
                            i = afterArg;
                            continue;
                        }
                        sb.Append(s, i, 5);
                        i += 5;
                        continue;
                    }
                }

                if (s[i] == '/')
                {
                    sb.Append('\\').Append('/');
                    i++;
                    continue;
                }

                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            return pos;
        }

/// <summary>Reads a balanced {...} group or a single token (char or \command) at pos.</summary>
        private static bool TryReadGroup(string s, int pos, out string content, out int end)
        {
            content = null;
            end = pos;
            if (pos >= s.Length) return false;

            if (s[pos] == '{')
            {
                var depth = 0;
                for (var j = pos; j < s.Length; j++)
                {
                    if (s[j] == '{') depth++;
                    else if (s[j] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            content = s.Substring(pos + 1, j - pos - 1);
                            end = j + 1;
                            return true;
                        }
                    }
                }
                return false; // unbalanced
            }

            if (s[pos] == '\\')
            {
                var j = pos + 1;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                var name = s.Substring(pos + 1, j - pos - 1);

                // \sqrt takes an argument — keep it inside the operand, or
                // \frac{1}{\sqrt{f}} would orphan the {f} from the denominator.
                if (name == "sqrt")
                {
                    var k = SkipWhitespace(s, j);
                    if (k < s.Length && s[k] == '[')
                    {
                        var close = s.IndexOf(']', k);
                        if (close > k) k = SkipWhitespace(s, close + 1);
                    }
                    string arg;
                    int afterArg;
                    if (TryReadGroup(s, k, out arg, out afterArg))
                    {
                        content = s.Substring(pos, afterArg - pos);
                        end = afterArg;
                        return true;
                    }
                }

                content = s.Substring(pos, j - pos);
                end = j;
                return true;
            }

            content = s[pos].ToString();
            end = pos + 1;
            return true;
        }
    }
}
