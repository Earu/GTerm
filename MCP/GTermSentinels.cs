namespace GTerm.MCP
{
    internal readonly record struct Sentinel(string Marker, string Payload);

    /// <summary>
    /// Out-of-band markers that GTerm's generated Lua prints so results can be told apart from
    /// ordinary console chatter. A sentinel looks like: &lt;&lt;GTERM:MARKER&gt;&gt;payload&lt;&lt;END&gt;&gt;
    /// </summary>
    internal static class GTermSentinels
    {
        internal const string Status = "STATUS";
        internal const string Client = "CLIENT";
        internal const string LuaOk = "LUAOK";
        internal const string LuaErr = "LUAERR";
        internal const string SyntaxOk = "SYNOK";
        internal const string SyntaxErr = "SYNERR";
        internal const string File = "FILE";
        internal const string ReloadOk = "RLDOK";
        internal const string ReloadErr = "RLDERR";

        private const string Prefix = "<<GTERM:";
        private const string HeaderEnd = ">>";
        private const string Suffix = "<<END>>";

        internal static bool IsSentinel(string message)
            => message.Contains(Prefix, StringComparison.Ordinal);

        internal static bool TryParse(string message, out Sentinel sentinel)
        {
            sentinel = default;

            int markerStart = message.IndexOf(Prefix, StringComparison.Ordinal);
            if (markerStart < 0) return false;
            markerStart += Prefix.Length;

            int markerEnd = message.IndexOf(HeaderEnd, markerStart, StringComparison.Ordinal);
            if (markerEnd < 0) return false;

            int payloadStart = markerEnd + HeaderEnd.Length;
            int payloadEnd = message.IndexOf(Suffix, payloadStart, StringComparison.Ordinal);
            if (payloadEnd < 0) return false;

            sentinel = new Sentinel(
                message[markerStart..markerEnd],
                message[payloadStart..payloadEnd]);

            return true;
        }

        /// <summary>
        /// Builds the Lua that prints a sentinel. The marker is split across concatenations so the
        /// literal never appears in the command string we send: the engine can echo a command back
        /// to us, and an echoed literal would otherwise be mistaken for a real result.
        /// </summary>
        internal static string LuaEmit(string marker, string payloadExpression)
            => $"print(\"<<GTERM\" .. \":{marker}\" .. \">>\" .. {payloadExpression} .. \"<<\" .. \"END>>\")";

        /// <summary>Escapes an arbitrary string into a Lua double-quoted literal.</summary>
        internal static string LuaLiteral(string value)
        {
            System.Text.StringBuilder sb = new(value.Length + 2);
            sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Picks a Lua long-bracket level whose closing sequence does not occur in <paramref name="source"/>,
        /// so the source can be embedded verbatim as a long string.
        /// </summary>
        internal static string LongBracketLevel(string source)
        {
            int level = 2;
            while (source.Contains($"]{new string('=', level)}]", StringComparison.Ordinal)) level++;
            return new string('=', level);
        }
    }
}
