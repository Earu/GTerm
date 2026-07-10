using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace GTerm.MCP
{
    /// <summary>Fetches and cleans pages from the Garry's Mod wiki (wiki.facepunch.com/gmod).</summary>
    internal static partial class GmodWiki
    {
        private const string Base = "https://wiki.facepunch.com/gmod/";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GTerm-MCP/1.0 (+https://github.com/Earu/GTerm)");
            return client;
        }

        internal sealed class WikiResult
        {
            public bool Success { get; init; }
            public string? Url { get; init; }
            public string? Text { get; init; }
            public string? Error { get; init; }
        }

        internal static async Task<WikiResult> FetchAsync(string page, int maxChars, CancellationToken cancellationToken = default)
        {
            // The wiki path is the page name verbatim (e.g. "Global.print", "Entity:SetHealth").
            // Only the query/fragment need escaping; ':' and '.' are valid path chars.
            string trimmed = page.Trim().TrimStart('/');
            string url = Base + Uri.EscapeDataString(trimmed).Replace("%2F", "/").Replace("%3A", ":").Replace("%2E", ".");

            try
            {
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode || body.Contains("This page is missing", StringComparison.Ordinal))
                {
                    return new WikiResult
                    {
                        Success = false,
                        Url = url,
                        Error = $"No wiki page '{trimmed}'. Use the exact page name, e.g. 'Global.print', 'Entity:SetHealth', "
                            + "'util.TableToJSON', 'file.Read'. Library functions are 'lib.func'; entity methods are 'Class:Method'.",
                    };
                }

                string text = Extract(body);
                if (text.Length > maxChars) text = text[..maxChars] + $"\n\n... [truncated at {maxChars} chars]";

                return new WikiResult { Success = true, Url = url, Text = text };
            }
            catch (Exception ex)
            {
                return new WikiResult { Success = false, Url = url, Error = $"Fetch failed: {ex.Message}" };
            }
        }

        // Lua type annotations link to their own pages but are noise as cross-references.
        private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "string", "number", "boolean", "table", "function", "vararg",
            "float", "double", "integer", "int", "any", "nil", "void", "bit",
        };

        /// <summary>Pulls the readable content out of a wiki page, dropping nav chrome and the footer.</summary>
        private static string Extract(string html)
        {
            int start = html.IndexOf("<div class=\"content\"", StringComparison.Ordinal);
            string seg = start >= 0 ? html[start..] : html;

            seg = ScriptOrStyle().Replace(seg, " ");

            // Preserve internal wiki links: without them, "see the Custom Shaders page" and the fact
            // that each shader name links to a Shaders/<name> subpage are invisible, which makes the
            // page look like a dead end. Annotate the first mention of each target with [->page] and
            // list them all in a footer so they can be fetched.
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            seg = Anchor().Replace(seg, m =>
            {
                string inner = AnyTag().Replace(m.Groups[2].Value, "").Trim();
                string? page = InternalPage(m.Groups[1].Value);
                // Pad with spaces so adjacent anchors don't fuse (e.g. "string" + "PIXSHADER").
                if (page == null || PrimitiveTypes.Contains(page)) return $" {inner} ";
                if (!seen.Add(page)) return $" {inner} ";
                return inner.Length == 0 ? $" [->{page}] " : $" {inner} [->{page}] ";
            });

            seg = BlockEnd().Replace(seg, "\n");
            seg = LineBreak().Replace(seg, "\n");
            seg = AnyTag().Replace(seg, " ");
            seg = HttpUtility.HtmlDecode(seg);

            StringBuilder sb = new();
            int blankRun = 0;
            foreach (string rawLine in seg.Split('\n'))
            {
                string line = Spaces().Replace(rawLine, " ").Trim();
                if (line.Length == 0)
                {
                    if (++blankRun <= 1) sb.Append('\n');
                    continue;
                }

                blankRun = 0;
                sb.Append(line).Append('\n');
            }

            string text = sb.ToString().Trim();

            foreach (string marker in new[] { "Page views:", "Special Pages", "Account Management" })
            {
                int i = text.IndexOf(marker, StringComparison.Ordinal);
                if (i > 0) text = text[..i].TrimEnd();
            }

            // Aggregate the links that survived the footer cut into a fetchable list.
            SortedSet<string> related = new(StringComparer.Ordinal);
            foreach (Match m in RelatedToken().Matches(text)) related.Add(m.Groups[1].Value);
            if (related.Count > 0)
            {
                const int cap = 60;
                IEnumerable<string> shown = related.Take(cap);
                string suffix = related.Count > cap ? $", ...and {related.Count - cap} more" : "";
                text += "\n\nRelated wiki pages (fetch with read_gmod_wiki): " + string.Join(", ", shown) + suffix;
            }

            return text;
        }

        /// <summary>Returns the wiki page name for an internal gmod link, or null for anchors/external links.</summary>
        private static string? InternalPage(string href)
        {
            if (string.IsNullOrEmpty(href) || href[0] == '#') return null;

            const string full = "https://wiki.facepunch.com/gmod/";
            string path;
            if (href.StartsWith(full, StringComparison.OrdinalIgnoreCase)) path = href[full.Length..];
            else if (href.StartsWith("/gmod/", StringComparison.Ordinal)) path = href["/gmod/".Length..];
            else return null;

            int cut = path.IndexOfAny(['#', '?']);
            if (cut >= 0) path = path[..cut];
            path = path.Trim('/');

            return path.Length == 0 ? null : path;
        }

        [GeneratedRegex(@"<a\b[^>]*\bhref=""([^""]*)""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Anchor();

        [GeneratedRegex(@"\[->([^\]]+)\]")]
        private static partial Regex RelatedToken();

        [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex ScriptOrStyle();

        [GeneratedRegex(@"</(h1|h2|h3|h4|p|div|li|tr|pre)>", RegexOptions.IgnoreCase)]
        private static partial Regex BlockEnd();

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex LineBreak();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex AnyTag();

        [GeneratedRegex(@"[ \t]+")]
        private static partial Regex Spaces();
    }
}
