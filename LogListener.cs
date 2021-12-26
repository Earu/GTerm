using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

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

        public int Type { get; private set; }
        public int Level { get; private set; }
        public string Group { get; private set; }
        public Color Color { get; private set; }
        public string Message { get; private set; }

    }

    delegate void LogEventHandler(object sender, LogEventArgs args);

    internal class LogListener
    {
        private const int BUFFER_SIZE = 8192;
        private readonly byte[] Buffer = new byte[BUFFER_SIZE];

        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event ErrorEventHandler OnError;
        public event LogEventHandler OnLog;

        public bool IsConnected { get; private set; }

        private void SetConnectionStatus(bool connected)
        {
            if (connected)
                this.OnConnected?.Invoke(this, EventArgs.Empty);
            else
                this.OnDisconnected?.Invoke(this, EventArgs.Empty);

            this.IsConnected = connected;
        }

        private string ReadString(BinaryReader reader)
        {
            List<byte> list = new List<byte>();
            byte ch = 0;
            while ((ch = reader.ReadByte()) != 0)
                list.Add(ch);

            return Encoding.UTF8.GetString(list.ToArray());
        }

        private void ProcessMessages()
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(
                    ".",
                    "garrysmod_console",
                    PipeAccessRights.Read | PipeAccessRights.WriteAttributes,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Anonymous,
                    HandleInheritability.None
                ))
            {
                while (true)
                {
                    try
                    {
                        pipe.Connect();
                        pipe.ReadMode = PipeTransmissionMode.Message;

                        this.SetConnectionStatus(true);

                        while (pipe.IsConnected)
                        {
                            try
                            {
                                int read = pipe.Read(this.Buffer, 0, BUFFER_SIZE);
                                if (read == 0) continue;

                                using (BinaryReader reader = new BinaryReader(new MemoryStream(this.Buffer, 0, read)))
                                {
                                    int type = reader.ReadInt32();
                                    int level = reader.ReadInt32();
                                    string group = this.ReadString(reader);
                                    Color color = Color.FromArgb(
                                        reader.ReadByte(),
                                        reader.ReadByte(),
                                        reader.ReadByte()
                                    );

                                    reader.ReadByte();

                                    string msg = this.ReadString(reader);
                                    this.OnLog?.Invoke(this, new LogEventArgs(type, level, group, color, msg.Replace("<NEWLINE>", "\n")));
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

        }

        public void Start()
        {
            Thread thread = new Thread(this.ProcessMessages);
            thread.Start();
        }
    }
}
