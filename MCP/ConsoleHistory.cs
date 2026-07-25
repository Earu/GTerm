using GTerm.Listeners;

namespace GTerm.MCP
{
    /// <summary>
    /// An always-on ring buffer of recent console output. The console is a live stream: by the time
    /// an agent decides to look at it, the lines it wants have already scrolled past. Keeping a
    /// backlog lets "what just printed?" be answered instantly and looking BACKWARDS (newest first),
    /// instead of racing a forward capture window that can only ever see the future.
    /// </summary>
    internal sealed class ConsoleHistory
    {
        private const int Capacity = 1000;

        private readonly LinkedList<OutputLine> Buffer = new();
        private readonly object Locker = new();

        internal ConsoleHistory(ILogListener listener)
        {
            listener.OnLog += OnLog;
        }

        private void OnLog(object sender, LogEventArgs args)
        {
            // GTerm's own probe/result markers are plumbing, not console output.
            if (GTermSentinels.IsSentinel(args.Message)) return;

            lock (this.Locker)
            {
                this.Buffer.AddLast(new OutputLine
                {
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    Message = args.Message,
                    Color = new ColorInfo { R = args.Color.R, G = args.Color.G, B = args.Color.B, A = args.Color.A },
                });

                while (this.Buffer.Count > Capacity) this.Buffer.RemoveFirst();
            }
        }

        /// <summary>Returns up to <paramref name="count"/> of the most recent lines, newest first.</summary>
        internal List<OutputLine> GetRecent(int count)
        {
            lock (this.Locker)
            {
                List<OutputLine> result = new(Math.Min(count, this.Buffer.Count));

                for (LinkedListNode<OutputLine>? node = this.Buffer.Last; node != null && result.Count < count; node = node.Previous)
                    result.Add(node.Value);

                return result;
            }
        }

        internal int Count
        {
            get { lock (this.Locker) return this.Buffer.Count; }
        }
    }
}
