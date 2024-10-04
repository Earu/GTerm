using System.Drawing;
using System.Text;

namespace GTerm.Listeners
{
    internal class UnixLogListener : ILogListener
    {
        private const int BUFFER_SIZE = 8192;
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

        private void ProcessMessage(byte[] buffer)
        {
            using (BinaryReader reader = new(new MemoryStream(buffer, 0, buffer.Length)))
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


        private async Task ProcessMessages()
        {
            byte[] eolSequence = Encoding.UTF8.GetBytes("<EOL>");

            int eolIndex = 0;
            List<byte> dataBuffer = [];
            byte[] buffer = new byte[BUFFER_SIZE];
            int bytesRead;

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
                            bytesRead = await this.Pipe.ReadAsync(buffer);
                            if (bytesRead == 0)
                            {
                                await Task.Delay(50);
                                continue;
                            }

                            for (int i = 0; i < bytesRead; i++)
                            {
                                byte currentByte = buffer[i];
                                dataBuffer.Add(currentByte);

                                // Check if the current byte matches the next byte in the EOL sequence
                                if (currentByte == eolSequence[eolIndex])
                                {
                                    eolIndex++;
                                    if (eolIndex == eolSequence.Length)  // Full EOL found
                                    {
                                        dataBuffer.RemoveRange(dataBuffer.Count - eolSequence.Length, eolSequence.Length);  // Remove <EOL> bytes

                                        this.ProcessMessage([.. dataBuffer]);

                                        dataBuffer.Clear();  // Clear the buffer for new data
                                        eolIndex = 0;  // Reset the EOL matching index
                                    }
                                }
                                else
                                {
                                    // If the byte doesn't match, reset the EOL matching index
                                    eolIndex = 0;
                                }
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
