using Spectre.Console;
using System.Text;

namespace GTerm
{
    /// <summary>
    /// Colours one line of GLua or of a console command for GTerm's own output, in Monokai.
    ///
    /// Deliberately a lexer and not a parser: it runs on whatever an agent decided to send, it must
    /// never throw on half-written or malformed code, and being wrong about an edge case costs nothing
    /// worse than a mis-coloured token. It also works strictly line by line, because that is the unit
    /// the console writes in.
    /// </summary>
    internal static class SyntaxHighlighter
    {
        private const string Fg = "#f8f8f2";       // plain text
        private const string Comment = "#75715e";  // comments
        private const string Pink = "#f92672";     // keywords, operators
        private const string Purple = "#ae81ff";   // numbers, nil/true/false
        private const string Yellow = "#e6db74";   // strings
        private const string Cyan = "#66d9ef";     // known globals and libraries
        private const string Green = "#a6e22e";    // anything being called

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "and", "break", "continue", "do", "else", "elseif", "end", "for", "function", "goto",
            "if", "in", "local", "not", "or", "repeat", "return", "then", "until", "while",
        };

        /// <summary>Monokai paints constants like numbers, not like keywords.</summary>
        private static readonly HashSet<string> Constants = new(StringComparer.Ordinal) { "nil", "true", "false" };

        private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
        {
            "hook", "timer", "net", "file", "player", "ents", "util", "string", "table", "math", "os",
            "render", "surface", "draw", "cam", "vgui", "concommand", "cvars", "team", "umsg", "sound",
            "self", "_G", "GAMEMODE", "GM", "LocalPlayer", "Entity", "Vector", "Angle", "Color", "Material",
        };

        /// <summary>
        /// Colour for a realm badge, using the Garry's Mod wiki's own values taken from its stylesheet,
        /// so they read the way anyone who has used the wiki already expects: client amber, server blue,
        /// menu green. See https://wiki.facepunch.com/gmod/States
        /// </summary>
        internal static string RealmColour(string? realm) => realm?.Trim().ToLowerInvariant() switch
        {
            "client" => "#dea909",
            "server" => "#03a9f4",
            "menu" => "#4caf50",
            _ => Fg,
        };

        internal static string Lua(string line)
        {
            StringBuilder sb = new(line.Length * 3);
            int i = 0;

            while (i < line.Length)
            {
                char c = line[i];

                // Comment to end of line. Long-bracket comments still colour correctly per line.
                if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    Paint(sb, Comment, line[i..]);
                    return sb.ToString();
                }

                if (c is '"' or '\'')
                {
                    int start = i;
                    char quote = c;
                    i++;

                    while (i < line.Length)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == quote) { i++; break; }
                        i++;
                    }

                    if (i > line.Length) i = line.Length;
                    Paint(sb, Yellow, line[start..i]);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                    Paint(sb, Purple, line[start..i]);
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    string word = line[start..i];

                    if (Keywords.Contains(word)) Paint(sb, Pink, word);
                    else if (Constants.Contains(word)) Paint(sb, Purple, word);
                    else if (Globals.Contains(word)) Paint(sb, Cyan, word);
                    else if (IsCalled(line, i)) Paint(sb, Green, word);
                    else Paint(sb, Fg, word);

                    continue;
                }

                // Operators read as structure; brackets and separators stay quiet.
                Paint(sb, "+-*/%^#=<>~.:".Contains(c) ? Pink : Fg, c.ToString());
                i++;
            }

            return sb.ToString();
        }

        /// <summary>A console command: the command itself, then its arguments.</summary>
        internal static string ConsoleCommand(string line)
        {
            StringBuilder sb = new(line.Length * 3);
            int i = 0;
            bool first = true;

            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    int ws = i;
                    while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                    sb.Append(Markup.Escape(line[ws..i]));
                    continue;
                }

                if (line[i] == '"')
                {
                    int start = i;
                    i++;
                    while (i < line.Length && line[i] != '"') i++;
                    if (i < line.Length) i++;
                    Paint(sb, Yellow, line[start..i]);
                    first = false;
                    continue;
                }

                int tokenStart = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                string token = line[tokenStart..i];

                if (first) Paint(sb, Green, token);
                else if (double.TryParse(token, out _)) Paint(sb, Purple, token);
                else Paint(sb, Fg, token);

                first = false;
            }

            return sb.ToString();
        }

        /// <summary>True when the next non-space character opens a call or a string/table argument.</summary>
        private static bool IsCalled(string line, int after)
        {
            while (after < line.Length && line[after] == ' ') after++;
            return after < line.Length && line[after] is '(' or '"' or '\'' or '{';
        }

        private static void Paint(StringBuilder sb, string colour, string text)
            => sb.Append('[').Append(colour).Append(']').Append(Markup.Escape(text)).Append("[/]");
    }
}
