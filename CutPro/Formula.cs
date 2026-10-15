using System.Globalization;

namespace CutPro;

/// <summary>Evaluation context of a property formula at one moment.</summary>
public readonly struct FormulaEnv
{
    public readonly Clip C; public readonly AnimProp P; public readonly double Time, Value;
    public FormulaEnv(Clip c, AnimProp p, double time, double value) { C = c; P = p; Time = time; Value = value; }
    public double T => Time - C.Start;                                   // seconds since the clip started
    public double Src => TimeMap.Src(C, Time);
    public double Frame => Math.Floor(T * TimeUtil.Fps + 1e-6);
}

public sealed class FormulaException(string message, int pos) : Exception(message) { public int Pos { get; } = pos; }

/// <summary>
/// Property formulas (like After Effects expressions), compiled to delegates. Example: <c>value + 20*sin(t*2*pi)</c>,
/// <c>wiggle(2, 10)</c>, <c>cycle()</c>, <c>linear(t, 0, 1, 0, 100)</c>. Operators: + − * / % ^ (power), comparisons, &amp;&amp; || !, c ? a : b.
/// See <see cref="Help"/> for variables and functions. Deterministic: preview and export get the same numbers.
/// </summary>
public sealed class Formula
{
    public string Source { get; }
    readonly Func<FormulaEnv, double> fn;
    Formula(string src, Func<FormulaEnv, double> fn) { Source = src; this.fn = fn; }
    public double Eval(FormulaEnv e) { try { return fn(e); } catch { return double.NaN; } }

