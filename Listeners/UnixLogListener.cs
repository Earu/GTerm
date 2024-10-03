using System.Drawing;
using System.Text;

namespace GTerm.Listeners
{
    internal class UnixLogListener : ILogListener
    {
        private const int BUFFER_SIZE = 8192;
        private readonly byte[] Buffer = new byte[BUFFER_SIZE];

        private FileStream? Pipe;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event ErrorEventHandler? OnError;
        public event LogEventHandler? OnLog;

        public bool IsConnected { get; private set; }

        private void SetConnectionStatus(bool connected)
        {
            if (connected)
                OnConnected?.Invoke(this, EventArgs.Empty);
            else
                OnDisconnected?.Invoke(this, EventArgs.Empty);

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
                    this.Pipe = new FileStream("/tmp/garrysmod_console", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    this.SetConnectionStatus(true);

                    while (true)
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

                                string fullMsg = ReadString(reader);
                                OnLog?.Invoke(this, new LogEventArgs(type, level, group, color, fullMsg));
                            }
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke(this, new ErrorEventArgs(ex));
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.SetConnectionStatus(false);

                    OnError?.Invoke(this, new ErrorEventArgs(ex));
                }
            }
        }

        public void Start() => Task.Run(this.ProcessMessages);

        public async Task WriteMessage(string input)
        {
            try
            {
                if (this.Pipe == null) return;

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
