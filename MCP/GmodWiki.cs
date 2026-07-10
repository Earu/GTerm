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

        /// <summary>Pulls the readable content out of a wiki page, dropping nav chrome and the footer.</summary>
        private static string Extract(string html)
        {
            int start = html.IndexOf("<div class=\"content\"", StringComparison.Ordinal);
            string seg = start >= 0 ? html[start..] : html;

            seg = ScriptOrStyle().Replace(seg, " ");
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

            return text;
        }

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
