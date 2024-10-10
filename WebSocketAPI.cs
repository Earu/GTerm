using GTerm.Listeners;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Drawing;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GTerm
{
    internal class LineChunkColor
    {
        public int R { get; set; } = 255;
        public int G { get; set; } = 255;
        public int B { get; set; } = 255;
        public int A { get; set; } = 255;
    }

    internal class LineChunkData
    {
        public LineChunkColor Color { get; set; } = new();
        public string Text { get; set; } = string.Empty;
    }

    internal class LineData
    {
        public int Time { get; set; }
        public List<LineChunkData> Data { get; set; } = [];

        public LineData(DateTime time) {
            int unixTimestamp = (int)time.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
            this.Time = unixTimestamp;
        }
    }

    internal class WebSocketAPI
    {
        private readonly ILogListener Listener;
        private readonly ConcurrentDictionary<Guid, WebSocket> Clients = new();
        private readonly int Port;
        private readonly string? Secret;

        private bool ShouldStop = false;
        private LineData? CurrentLine;

        internal WebSocketAPI(ILogListener listener, int? port, string? secret)
        {
            this.Listener = listener;
            this.Port = port ?? 27512;
            this.Secret = secret;
        }

        internal async Task Start()
        {
            this.ShouldStop = false;

            HttpListener httpListener = new();
            httpListener.Prefixes.Add($"http://localhost:{this.Port}/ws/");
            httpListener.Start();

            while (!this.ShouldStop)
            {
                HttpListenerContext listenerContext = await httpListener.GetContextAsync();
                if (listenerContext.Request.IsWebSocketRequest)
                {
                    // Verify client by checking the secret in query parameters
                    if (!this.IsValidClient(listenerContext.Request))
                    {
                        listenerContext.Response.StatusCode = 403; // Forbidden
                        listenerContext.Response.Close();
                        continue;
                    }

                    WebSocketContext webSocketContext = await listenerContext.AcceptWebSocketAsync(null);
                    WebSocket webSocket = webSocketContext.WebSocket;
                    Guid clientId = Guid.NewGuid();
                    this.Clients.TryAdd(clientId, webSocket);

                    _ = Task.Run(() => this.HandleClientAsync(clientId, webSocket));
                }
                else
                {
                    listenerContext.Response.StatusCode = 400;
                    listenerContext.Response.Close();
                }
            }

            this.Clients.Clear();
        }

        internal void Stop() => this.ShouldStop = true;

        private bool IsValidClient(HttpListenerRequest request)
        {
            if (string.IsNullOrWhiteSpace(this.Secret)) return true;

            NameValueCollection? queryParams = request.QueryString;
            string? clientSecret = queryParams["secret"];
            return clientSecret == this.Secret;
        }

        private async Task HandleClientAsync(Guid clientId, WebSocket webSocket)
        {
            try
            {
                await this.ReceiveMessagesAsync(webSocket);
            }
            finally
            {
                // Remove the client and close the connection when done
                this.Clients.TryRemove(clientId, out _);
                if (webSocket.State != WebSocketState.Closed)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }

                webSocket.Dispose();
            }
        }

        private async Task ReceiveMessagesAsync(WebSocket webSocket)
        {
            byte[] buffer = new byte[1024 * 4];
            ArraySegment<byte> segment = new(buffer);

            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(segment, CancellationToken.None);
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await this.Listener.WriteMessage(message);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
        }

        private async Task SendDataAsync<T>(T data)
        {
            string jsonData = JsonSerializer.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(jsonData);
            ArraySegment<byte> segment = new(buffer);

            foreach (KeyValuePair<Guid, WebSocket> client in this.Clients)
            {
                WebSocket? webSocket = client.Value;
                if (webSocket == null)
                {
                    this.Clients.TryRemove(client.Key, out _);
                    continue;
                }

                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        this.Clients.TryRemove(client.Key, out _);
                    }
                }
            }
        }

        internal async Task FinishDataAsync()
        {
            await this.SendDataAsync(this.CurrentLine);
            this.CurrentLine = null;
        }

        internal void StartData(DateTime time)
        {
            this.CurrentLine = new LineData(time);
        }

        internal void AppendData(Color color, string text)
        {
            this.CurrentLine?.Data.Add(new LineChunkData
            {
                Color = new LineChunkColor { R = color.R, G = color.G, B = color.B, A = color.A },
                Text = text,
            });
        }
    }
}
