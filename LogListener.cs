using System.Drawing;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace GTerm
{
    internal class LogEventArgs : EventArgs
    {
        internal LogEventArgs(int type, int lvl, string grp, Color col, string msg)
        {
            this.Type = type;
            this.Level = lvl;
            this.Group = grp;
            this.Color = col;
            this.Message = msg;
        }

        internal int Type { get; private set; }
        internal int Level { get; private set; }
        internal string Group { get; private set; }
        internal Color Color { get; private set; }
        internal string Message { get; set; }
    }

    delegate void LogEventHandler(object sender, LogEventArgs args);

    internal class LogListener
    {
        private const int BUFFER_SIZE = 8192;
        private readonly byte[] Buffer = new byte[BUFFER_SIZE];
        private readonly NamedPipeClientStream Pipe;

        internal event EventHandler? OnConnected;
        internal event EventHandler? OnDisconnected;
        internal event ErrorEventHandler? OnError;
        internal event LogEventHandler? OnLog;

        internal LogListener()
        {
            this.Pipe = new NamedPipeClientStream(
                ".",
                "garrysmod_console",
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous,
                HandleInheritability.None
            );
        }

        internal bool IsConnected { get; private set; }

        private void SetConnectionStatus(bool connected)
        {
            if (connected)
                this.OnConnected?.Invoke(this, EventArgs.Empty);
            else
                this.OnDisconnected?.Invoke(this, EventArgs.Empty);

            this.IsConnected = connected;
        }

        private static string ReadString(BinaryReader reader)
        {
            List<byte> list = [];
            byte ch;
            while ((ch = reader.ReadByte()) != 0)
                list.Add(ch);

            return Encoding.UTF8.GetString(list.ToArray());
        }

        private async Task ProcessMessages()
        {
            while (true)
            {
                try
                {
                    await this.Pipe.ConnectAsync();
                    this.Pipe.ReadMode = PipeTransmissionMode.Message;

                    this.SetConnectionStatus(true);

                    while (this.Pipe.IsConnected)
                    {
                        try
                        {
                            int read = await this.Pipe.ReadAsync(this.Buffer.AsMemory(0, BUFFER_SIZE));
                            if (read == 0) continue;

                            using (BinaryReader reader = new(new MemoryStream(this.Buffer, 0, read)))
                            {
                                int type = reader.ReadInt32();
                                int level = reader.ReadInt32();
                                string group = ReadString(reader);
                                Color color = Color.FromArgb(
                                    reader.ReadByte(),
                                    reader.ReadByte(),
                                    reader.ReadByte()
                                );

                                reader.ReadByte();

                                string fullMsg = ReadString(reader).Replace("<NEWLINE>", "\n");
                                this.OnLog?.Invoke(this, new LogEventArgs(type, level, group, color, fullMsg));
                            }
                        }
                        catch (Exception ex)
                        {
                            this.OnError?.Invoke(this, new ErrorEventArgs(ex));
                        }
                    }

                    this.SetConnectionStatus(false);
                }
                catch (TimeoutException) { } // ignore that
                catch (Exception ex)
                {
                    this.OnError?.Invoke(this, new ErrorEventArgs(ex));
                }
            }
        }

        internal void Start() => Task.Run(this.ProcessMessages);

        internal async Task WriteMessage(string input)
        {
            if (!this.Pipe.IsConnected) return;

            try
            {
                input = input.Replace("\0", "");

                byte[] buffer = Encoding.UTF8.GetBytes(input);
                await this.Pipe.WriteAsync(buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
