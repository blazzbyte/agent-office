using System;
using System.Collections.Generic;
using System.Text;

namespace AgentOffice.Bridge
{
/// <summary>Parses the Word linear format (UnicodeMath) into OMML (HTML-encoded dialect, see OmmlBuilder).</summary>
    internal static class LinearToOmml
    {
/// <summary>Returns the OMML for the linear input, or null when nothing parseable remains.</summary>
        public static string Convert(string linear)
        {
            if (string.IsNullOrWhiteSpace(linear)) return null;
            return ParseFragment(linear);
        }

/// <summary>Parses a fragment (used recursively for groups/operands).</summary>
        private static string ParseFragment(string s)
        {
            var p = new Parser(s);
            return p.ParseExpr();
        }

        private sealed class Node
        {
            public string Xml;
            public string DelimInner; // non-null only when this node is a (...) group
            public bool IsBreak;      // relational operator (=, <, ≤, …) — fraction operands stop here
            public bool IsAdditive;   // + - ± ∓ — denominators stop here (except leading sign)
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
                _i = 0;
            }

            // expr := seq ('/' seq)* — left-assoc, mirrors Word's incremental build-up.
            public string ParseExpr()
            {
                return ParseFractionChain(ParseSeq());
            }
            private string ParseFractionChain(List<Node> leftNodes)
            {
                SkipSpaces();
                if (Peek() != '/') return Render(leftNodes, false);

                var prefixLen = SplitPrefix(leftNodes);
                var prefix = RenderRange(leftNodes, 0, prefixLen, false);
                var frac = RenderRange(leftNodes, prefixLen, leftNodes.Count - prefixLen, true);

                List<Node> tail = null;
                while (Peek() == '/')
                {
                    _i++;
                    var denNodes = ParseSeq();
                    var denLen = SplitDenominator(denNodes);
                    var den = RenderRange(denNodes, 0, denLen, true);
                    frac = OmmlBuilder.Frac(frac, den);
                    if (denLen < denNodes.Count)
                    {
                        // Denominator ended early (relational/additive ahead):
                        // the chain is done; the tail continues as a new expression.
                        tail = denNodes.GetRange(denLen, denNodes.Count - denLen);
                        break;
                    }
                    SkipSpaces();
                }

                var result = prefix + frac;
                if (tail != null)
                {
                    result += ParseFractionChain(tail);
                }
                return result;
            }

            // seq := scripted*  (stops at end, unmatched ')' or '/')
            private List<Node> ParseSeq()
            {
                var nodes = new List<Node>();
                while (true)
                {
                    SkipSpaces();
                    if (_i >= _s.Length) break;
                    var c = _s[_i];
                    if (c == ')' || c == '/') break;
                    nodes.Add(ParseScripted());
                }
                return nodes;
            }

            // scripted := primary ( '^' operand | '_' operand )*
            private Node ParseScripted()
            {
                var baseNode = ParsePrimary();
                string sub = null, sup = null;
                while (true)
                {
                    SkipSpaces();
                    if (_i >= _s.Length) break;
                    if (_s[_i] == '^' && sup == null)
                    {
                        _i++;
                        sup = ParseOperand();
                    }
                    else if (_s[_i] == '_' && sub == null)
                    {
                        _i++;
                        sub = ParseOperand();
                    }
                    else
                    {
                        break;
                    }
                }

                if (sub == null && sup == null) return baseNode;

                string xml;
                if (sub != null && sup != null) xml = OmmlBuilder.SubSup(baseNode.Xml, sub, sup);
                else if (sup != null) xml = OmmlBuilder.Sup(baseNode.Xml, sup);
                else xml = OmmlBuilder.Sub(baseNode.Xml, sub);
                return new Node { Xml = xml };
            }

            // operand := '(' expr ')'  |  primary   (group parens are absorbed)
            private string ParseOperand()
            {
                SkipSpaces();
                if (_i >= _s.Length) return string.Empty;
                if (_s[_i] == '(')
                {
                    return ParseFragment(ReadGroup());
                }
                return ParsePrimary().Xml;
            }

