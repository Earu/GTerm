using System.Drawing;
using System.Net.Sockets;
using System.Text;

namespace GTerm.Listeners
{
    // Single cross-platform listener that talks to the gmod module over a
    // localhost TCP socket. The module is the server (listens on GTERM_PORT);
    // GTerm is the client that connects. Each message is length-prefixed with a
    // little-endian uint32, both for incoming logs and outgoing commands.
    internal class TcpLogListener : ILogListener
    {
        private const string HOST = "127.0.0.1";
        private const int PORT = 27514;
        private const int RECONNECT_DELAY_MS = 1000;

        private TcpClient? Client;
        private NetworkStream? Stream;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event ErrorEventHandler? OnError;
        public event LogEventHandler? OnLog;

        public bool IsConnected { get; private set; } = false;

        private void SetConnectionStatus(bool connected)
        {
            if (connected != this.IsConnected)
            {
                if (connected)
                    OnConnected?.Invoke(this, EventArgs.Empty);
                else
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
            }

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

        private void ProcessMessage(byte[] payload)
        {
            using BinaryReader reader = new(new MemoryStream(payload, 0, payload.Length));

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

        private async Task ProcessMessages()
        {
            byte[] header = new byte[4];

            while (true)
            {
                try
                {
                    this.Client = new TcpClient();
                    await this.Client.ConnectAsync(HOST, PORT);
                    this.Stream = this.Client.GetStream();
                    this.SetConnectionStatus(true);

                    while (this.Client.Connected)
                    {
                        await this.Stream.ReadExactlyAsync(header.AsMemory(0, 4));
                        uint length = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
                        if (length == 0) continue;

                        byte[] payload = new byte[length];
                        await this.Stream.ReadExactlyAsync(payload.AsMemory(0, (int)length));

                        this.ProcessMessage(payload);
                    }
                }
                catch (EndOfStreamException) { } // remote closed the connection
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new ErrorEventArgs(ex));
                }
                finally
                {
                    this.SetConnectionStatus(false);
                    this.Stream?.Dispose();
                    this.Client?.Dispose();
                    this.Stream = null;
                    this.Client = null;
                }

                await Task.Delay(RECONNECT_DELAY_MS);
            }
        }

        public void Start() => Task.Run(this.ProcessMessages);

        public async Task WriteMessage(string input)
        {
            NetworkStream? stream = this.Stream;
            if (stream == null || !this.IsConnected) return;

            try
            {
                input = input.Replace("\0", "");

                byte[] body = Encoding.UTF8.GetBytes(input);
                byte[] frame = new byte[4 + body.Length];
                uint length = (uint)body.Length;
                frame[0] = (byte)(length & 0xFF);
                frame[1] = (byte)((length >> 8) & 0xFF);
                frame[2] = (byte)((length >> 16) & 0xFF);
                frame[3] = (byte)((length >> 24) & 0xFF);
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);

                await stream.WriteAsync(frame);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new ErrorEventArgs(ex));
            }
        }
    }
}