    static readonly Dictionary<string, Formula?> cache = new();
    /// <summary>Compiled formula (cached); false if it doesn't compile.</summary>
    public static bool TryGet(string src, out Formula f)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(src, out var c))
            {
                try { c = Compile(src); } catch (FormulaException) { c = null; }
                if (cache.Count > 500) cache.Clear();
                cache[src] = c;
            }
            f = c!;
            return c != null;
        }
    }
    /// <summary>null when valid, else "message (at column n)".</summary>
    public static string? Check(string src)
    {
        try { Compile(src); return null; } catch (FormulaException e) { return $"{e.Message} (column {e.Pos + 1})"; }
    }

    public const string Help =
        "Variables: t (seconds since the clip start), time (sequence time), frame, dur (clip length), fps, value (the keyframed value now), " +
        "scale x y rotation opacity blur volume (this clip's keyframed values), pi, e, tau.\n" +
        "Motion: wiggle(freq, amount[, octaves, seed]), noise(x), random() / random(max) / random(min, max), cycle(), pingpong(), stepped(fps), " +
        "valueAt(t), key(n), keyTime(n), numKeys.\n" +
        "Shaping: linear(x, x0, x1, y0, y1), ease(x, x0, x1, y0, y1), easeIn(...), easeOut(...), lerp(a, b, f), clamp(x, lo, hi), smoothstep(a, b, x), step(edge, x), " +
        "bounce(amount, freq, decay), decay(amount, freq, decay).\n" +
        "Waves (period 1/freq, 0..1): saw(freq), tri(freq), pulse(freq[, duty]); square(freq) is ±1.\n" +
        "Math: sin cos tan asin acos atan atan2 sinh cosh tanh abs sqrt cbrt pow exp ln log log10 log2 min max floor ceil round(x[, digits]) trunc sign frac mod hypot deg rad if(c, a, b).\n" +
        "Operators: + - * / % ^ (power), < <= > >= == !=, && || !, cond ? a : b.";

    public static readonly (string name, string formula, string tip)[] Presets =
    {
        ("Wiggle (random shake)", "wiggle(2, 10)", "value ± 10, changing about 2 times a second"),
        ("Sine wave", "value + 20 * sin(t * 2 * pi * 0.5)", "swing ±20 every 2 seconds"),
        ("Pulse / breathe", "value * (1 + 0.08 * sin(t * 2 * pi))", "grow and shrink 8% once a second"),
        ("Spin", "value + t * 90", "turn 90 degrees a second (rotation)"),
        ("Drift", "value + t * 5", "move 5 units a second"),
        ("Bounce in", "value * (1 - exp(-5 * t) * cos(12 * t))", "overshoot and settle (scale)"),
        ("Fade in over 1 s", "value * clamp(t / 1, 0, 1)", "opacity or volume"),
        ("Fade out over the last 1 s", "value * clamp((dur - t) / 1, 0, 1)", "opacity or volume"),
        ("Flicker", "value * (0.75 + 0.25 * random())", "random every frame"),
        ("Loop keyframes", "cycle()", "repeat the keyframes after the last one"),
        ("Ping-pong keyframes", "pingpong()", "play the keyframes forwards, backwards, …"),
        ("Choppy (8 fps)", "stepped(8)", "hold values like stop motion"),
        ("Ramp over the clip", "linear(t, 0, dur, 0, 100)", "0 → 100 from start to end"),
        ("Follow another property", "scale / 2", "any property name works: x, y, rotation, …"),
    };

    // ---------------- parser ----------------
    enum K { Num, Id, Op, End }
    readonly record struct Tok(K Kind, string Text, double Num, int Pos);

    static List<Tok> Lex(string s)
    {
        var r = new List<Tok>(); int i = 0;
        while (i < s.Length)
        {
            char ch = s[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            int st = i;
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || ((s[i + 1] == '-' || s[i + 1] == '+') && i + 2 < s.Length && char.IsDigit(s[i + 2]))))
                { i += 2; while (i < s.Length && char.IsDigit(s[i])) i++; }
                if (!double.TryParse(s[st..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new FormulaException($"bad number '{s[st..i]}'", st);
                r.Add(new Tok(K.Num, s[st..i], v, st)); continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                r.Add(new Tok(K.Id, s[st..i].ToLowerInvariant(), 0, st)); continue;
            }
            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "==" or "!=" or "&&" or "||" or "**") { r.Add(new Tok(K.Op, two == "**" ? "^" : two, 0, st)); i += 2; continue; }
            if ("+-*/%^()<>!?:,".Contains(ch)) { r.Add(new Tok(K.Op, ch.ToString(), 0, st)); i++; continue; }
            if (ch == '×') { r.Add(new Tok(K.Op, "*", 0, st)); i++; continue; }
            throw new FormulaException($"unexpected '{ch}'", st);
        }
        r.Add(new Tok(K.End, "", 0, s.Length));
        return r;
    }

    public static Formula Compile(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) throw new FormulaException("empty formula", 0);
        var p = new Parser(Lex(src));
        var fn = p.Ternary();
        if (p.Cur.Kind != K.End) throw new FormulaException($"unexpected '{p.Cur.Text}'", p.Cur.Pos);
        return new Formula(src, fn);
    }

    delegate double F(FormulaEnv e);

    sealed class Parser(List<Tok> toks)
    {
        int i; int salt;
        public Tok Cur => toks[i];
        Tok Next() => toks[i++];
        bool Is(string op) => Cur.Kind == K.Op && Cur.Text == op;
        void Expect(string op) { if (!Is(op)) throw new FormulaException(Cur.Kind == K.End ? $"missing '{op}'" : $"expected '{op}' but found '{Cur.Text}'", Cur.Pos); i++; }

        public Func<FormulaEnv, double> Ternary()
        {
            var c = Or();
            if (!Is("?")) return c;
            i++;
            var a = Ternary(); Expect(":"); var b = Ternary();
            return e => c(e) != 0 ? a(e) : b(e);
        }
        Func<FormulaEnv, double> Or() { var l = And(); while (Is("||")) { i++; var a = l; var b = And(); l = e => a(e) != 0 || b(e) != 0 ? 1 : 0; } return l; }
        Func<FormulaEnv, double> And() { var l = Eq(); while (Is("&&")) { i++; var a = l; var b = Eq(); l = e => a(e) != 0 && b(e) != 0 ? 1 : 0; } return l; }
        Func<FormulaEnv, double> Eq()
        {
            var l = Cmp();
            while (Is("==") || Is("!="))
            {
                bool eq = Next().Text == "=="; var a = l; var b = Cmp();
                l = eq ? e => Math.Abs(a(e) - b(e)) < 1e-9 ? 1 : 0 : e => Math.Abs(a(e) - b(e)) >= 1e-9 ? 1 : 0;
            }
            return l;
        }
        Func<FormulaEnv, double> Cmp()
        {
            var l = Add();
            while (Is("<") || Is("<=") || Is(">") || Is(">="))
            {
                var op = Next().Text; var a = l; var b = Add();
                l = op switch
                {
                    "<" => e => a(e) < b(e) ? 1 : 0, "<=" => e => a(e) <= b(e) ? 1 : 0,
                    ">" => e => a(e) > b(e) ? 1 : 0, _ => e => a(e) >= b(e) ? 1 : 0,
                };
            }
            return l;
        }
        Func<FormulaEnv, double> Add()
        {
            var l = Mul();
            while (Is("+") || Is("-")) { bool plus = Next().Text == "+"; var a = l; var b = Mul(); l = plus ? e => a(e) + b(e) : e => a(e) - b(e); }
            return l;
        }
        Func<FormulaEnv, double> Mul()
        {
            var l = Unary();
            while (Is("*") || Is("/") || Is("%"))
            {
                var op = Next().Text; var a = l; var b = Unary();
                l = op switch { "*" => e => a(e) * b(e), "/" => e => a(e) / b(e), _ => e => FMod(a(e), b(e)) };
            }
            return l;
        }
        Func<FormulaEnv, double> Unary()
        {
            if (Is("-")) { i++; var a = Unary(); return e => -a(e); }
            if (Is("+")) { i++; return Unary(); }
            if (Is("!")) { i++; var a = Unary(); return e => a(e) == 0 ? 1 : 0; }
            return Pow();
        }
        Func<FormulaEnv, double> Pow()
        {
            var b = Primary();
            if (!Is("^")) return b;
            i++;
            var x = Unary();                                           // right-associative, -2^2 = -4, 2^-1 = 0.5
            return e => Math.Pow(b(e), x(e));
        }
        Func<FormulaEnv, double> Primary()
        {
            var tk = Cur;
            if (tk.Kind == K.Num) { i++; double v = tk.Num; return _ => v; }
            if (Is("(")) { i++; var x = Ternary(); Expect(")"); return x; }
            if (tk.Kind == K.Id)
            {
                i++;
                if (Is("("))
                {
                    i++;
                    var args = new List<Func<FormulaEnv, double>>();
                    if (!Is(")")) { args.Add(Ternary()); while (Is(",")) { i++; args.Add(Ternary()); } }
                    Expect(")");
                    return Call(tk, args.ToArray());
                }
                return Var(tk);
            }
            throw new FormulaException(tk.Kind == K.End ? "unexpected end of formula" : $"unexpected '{tk.Text}'", tk.Pos);
        }

        static Func<FormulaEnv, double> Var(Tok tk)
        {
            switch (tk.Text)
            {
                case "t": return e => e.T;
                case "time": return e => e.Time;
                case "src": return e => e.Src;
                case "frame": return e => e.Frame;
                case "dur": return e => e.C.Dur;
                case "fps": return _ => TimeUtil.Fps;
                case "value": return e => e.Value;
                case "pi": return _ => Math.PI;
                case "tau": return _ => 2 * Math.PI;
                case "e": return _ => Math.E;
                case "numkeys": return e => Anim.Track(e.C, e.P)?.Keys.Count ?? 0;
            }
            foreach (var info in Anim.Props)
                if (info.Name == tk.Text) { var p = info.P; return e => p == e.P ? e.Value : Anim.Keyed(e.C, p, e.Src); }
            throw new FormulaException($"unknown name '{tk.Text}'", tk.Pos);
        }

        Func<FormulaEnv, double> Call(Tok tk, Func<FormulaEnv, double>[] a)
        {
            string name = tk.Text;
            void N(params int[] counts) { if (!counts.Contains(a.Length)) throw new FormulaException($"{name}() takes {string.Join(" or ", counts)} argument{(counts.Max() == 1 ? "" : "s")}", tk.Pos); }
            Func<FormulaEnv, double> M1(Func<double, double> f) { N(1); var x = a[0]; return e => f(x(e)); }
            Func<FormulaEnv, double> M2(Func<double, double, double> f) { N(2); var x = a[0]; var y = a[1]; return e => f(x(e), y(e)); }
            int mySalt = ++salt;
            switch (name)
            {
                case "sin": return M1(Math.Sin); case "cos": return M1(Math.Cos); case "tan": return M1(Math.Tan);
                case "asin": return M1(Math.Asin); case "acos": return M1(Math.Acos); case "atan": return M1(Math.Atan);
                case "sinh": return M1(Math.Sinh); case "cosh": return M1(Math.Cosh); case "tanh": return M1(Math.Tanh);
                case "atan2": return M2(Math.Atan2);
                case "abs": return M1(Math.Abs); case "sqrt": return M1(Math.Sqrt); case "cbrt": return M1(Math.Cbrt);
                case "exp": return M1(Math.Exp); case "ln": case "log": return M1(Math.Log); case "log10": return M1(Math.Log10); case "log2": return M1(Math.Log2);
                case "floor": return M1(Math.Floor); case "ceil": return M1(Math.Ceiling); case "trunc": return M1(Math.Truncate);
                case "sign": return M1(v => Math.Sign(v)); case "frac": return M1(v => v - Math.Floor(v));
                case "deg": return M1(v => v * 180 / Math.PI); case "rad": return M1(v => v * Math.PI / 180);
                case "pow": return M2(Math.Pow); case "mod": return M2(FMod); case "hypot": return M2((x, y) => Math.Sqrt(x * x + y * y));
                case "round":
                    N(1, 2);
                    if (a.Length == 1) { var x = a[0]; return e => Math.Round(x(e), MidpointRounding.AwayFromZero); }
                    { var x = a[0]; var d = a[1]; return e => Math.Round(x(e), Math.Clamp((int)d(e), 0, 12), MidpointRounding.AwayFromZero); }
                case "min":
                case "max":
                    if (a.Length < 1) throw new FormulaException($"{name}() needs arguments", tk.Pos);
                    { bool mx = name == "max"; return e => { double r = a[0](e); for (int k = 1; k < a.Length; k++) { double v = a[k](e); r = mx ? Math.Max(r, v) : Math.Min(r, v); } return r; }; }
                case "clamp": { N(3); var x = a[0]; var lo = a[1]; var hi = a[2]; return e => { double l = lo(e), h = hi(e); return Math.Clamp(x(e), Math.Min(l, h), Math.Max(l, h)); }; }
                case "lerp": { N(3); var x = a[0]; var y = a[1]; var f = a[2]; return e => x(e) + (y(e) - x(e)) * f(e); }
                case "if": { N(3); var c = a[0]; var x = a[1]; var y = a[2]; return e => c(e) != 0 ? x(e) : y(e); }
                case "step": return M2((edge, x) => x >= edge ? 1 : 0);
                case "smoothstep": { N(3); var e0 = a[0]; var e1 = a[1]; var x = a[2]; return e => Smooth(Unit(x(e), e0(e), e1(e))); }
                case "linear": case "ease": case "easein": case "easeout":
                {
                    N(3, 5);
                    Func<double, double> shape = name switch { "ease" => Smooth, "easein" => u => u * u * u, "easeout" => u => 1 - Math.Pow(1 - u, 3), _ => u => u };
                    if (a.Length == 3) { var x = a[0]; var y0 = a[1]; var y1 = a[2]; return e => { double u = Math.Clamp(x(e), 0, 1); double v0 = y0(e); return v0 + (y1(e) - v0) * shape(u); }; }
                    { var x = a[0]; var x0 = a[1]; var x1 = a[2]; var y0 = a[3]; var y1 = a[4];
                      return e => { double u = Unit(x(e), x0(e), x1(e)); double v0 = y0(e); return v0 + (y1(e) - v0) * shape(u); }; }
                }
                case "noise":
                {
                    N(1, 2); var x = a[0]; var sd = a.Length > 1 ? a[1] : null;
                    return e => Noise(x(e) + (sd != null ? sd(e) * 71.13 : 0), Seed(e));
                }
                case "wiggle":
                {
                    N(2, 3, 4); var fq = a[0]; var am = a[1]; var oc = a.Length > 2 ? a[2] : null; var sd = a.Length > 3 ? a[3] : null;
                    return e => e.Value + am(e) * Fbm(e.T * fq(e) + (sd != null ? sd(e) * 71.13 : 0) + mySalt * 13.7, Seed(e), oc != null ? Math.Clamp((int)oc(e), 1, 8) : 1);
                }
                case "random":
                {
                    N(0, 1, 2);
                    Func<FormulaEnv, double> u = e => Hash01(Seed(e) ^ (uint)(mySalt * 0x9E3779B1u) ^ (uint)(long)e.Frame * 0x85EBCA77u);
                    if (a.Length == 0) return u;
                    if (a.Length == 1) { var mx = a[0]; return e => u(e) * mx(e); }
                    { var lo = a[0]; var hi = a[1]; return e => lo(e) + (hi(e) - lo(e)) * u(e); }
                }
                case "saw": { N(1); var f = a[0]; return e => Frac(e.T * f(e)); }
                case "tri": { N(1); var f = a[0]; return e => 1 - Math.Abs(2 * Frac(e.T * f(e)) - 1); }
                case "square": { N(1); var f = a[0]; return e => Frac(e.T * f(e)) < 0.5 ? 1 : -1; }
                case "pulse": { N(1, 2); var f = a[0]; var d = a.Length > 1 ? a[1] : null; return e => Frac(e.T * f(e)) < (d != null ? d(e) : 0.5) ? 1 : 0; }
                case "bounce": { N(3); var am = a[0]; var f = a[1]; var dc = a[2]; return e => am(e) * Math.Abs(Math.Sin(e.T * f(e) * Math.PI)) * Math.Exp(-dc(e) * e.T); }
                case "decay": { N(3); var am = a[0]; var f = a[1]; var dc = a[2]; return e => am(e) * Math.Sin(e.T * f(e) * 2 * Math.PI) * Math.Exp(-dc(e) * e.T); }
                case "valueat": { N(1); var x = a[0]; return e => Anim.Keyed(e.C, e.P, TimeMap.Src(e.C, e.C.Start + x(e))); }
                case "stepped": { N(1); var f = a[0]; return e => { double fps = Math.Max(0.01, f(e)); return Anim.Keyed(e.C, e.P, TimeMap.Src(e.C, e.C.Start + Math.Floor(e.T * fps + 1e-6) / fps)); }; }
                case "key":
                case "keytime":
                {
                    N(1); var n = a[0]; bool time = name == "keytime";
                    return e =>
                    {
                        var keys = Anim.Track(e.C, e.P)?.Keys; int k = (int)Math.Round(n(e)) - 1;
                        if (keys == null || k < 0 || k >= keys.Count) return double.NaN;
                        return time ? TimeMap.Out(e.C, keys[k].T) - e.C.Start : keys[k].V;
                    };
                }
                case "cycle": case "pingpong":
                {
                    N(0); bool pp = name == "pingpong";
                    return e =>
                    {
                        var keys = Anim.Track(e.C, e.P)?.Keys;
                        if (keys == null || keys.Count < 2) return e.Value;
                        double k0 = keys[0].T, span = keys[^1].T - k0, s = e.Src;
                        if (span <= 1e-9 || (s >= k0 && s <= keys[^1].T)) return e.Value;
                        double u = s - k0, n = Math.Floor(u / span), r = u - n * span;
                        if (pp && ((long)Math.Abs(n) & 1) == 1) r = span - r;
                        return Curve.Eval(keys, k0 + r);
                    };
                }
            }
            throw new FormulaException($"unknown function '{name}'", tk.Pos);
        }
    }

    // ---------------- helpers ----------------
    static double FMod(double a, double b) => b == 0 ? double.NaN : a - b * Math.Floor(a / b);
    static double Frac(double v) => v - Math.Floor(v);
    static double Unit(double x, double x0, double x1) => Math.Abs(x1 - x0) < 1e-12 ? (x >= x1 ? 1 : 0) : Math.Clamp((x - x0) / (x1 - x0), 0, 1);
    static double Smooth(double u) => u * u * (3 - 2 * u);

    /// <summary>Per clip + property seed: two wiggles on different properties don't move in step.</summary>
    static uint Seed(FormulaEnv e)
    {
        uint h = 2166136261;
        foreach (char ch in e.C.Id) h = (h ^ ch) * 16777619;
        return h ^ (uint)((int)e.P * 0x27D4EB2D);
    }
    static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16; return x; }
    static double Hash01(uint x) => Mix(x) / 4294967296.0;
    /// <summary>1D gradient noise, about −1..1, smooth, deterministic.</summary>
    public static double Noise(double x, uint seed)
    {
        double i0 = Math.Floor(x), f = x - i0;
        double G(double i) => Hash01(seed ^ (uint)(long)i * 0x9E3779B1u) * 2 - 1;
        double g0 = G(i0), g1 = G(i0 + 1);
        double u = f * f * f * (f * (f * 6 - 15) + 10);
        return 2 * ((1 - u) * g0 * f + u * g1 * (f - 1));
    }
    static double Fbm(double x, uint seed, int oct)
    {
        double sum = 0, amp = 1, norm = 0, fr = 1;
        for (int o = 0; o < oct; o++) { sum += amp * Noise(x * fr, seed + (uint)o * 101); norm += amp; amp *= 0.5; fr *= 2; }
        return Math.Clamp(sum / norm * 1.6, -1, 1);
    }
}
