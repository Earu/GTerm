using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTerm.Listeners
{
    delegate void LogEventHandler(object sender, LogEventArgs args);

    internal interface ILogListener
    {
        internal event EventHandler? OnConnected;
        internal event EventHandler? OnDisconnected;
        internal event ErrorEventHandler? OnError;
        internal event LogEventHandler? OnLog;

        internal bool IsConnected { get; }

        internal void Start();
        internal Task WriteMessage(string input);
    }
}
