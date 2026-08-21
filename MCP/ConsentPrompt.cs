using Spectre.Console;
using System.Text;

namespace GTerm.MCP
{
    /// <summary>
    /// An interactive checkbox list drawn in GTerm's own console. While one is open the key loop
    /// hands every key here, all game and agent output is held back, and the block re-renders in
    /// place on each key. The answer is the set of checked indexes, or null when the user cancelled
    /// (Esc). There is no timeout: the console visibly says it is waiting, and the held output is
    /// capped. Only one can be open at a time.
    /// </summary>
    internal sealed class ConsentPrompt
    {
        internal sealed record Tool(string Name, string Realm, string Description);
        internal sealed record Item(string Name, string? Bound, string? Description, IReadOnlyList<Tool> Tools);

        private static readonly object Locker = new();
        private static ConsentPrompt? Active;

        private readonly TaskCompletionSource<int[]?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string Title;
        private readonly IReadOnlyList<Item> Items;
        private readonly bool[] Checked;
        private int Cursor;
        private int RenderedRows;

        private ConsentPrompt(string title, IReadOnlyList<Item> items)
        {
            this.Title = title;
            this.Items = items;
            this.Checked = new bool[items.Count];
        }

        internal static bool IsOpen
        {
            get { lock (Locker) return Active != null; }
        }

        /// <summary>Opens and draws a prompt. Null when one is already open.</summary>
        internal static ConsentPrompt? Open(string title, IReadOnlyList<Item> items)
        {
            ConsentPrompt prompt;
            lock (Locker)
            {
                if (Active != null) return null;
                prompt = new ConsentPrompt(title, items);
                Active = prompt;
            }

            prompt.Render();
            return prompt;
        }

        internal async Task<int[]?> WaitAsync()
        {
            try
            {
                return await this.Completion.Task;
            }
            finally
            {
                lock (Locker)
                {
                    if (Active == this) Active = null;
                }

                Program.ReleaseHeldLogs();
            }
        }

        /// <summary>Feeds a key from the input thread. False when no prompt is open.</summary>
        internal static bool HandleKey(ConsoleKeyInfo key)
        {
            ConsentPrompt? prompt;
            lock (Locker) prompt = Active;
            if (prompt == null) return false;

            prompt.OnKey(key);
            return true;
        }

        private void OnKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    this.Cursor = (this.Cursor - 1 + this.Items.Count) % this.Items.Count;
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.Tab:
                    this.Cursor = (this.Cursor + 1) % this.Items.Count;
                    break;

                case ConsoleKey.Spacebar:
                    this.Checked[this.Cursor] = !this.Checked[this.Cursor];
                    break;

                case ConsoleKey.A:
                    bool all = this.Checked.All(c => c);
                    Array.Fill(this.Checked, !all);
                    break;

                case ConsoleKey.Enter:
                    Finish(Enumerable.Range(0, this.Items.Count).Where(i => this.Checked[i]).ToArray());
                    return;

                case ConsoleKey.Escape:
                    Finish(null);
                    return;

                default:
                    // Digits toggle directly, so "1" still works for people who read the old prompt.
                    if (char.IsAsciiDigit(key.KeyChar))
                    {
                        int n = key.KeyChar - '1';
                        if (n >= 0 && n < this.Items.Count) this.Checked[n] = !this.Checked[n];
                    }
                    break;
            }

            Render();
        }

        private void Finish(int[]? chosen)
        {
            Render(final: true, cancelled: chosen == null);
            this.Completion.TrySetResult(chosen);
        }

        private void Render(bool final = false, bool cancelled = false)
        {
            const string rule = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
            string client = SyntaxHighlighter.RealmColour("client");
            string server = SyntaxHighlighter.RealmColour("server");

            List<(string plain, string markup)> lines = [];
            void Line(string plain, string markup) => lines.Add((plain, markup));

            Line(rule, $"[yellow]{rule}[/]");
            Line(this.Title, $"[bold black on yellow] {Markup.Escape(this.Title)} [/]");
            Line("WARNING: Enabling a package lets the agent run third-party Lua in your game. Malicious code could steal data or break your install. Only enable sources you trust.",
                "[bold black on yellow] WARNING: Enabling a package lets the agent run third-party Lua in your game. Malicious code could steal data or break your install. Only enable sources you trust. [/]");
            Line("", "");

            for (int i = 0; i < this.Items.Count; i++)
            {
                Item item = this.Items[i];
                bool on = this.Checked[i];
                bool at = i == this.Cursor && !final;
                if (cancelled) on = false;
                string box = on ? "[x]" : "[ ]";
                string arrow = at ? "▶" : " ";
                string bound = item.Bound != null ? $"  (bound to {item.Bound})" : "";

                Line($" {arrow} {box} {item.Name}{bound}",
                    $" [bold yellow]{arrow}[/] {(on ? "[bold green][[x]][/]" : "[grey][[ ]][/]")} "
                    + (at ? "[bold magenta1 underline]" : "[bold magenta1]") + Markup.Escape(item.Name) + "[/]"
                    + $"[grey]{Markup.Escape(bound)}[/]");

                if (!string.IsNullOrEmpty(item.Description))
                    Line($"         {Short(item.Description, 80)}", $"         [white]{Markup.Escape(Short(item.Description, 80))}[/]");

                foreach (Tool tool in item.Tools)
                {
                    string colour = tool.Realm == "server" ? server : client;
                    Line($"         - {tool.Name} [{tool.Realm}]  {Short(tool.Description, 60)}",
                        $"         [grey]-[/] [bold cyan1]{Markup.Escape(tool.Name)}[/] [{colour}]█ {tool.Realm,-6}[/] [white]{Markup.Escape(Short(tool.Description, 60))}[/]");
                }

                Line("", "");
            }

            if (final)
            {
                int count = cancelled ? 0 : this.Checked.Count(c => c);
                string text = cancelled ? "Cancelled" : count > 0 ? $"Enabled {count}" : "None enabled";
                Line(text, count > 0 ? $"[bold black on green] {text} [/]" : $"[bold white on red] {text} [/]");
            }
            else
            {
                Line("↑/↓ move    Space toggle    A all    Enter confirm    Esc cancel",
                    "[bold white on darkorange3] ↑/↓ move    Space toggle    A all    Enter confirm    Esc cancel [/]");
            }

            Line(rule, $"[yellow]{rule}[/]");

            this.RenderedRows = Program.RenderBlock(lines, this.RenderedRows);
        }

        private static string Short(string text, int max)
        {
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
        }
    }
}
