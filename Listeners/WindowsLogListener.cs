using System.Drawing;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace GTerm.Listeners
{
    delegate void LogEventHandler(object sender, LogEventArgs args);

    [SupportedOSPlatform("windows")]
    internal class WindowsLogListener : ILogListener
    {
        private const int BUFFER_SIZE = 16384; // max buffer size as per xconsole Windows named pipe
        private readonly NamedPipeClientStream Pipe;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event ErrorEventHandler? OnError;
        public event LogEventHandler? OnLog;

        internal WindowsLogListener()
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
                    await this.Pipe.ConnectAsync();
                    this.Pipe.ReadMode = PipeTransmissionMode.Message;
                    this.SetConnectionStatus(true);

                    while (this.Pipe.IsConnected)
                    {
                        try
                        {
                            byte[] buffer = new byte[BUFFER_SIZE];
                            int read = await this.Pipe.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE));
                            if (read == 0) continue;

                            using (BinaryReader reader = new(new MemoryStream(buffer, 0, read)))
                            {
                                int type = reader.ReadInt32();
                                int level = reader.ReadInt32();
                                string group = ReadString(reader);
                                int r = reader.ReadByte();
                                int g = reader.ReadByte();
                                int b = reader.ReadByte();
                                int a = reader.ReadByte();

                                Color color = Color.FromArgb(a, r, g, b);

                                string fullMsg = ReadString(reader);
                                OnLog?.Invoke(this, new LogEventArgs(type, level, group, color, fullMsg));
                            }
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke(this, new ErrorEventArgs(ex));
                        }
                    }

                    this.SetConnectionStatus(false);
                }
                catch (TimeoutException) { } // ignore that
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new ErrorEventArgs(ex));
                }
            }
        }

        public void Start() => Task.Run(this.ProcessMessages);

        public async Task WriteMessage(string input)
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
