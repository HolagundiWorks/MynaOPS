// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;

namespace Office.Shared.SmartBlocks;

/// <summary>A definition, formula or value a smart block could not use.</summary>
public sealed class SmartBlockException(string message) : Exception(message);

/// <summary>
/// The formula language of the smart block definitions (the same as O-Draft's <c>aad-param</c> formulas, without units): numbers, the
/// parameters and derived values by name, <c>+ - * /</c>, <c>^</c> with a whole-number exponent, parentheses and <c>min</c>, <c>max</c>,
/// <c>abs</c>, <c>sqrt</c>, <c>round</c>, <c>floor</c>, <c>ceil</c>. A formula is parsed once; evaluation never parses again.
/// </summary>
public sealed class SmartFormula
{
    private const int MaxLength = 400;
    private const int MaxDepth = 32;

    private readonly Node _root;

    public string Text { get; }

    private SmartFormula(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public static SmartFormula Parse(string text)
    {
        if (text.Length > MaxLength)
            throw new SmartBlockException($"formula `{text[..20]}…` is longer than {MaxLength} characters");
        var p = new Parser(text);
        var root = p.Expression(0);
        p.End();
        return new SmartFormula(text, root);
    }

    public double Evaluate(IReadOnlyDictionary<string, double> vars)
    {
        var v = _root.Eval(vars);
        if (!double.IsFinite(v))
            throw new SmartBlockException($"formula `{Text}` is not a finite number");
        return v;
    }

    private abstract class Node
    {
        public abstract double Eval(IReadOnlyDictionary<string, double> vars);
    }

    private sealed class Num(double v) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars) => v;
    }

    private sealed class Var(string name) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars) =>
            vars.TryGetValue(name, out var v) ? v : throw new SmartBlockException($"`{name}` has no value");
    }

    private sealed class Neg(Node a) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars) => -a.Eval(vars);
    }

    private sealed class Bin(char op, Node a, Node b) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars)
        {
            var x = a.Eval(vars);
            var y = b.Eval(vars);
            return op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                _ => y == 0 ? throw new SmartBlockException("division by zero") : x / y,
            };
        }
    }

    private sealed class Pow(Node a, int n) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars) => Math.Pow(a.Eval(vars), n);
    }

    private sealed class Call(string fn, List<Node> args) : Node
    {
        public override double Eval(IReadOnlyDictionary<string, double> vars)
        {
            var xs = args.Select(a => a.Eval(vars)).ToList();
            var x = xs.Count > 0 ? xs[0] : 0;
            return fn switch
            {
                "min" => xs.Min(),
                "max" => xs.Max(),
                "abs" => Math.Abs(x),
                "sqrt" => x < 0 ? throw new SmartBlockException("square root of a negative number") : Math.Sqrt(x),
                "round" => Math.Round(x, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(x),
                _ => Math.Ceiling(x),
            };
        }
    }

    private sealed class Parser(string s)
    {
        private int _i;

        private char Peek()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i]))
                _i++;
            return _i < s.Length ? s[_i] : '\0';
        }

        public void End()
        {
            if (Peek() != '\0')
                throw new SmartBlockException($"formula `{s}`: unexpected `{s[_i]}`");
        }

        public Node Expression(int depth)
        {
            if (depth > MaxDepth)
                throw new SmartBlockException($"formula `{s}` is nested too deeply");
            var left = Term(depth);
            while (Peek() is '+' or '-')
            {
                var op = s[_i++];
                left = new Bin(op, left, Term(depth));
            }
            return left;
        }

        private Node Term(int depth)
        {
            var left = Unary(depth);
            while (Peek() is '*' or '/')
            {
                var op = s[_i++];
                left = new Bin(op, left, Unary(depth));
            }
            return left;
        }

        private Node Unary(int depth)
        {
            if (Peek() == '-')
            {
                _i++;
                return new Neg(Unary(depth + 1));
            }
            var a = Primary(depth);
            if (Peek() == '^')
            {
                _i++;
                var sign = 1;
                if (Peek() == '-')
                {
                    _i++;
                    sign = -1;
                }
                var start = _i;
                while (_i < s.Length && char.IsDigit(s[_i]))
                    _i++;
                if (start == _i || !int.TryParse(s.AsSpan(start, _i - start), out var n) || n > 64)
                    throw new SmartBlockException($"formula `{s}`: `^` takes a whole-number exponent");
                return new Pow(a, sign * n);
            }
            return a;
        }

        private Node Primary(int depth)
        {
            var c = Peek();
            if (c == '(')
            {
                _i++;
                var e = Expression(depth + 1);
                if (Peek() != ')')
                    throw new SmartBlockException($"formula `{s}`: missing `)`");
                _i++;
                return e;
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.'))
                    _i++;
                if (!double.TryParse(s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new SmartBlockException($"formula `{s}`: bad number");
                return new Num(v);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_'))
                    _i++;
                var name = s[start.._i];
                if (Peek() == '(')
                {
                    if (name is not ("min" or "max" or "abs" or "sqrt" or "round" or "floor" or "ceil"))
                        throw new SmartBlockException($"formula `{s}`: unknown function `{name}`");
                    _i++;
                    var args = new List<Node>();
                    if (Peek() != ')')
                    {
                        do
                        {
                            if (args.Count > 0)
                                _i++;
                            args.Add(Expression(depth + 1));
                        }
                        while (Peek() == ',');
                    }
                    if (Peek() != ')')
                        throw new SmartBlockException($"formula `{s}`: missing `)`");
                    _i++;
                    if (args.Count == 0)
                        throw new SmartBlockException($"formula `{s}`: `{name}` needs an argument");
                    return new Call(name, args);
                }
                return new Var(name);
            }
            throw new SmartBlockException($"formula `{s}`: unexpected `{(c == '\0' ? "end" : c.ToString())}`");
        }
    }
}
