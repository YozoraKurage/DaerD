using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Call recorder behind the C# exporter. When one is attached to a
    /// <see cref="ControllerBuilder"/>, every fluent call additionally appends its own C#
    /// source line — so the exporter "writes code" by simply driving the real builders, and
    /// the emitted text is, by construction, the exact call sequence that was proven to
    /// rebuild the controller. Assets appear as registered field names, never GUIDs.
    /// </summary>
    class RecipeScript
    {
        readonly List<string> _lines = new List<string>();
        readonly Dictionary<object, string> _builders = new Dictionary<object, string>();
        readonly Dictionary<UnityEngine.Object, string> _assets =
            new Dictionary<UnityEngine.Object, string>();
        readonly HashSet<string> _taken = new HashSet<string>();

        public IReadOnlyList<string> Lines => _lines;
        public IReadOnlyDictionary<UnityEngine.Object, string> Assets => _assets;

        /// <summary>Names the root <see cref="ControllerBuilder"/> ("c" in generated code).</summary>
        public void RegisterRoot(object builder)
        {
            _builders[builder] = "c";
            _taken.Add("c");
        }

        /// <summary>Reserves a field name for an asset reference; returns the name in use.</summary>
        public string RegisterAsset(UnityEngine.Object asset, string hint)
        {
            if (asset == null) return "null";
            if (_assets.TryGetValue(asset, out var existing)) return existing;
            string name = Unique(Identifier(hint, lowerFirst: true));
            _assets[asset] = name;
            return name;
        }

        public string AssetRef(UnityEngine.Object asset) =>
            asset == null ? "null"
            : _assets.TryGetValue(asset, out var name) ? name
            : RegisterAsset(asset, asset.name);

        /// <summary>The builder whose last recorded line can still be extended into a
        /// fluent chain (null right after anything unchainable).</summary>
        object _chainTarget;
        const int ChainLineLimit = 100;

        /// <summary>
        /// Records target.Method(...). Consecutive chainable calls on the same builder merge
        /// into one fluent chain ("a.TransitionsTo(b).When(go.IsTrue());") while the line
        /// stays readable;
        /// <paramref name="chain"/> is false for void methods, which must stay statements.
        /// </summary>
        public void Call(object target, string call, bool chain = true)
        {
            if (chain && target == _chainTarget && _lines.Count > 0)
            {
                string last = _lines[_lines.Count - 1];
                if (last.EndsWith(";") && last.Length + call.Length + 1 <= ChainLineLimit)
                {
                    _lines[_lines.Count - 1] = last.Substring(0, last.Length - 1) + "." + call + ";";
                    return;
                }
            }
            _lines.Add(NameOf(target) + "." + call + ";");
            _chainTarget = chain ? target : null;
        }

        public string Declare(object created, string hint, object owner, string call)
        {
            string name = Unique(Identifier(hint, lowerFirst: true));
            _builders[created] = name;
            _lines.Add("var " + name + " = " + NameOf(owner) + "." + call + ";");
            _chainTarget = created;
            return name;
        }

        public void Blank()
        {
            if (_lines.Count > 0 && _lines[_lines.Count - 1].Length > 0)
                _lines.Add(string.Empty);
            _chainTarget = null;
        }

        public void Comment(string text)
        {
            _lines.Add("// " + text);
            _chainTarget = null;
        }

        string NameOf(object builder) =>
            builder != null && _builders.TryGetValue(builder, out var name) ? name : "c";

        /// <summary>A builder reference used as a call argument ("idle" in
        /// "a.TransitionsTo(idle)").</summary>
        public string NameArg(object builder) => NameOf(builder);

        /// <summary>Registers a builder under a compound receiver expression
        /// ("move.LastChild") instead of a fresh variable.</summary>
        public void RegisterAlias(object builder, string expression) =>
            _builders[builder] = expression;

        string Unique(string name)
        {
            string candidate = name;
            for (int i = 2; !_taken.Add(candidate); i++)
                candidate = name + i;
            return candidate;
        }

        // ---- formatting --------------------------------------------------------

        /// <summary>A valid C# identifier derived from an arbitrary display name.</summary>
        public static string Identifier(string name, bool lowerFirst)
        {
            var builder = new StringBuilder();
            bool upperNext = false;
            foreach (var c in name ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(upperNext && builder.Length > 0 ? char.ToUpperInvariant(c) : c);
                    upperNext = false;
                }
                else
                    upperNext = true;   // word break: camel-case the next letter
            }
            if (builder.Length == 0) builder.Append("item");
            if (char.IsDigit(builder[0])) builder.Insert(0, '_');
            if (lowerFirst) builder[0] = char.ToLowerInvariant(builder[0]);
            string result = builder.ToString();
            return Keywords.Contains(result) ? result + "_" : result;
        }

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
            "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
            "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
            "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
            "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",
        };

        /// <summary>C# string literal with escapes.</summary>
        public static string S(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value ?? string.Empty)
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(c); break;
                }
            return builder.Append('"').ToString();
        }

        /// <summary>
        /// Float literal, kept as light as possible: whole numbers print as plain ints
        /// ("260" — implicit conversion carries them into float parameters), everything
        /// else round-trips with the f suffix ("0.25f").
        /// </summary>
        public static string F(float value)
        {
            if (value == (int)value && System.Math.Abs(value) < 1e7f)
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture) + "f";
        }

        public static string B(bool value) => value ? "true" : "false";

        public static string E<T>(T value) where T : System.Enum =>
            typeof(T).Name + "." + value;
    }
}