            private Node ParsePrimary()
            {
                SkipSpaces();
                if (_i >= _s.Length) return new Node { Xml = string.Empty };

                var c = _s[_i];

                if (c == '(')
                {
                    var inner = ParseFragment(ReadGroup());
                    return new Node { Xml = OmmlBuilder.Delim(inner), DelimInner = inner };
                }

                if (c == '√')
                {
                    _i++;
                    return new Node { Xml = OmmlBuilder.Rad(null, ParseOperand()) };
                }

                if (c == '\\')
                {
                    if (_i + 1 < _s.Length && !char.IsLetter(_s[_i + 1]))
                    {
                        var lit = _s[_i + 1];
                        _i += 2;
                        return new Node { Xml = OmmlBuilder.Run(lit.ToString(), IsItalicChar(lit)) };
                    }

                    var name = ReadCommandName();

                    if (name == "sqrt")
                    {
                        return new Node { Xml = OmmlBuilder.Rad(null, ParseOperand()) };
                    }

                    if (name == "root")
                    {
                        SkipSpaces();
                        if (_i < _s.Length && _s[_i] == '(')
                        {
                            var group = ReadGroup(); // "n\of(x)"
                            var of = IndexOfTopLevel(group, "\\of");
                            if (of >= 0)
                            {
                                var indexXml = ParseFragment(group.Substring(0, of));
                                var rest = group.Substring(of + 3).TrimStart();
                                string argXml;
                                if (rest.StartsWith("("))
                                {
                                    argXml = ParseFragment(BalancedInner(rest, 0));
                                }
                                else
                                {
                                    argXml = ParseFragment(rest);
                                }
                                return new Node { Xml = OmmlBuilder.Rad(indexXml, argXml) };
                            }
                        }
                        return new Node { Xml = OmmlBuilder.Run("root", false) };
                    }

                    string sym;
                    if (LatexConverter.Symbols.TryGetValue(name, out sym))
                    {
                        return new Node
                        {
                            Xml = OmmlBuilder.Run(sym, IsItalicSymbol(sym)),
                            IsBreak = sym.Length == 1 && IsRelationalBreak(sym[0]),
                            IsAdditive = sym.Length == 1 && IsAdditiveChar(sym[0]),
                        };
                    }
                    // Unknown command: upright function-name text (sin, cos, lim, ...)
                    return new Node { Xml = OmmlBuilder.Run(name, false) };
                }

                _i++;
                return new Node
                {
                    Xml = OmmlBuilder.Run(c.ToString(), IsItalicChar(c)),
                    IsBreak = IsRelationalBreak(c),
                    IsAdditive = IsAdditiveChar(c),
                };
            }

            // ------------------------------------------------------------

            private string Render(List<Node> nodes, bool stripSingleDelim)
            {
                return RenderRange(nodes, 0, nodes.Count, stripSingleDelim);
            }

            private string RenderRange(List<Node> nodes, int start, int count, bool stripSingleDelim)
            {
                if (count <= 0) return string.Empty;
                if (stripSingleDelim && count == 1 && nodes[start].DelimInner != null)
                {
                    return nodes[start].DelimInner;
                }
                var sb = new StringBuilder();
                for (var i = start; i < start + count; i++) sb.Append(nodes[i].Xml);
                return sb.ToString();
            }

/// <summary>Node count to keep as prefix before a fraction.</summary>
            private static int SplitPrefix(List<Node> nodes)
            {
                var len = 0;
                if (nodes.Count > 0 && nodes[0].IsAdditive) len = 1; // leading sign stays outside
                for (var i = nodes.Count - 1; i >= len; i--)
                {
                    if (nodes[i].IsBreak) return i + 1;
                }
                return len;
            }

/// <summary>Node count forming the denominator: everything up to the FIRST relational node, or the first non-leading additive node.</summary>
            private static int SplitDenominator(List<Node> nodes)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].IsBreak) return i;
                    if (i > 0 && nodes[i].IsAdditive) return i;
                }
                return nodes.Count;
            }

            private static bool IsRelationalBreak(char c)
            {
                switch (c)
                {
                    case '=': case '<': case '>':
                    case '≤': case '≥': case '≠': case '≈': case '≡':
                    case '∈': case '∋': case '⊂': case '⊃': case '⊆': case '⊇':
                    case '→': case '←': case '↔': case '⇒': case '⇐': case '⇔': case '↦':
                        return true;
                    default:
                        return false;
                }
            }

            private static bool IsAdditiveChar(char c)
            {
                return c == '+' || c == '-' || c == '±' || c == '∓';
            }

            private void SkipSpaces()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek()
            {
                return _i < _s.Length ? _s[_i] : '\0';
            }

/// <summary>Consumes a balanced (...) group at the cursor; returns the inner text.</summary>
            private string ReadGroup()
            {
                var inner = BalancedInner(_s, _i);
                // advance past the closing ')'
                _i += inner.Length + 2;
                return inner;
            }

/// <summary>Returns the inner text of the balanced group starting at s[start] (must be '(').</summary>
            private static string BalancedInner(string s, int start)
            {
                if (start >= s.Length || s[start] != '(')
                {
                    throw new FormatException("Expected '(' at position " + start);
                }
                var depth = 0;
                for (var j = start; j < s.Length; j++)
                {
                    if (s[j] == '(') depth++;
                    else if (s[j] == ')')
                    {
                        depth--;
                        if (depth == 0) return s.Substring(start + 1, j - start - 1);
                    }
                }
                throw new FormatException("Unbalanced parentheses in linear input");
            }

            private string ReadCommandName()
            {
                _i++; // past '\'
                var start = _i;
                while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
                return _s.Substring(start, _i - start);
            }

/// <summary>Index of needle occurring at paren depth 0, or -1.</summary>
            private static int IndexOfTopLevel(string s, string needle)
            {
                var depth = 0;
                for (var j = 0; j <= s.Length - needle.Length; j++)
                {
                    var c = s[j];
                    if (c == '(') { depth++; continue; }
                    if (c == ')') { depth--; continue; }
                    if (depth == 0 && string.Compare(s, j, needle, 0, needle.Length, StringComparison.Ordinal) == 0)
                    {
                        return j;
                    }
                }
                return -1;
            }

            private static bool IsItalicChar(char c)
            {
                return (c >= 'a' && c <= 'z') ||
                       (c >= 'A' && c <= 'Z') ||
                       (c >= 'α' && c <= 'ω') ||
                       (c >= 'Α' && c <= 'Ω');
            }

            private static bool IsItalicSymbol(string sym)
            {
                return sym.Length == 1 && IsItalicChar(sym[0]);
            }
        }
    }
}
