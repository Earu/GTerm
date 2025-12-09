using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace GTerm
{
    internal class MCPServer
    {
        private readonly CommandCollector Collector;
        private readonly LuaExecutor LuaExecutor;
        private readonly int Port;
        private readonly string? Secret;
        private bool IsRunning = false;

        internal MCPServer(CommandCollector collector, int port, string? secret)
        {
            this.Collector = collector;
            this.LuaExecutor = new LuaExecutor(collector);
            this.Port = port;
            this.Secret = secret;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            this.IsRunning = true;

            HttpListener listener = new();
            listener.Prefixes.Add($"http://localhost:{this.Port}/");

            try
            {
                listener.Start();
                LocalLogger.WriteLine($"MCP Server started on http://localhost:{this.Port}/");
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Failed to start MCP Server: {ex.Message}");
                return;
            }

            while (this.IsRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), cancellationToken);
                }
                catch (HttpListenerException)
                {
                    // Listener was stopped
                    break;
                }
                catch (Exception ex)
                {
                    LocalLogger.WriteLine($"MCP Server error: {ex.Message}");
                }
            }

            listener.Stop();
            LocalLogger.WriteLine("MCP Server stopped");
        }

        public void Stop()
        {
            this.IsRunning = false;
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                // Enable CORS
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                // Validate secret if configured
                if (!string.IsNullOrWhiteSpace(this.Secret))
                {
                    string? providedSecret = request.QueryString["secret"];
                    if (providedSecret != this.Secret)
                    {
                        LocalLogger.WriteLine("MCP request rejected: invalid or missing secret");
                        response.StatusCode = 403;
                        byte[] errorBytes = Encoding.UTF8.GetBytes("Forbidden");
                        response.ContentLength64 = errorBytes.Length;
                        await response.OutputStream.WriteAsync(errorBytes);
                        response.Close();
                        return;
                    }
                }

                string path = request.Url?.AbsolutePath ?? "/";
                LocalLogger.WriteLine($"MCP request: {request.HttpMethod} {path}");

                if (request.HttpMethod == "GET")
                {
                    await HandleGetRequest(path, response);
                }
                else if (request.HttpMethod == "POST")
                {
                    await HandlePostRequest(request, response);
                }
                else
                {
                    response.StatusCode = 405;
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Error handling MCP request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private async Task HandleGetRequest(string path, HttpListenerResponse response)
        {
            if (path == "/" || path == "/mcp")
            {
                var serverInfo = new
                {
                    name = "gterm",
                    version = "1.0.0",
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        tools = new { }
                    }
                };

                string json = JsonConvert.SerializeObject(serverInfo, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);

                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.StatusCode = 200;

                await response.OutputStream.WriteAsync(buffer);
                response.Close();
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }

        private async Task HandlePostRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            JToken? id = null;
            try
            {
                string body;
                using (StreamReader reader = new(request.InputStream, request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                LocalLogger.WriteLine($"MCP POST body: {body}");

                JObject? jsonRequest = JsonConvert.DeserializeObject<JObject>(body);
                if (jsonRequest == null)
                {
                    await SendError(response, -32700, "Parse error", null);
                    return;
                }

                string? method = jsonRequest["method"]?.ToString();
                id = jsonRequest["id"];

                try
                {
                    object? result = method switch
                    {
                        "initialize" => await HandleInitialize(jsonRequest),
                        "tools/list" => HandleToolsList(),
                        "tools/call" => await HandleToolCall(jsonRequest),
                        "ping" => new { },
                        _ => throw new Exception($"Unknown method: {method}")
                    };

                    await SendResponse(response, result, id);
                }
                catch (Exception ex)
                {
                    LocalLogger.WriteLine($"Error executing method {method}: {ex.Message}");
                    await SendError(response, -32603, ex.Message, id);
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Error in POST handler: {ex.Message}");
                await SendError(response, -32700, ex.Message, id);
            }
        }

        private async Task<object> HandleInitialize(JObject request)
        {
            await Task.CompletedTask;
            return new
            {
                protocolVersion = "2024-11-05",
                serverInfo = new
                {
                    name = "gterm",
                    version = "1.0.0"
                },
                capabilities = new
                {
                    tools = new { }
                }
            };
        }

        private object HandleToolsList()
        {
            return new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "run_gmod_command",
                        description = "Executes a Garry's Mod console command and returns the captured output. WARNING: This can execute potentially dangerous commands like 'lua_run', 'quit', 'disconnect', or change game settings. Commands like 'lua_run' can execute arbitrary SERVER-SIDE code. The tool waits for the first output after sending the command, then collects all console messages for the specified duration. Note: Output may include unrelated console messages due to the asynchronous nature of the game console.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                command = new
                                {
                                    type = "string",
                                    description = "The console command to execute (e.g., 'status', 'lua_run print(\"Hello\")', 'sv_cheats 1'). WARNING: Commands like 'lua_run' execute arbitrary code on the server!"
                                },
                                timeout = new
                                {
                                    type = "number",
                                    description = "Seconds to wait and collect output after first response (default: 1, min: 0.5, max: 30)"
                                }
                            },
                            required = new[] { "command" }
                        }
                    },
                    new
                    {
                        name = "list_gmod_directory",
                        description = "Lists the directory structure of a path within the Garry's Mod installation in a tree format. If no path is specified, lists the root garrysmod folder.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Relative path within the Garry's Mod installation (e.g., 'addons', 'lua/autorun', 'data'). Leave empty for root."
                                },
                                maxDepth = new
                                {
                                    type = "number",
                                    description = "Maximum depth to traverse (default: 3, max: 10)"
                                }
                            },
                            required = new string[] { }
                        }
                    },
                    new
                    {
                        name = "read_gmod_file",
                        description = "Reads the contents of a file from the Garry's Mod installation. Only text files can be read.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Relative path to the file (e.g., 'addons/myAddon/lua/autorun/init.lua', 'cfg/server.cfg')"
                                },
                                maxSizeKB = new
                                {
                                    type = "number",
                                    description = "Maximum file size to read in KB (default: 1024, max: 10240)"
                                }
                            },
                            required = new[] { "path" }
                        }
                    },
                    new
                    {
                        name = "execute_lua_code",
                        description = "Executes Lua code in Garry's Mod by creating a temporary file, running it, and cleaning up. The code will run on the CLIENT. Returns console output captured during execution. WARNING: This can execute arbitrary code in your game - use with caution!",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                code = new
                                {
                                    type = "string",
                                    description = "The Lua code to execute (e.g., 'print(\"Hello World\")' or 'Entity(1):SetHealth(100)')"
                                },
                                timeout = new
                                {
                                    type = "number",
                                    description = "Seconds to wait and collect output after first response (default: 1, min: 0.5, max: 30)"
                                }
                            },
                            required = new[] { "code" }
                        }
                    },
                    new
                    {
                        name = "capture_console_output",
                        description = "Captures all the Garry's Mod console output for a specified duration. Useful for monitoring ongoing events, server messages, or debugging. Simply starts collecting immediately and returns everything that appears in the console during the time window.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                duration = new
                                {
                                    type = "number",
                                    description = "Duration in seconds to capture console output (default: 5, min: 1, max: 60)"
                                }
                            },
                            required = new string[] { }
                        }
                    }
                }
            };
        }

        private async Task<object> HandleToolCall(JObject request)
        {
            string? toolName = request["params"]?["name"]?.ToString();
            JObject? arguments = request["params"]?["arguments"] as JObject;

            LocalLogger.WriteLine($"Executing tool call: {toolName}");

            return toolName switch
            {
                "run_gmod_command" => await HandleRunGmodCommand(arguments),
                "list_gmod_directory" => HandleListGmodDirectory(arguments),
                "read_gmod_file" => HandleReadGmodFile(arguments),
                "execute_lua_code" => await HandleExecuteLuaCode(arguments),
                "capture_console_output" => await HandleCaptureConsoleOutput(arguments),
                _ => throw new Exception($"Unknown tool: {toolName}")
            };
        }

        private async Task<object> HandleRunGmodCommand(JObject? arguments)
        {
            string? command = arguments?["command"]?.ToString();
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new Exception("Missing required parameter: command");
            }

            double timeoutSeconds = arguments?["timeout"]?.Value<double>() ?? 1.0;

            // Limit timeout
            if (timeoutSeconds < 0.5) timeoutSeconds = 0.5;
            if (timeoutSeconds > 30) timeoutSeconds = 30;

            int timeoutMs = (int)(timeoutSeconds * 1000);

            LocalLogger.WriteLine($"Running Gmod command: {command} (timeout: {timeoutSeconds}s)");

            // Execute the command
            CommandResult result = await this.Collector.ExecuteCommandAsync(command, timeoutMs);

            // Format the response
            if (!result.Success)
            {
                throw new Exception(result.Error ?? "Command execution failed");
            }

            StringBuilder outputText = new();
            outputText.AppendLine($"Command: {result.Command}");
            outputText.AppendLine($"Collection Duration: {result.CollectionDurationMs:F0}ms");
            outputText.AppendLine($"Lines Captured: {result.Output.Count}");
            outputText.AppendLine();
            outputText.AppendLine("Output:");
            outputText.AppendLine("-------");

            foreach (OutputLine line in result.Output)
            {
                outputText.Append($"[{line.Timestamp}] {line.Message}");
            }

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = outputText.ToString()
                    }
                }
            };
        }

        private object HandleListGmodDirectory(JObject? arguments)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
            {
                throw new Exception("Could not find Garry's Mod installation path");
            }

            string? relativePath = arguments?["path"]?.ToString() ?? "";
            int maxDepth = arguments?["maxDepth"]?.Value<int>() ?? 3;

            // Limit max depth
            if (maxDepth < 1) maxDepth = 1;
            if (maxDepth > 10) maxDepth = 10;

            string fullPath = string.IsNullOrWhiteSpace(relativePath)
                ? Path.Combine(gmodPath, "garrysmod")
                : Path.Combine(gmodPath, "garrysmod", relativePath);

            LocalLogger.WriteLine($"Listing directory: {fullPath} (depth: {maxDepth})");

            string tree = GmodFileHelper.GenerateDirectoryTree(fullPath, maxDepth);

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = tree
                    }
                }
            };
        }

        private object HandleReadGmodFile(JObject? arguments)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
            {
                throw new Exception("Could not find Garry's Mod installation path");
            }

            string? relativePath = arguments?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new Exception("Missing required parameter: path");
            }

            int maxSizeKB = arguments?["maxSizeKB"]?.Value<int>() ?? 1024;

            // Limit max size
            if (maxSizeKB < 1) maxSizeKB = 1;
            if (maxSizeKB > 10240) maxSizeKB = 10240;

            string basePath = Path.Combine(gmodPath, "garrysmod");

            LocalLogger.WriteLine($"Reading file: {relativePath} (max size: {maxSizeKB}KB)");

            string content = GmodFileHelper.ReadFile(basePath, relativePath, maxSizeKB);

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = content
                    }
                }
            };
        }

        private async Task<object> HandleExecuteLuaCode(JObject? arguments)
        {
            string? code = arguments?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new Exception("Missing required parameter: code");
            }

            double timeoutSeconds = arguments?["timeout"]?.Value<double>() ?? 1.0;

            // Limit timeout
            if (timeoutSeconds < 0.5) timeoutSeconds = 0.5;
            if (timeoutSeconds > 30) timeoutSeconds = 30;

            int timeoutMs = (int)(timeoutSeconds * 1000);

            LocalLogger.WriteLine($"Executing Lua code ({code.Length} chars, timeout: {timeoutSeconds}s)");

            // Execute the Lua code
            ExecuteLuaResult result = await this.LuaExecutor.ExecuteLuaAsync(code, timeoutMs);

            // Format the response
            if (!result.Success)
            {
                throw new Exception(result.Error ?? "Lua execution failed");
            }

            StringBuilder outputText = new();
            outputText.AppendLine($"Lua Execution Result");
            outputText.AppendLine($"====================");
            outputText.AppendLine($"Temp File: {result.FileName}");
            outputText.AppendLine($"Collection Duration: {result.CollectionDurationMs:F0}ms");
            outputText.AppendLine($"Lines Captured: {result.Output.Count}");
            outputText.AppendLine();
            outputText.AppendLine("Console Output:");
            outputText.AppendLine("---------------");

            if (result.Output.Count == 0)
            {
                outputText.AppendLine("(no output captured)");
            }
            else
            {
                foreach (OutputLine line in result.Output)
                {
                    outputText.Append($"[{line.Timestamp}] {line.Message}");
                }
            }

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = outputText.ToString()
                    }
                }
            };
        }

        private async Task<object> HandleCaptureConsoleOutput(JObject? arguments)
        {
            double durationSeconds = arguments?["duration"]?.Value<double>() ?? 5.0;

            // Limit duration
            if (durationSeconds < 1) durationSeconds = 1;
            if (durationSeconds > 60) durationSeconds = 60;

            int durationMs = (int)(durationSeconds * 1000);

            LocalLogger.WriteLine($"Capturing console output for {durationSeconds}s");

            // Capture console output
            CommandResult result = await this.Collector.CaptureConsoleAsync(durationMs);

            // Format the response
            if (!result.Success)
            {
                throw new Exception(result.Error ?? "Console capture failed");
            }

            StringBuilder outputText = new();
            outputText.AppendLine($"Console Capture Result");
            outputText.AppendLine($"======================");
            outputText.AppendLine($"Duration: {durationSeconds:F1}s ({result.CollectionDurationMs:F0}ms actual)");
            outputText.AppendLine($"Lines Captured: {result.Output.Count}");
            outputText.AppendLine();
            outputText.AppendLine("Console Output:");
            outputText.AppendLine("---------------");

            if (result.Output.Count == 0)
            {
                outputText.AppendLine("(no output captured during this time)");
            }
            else
            {
                foreach (OutputLine line in result.Output)
                {
                    outputText.Append($"[{line.Timestamp}] {line.Message}");
                }
            }

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = outputText.ToString()
                    }
                }
            };
        }

        private async Task SendResponse(HttpListenerResponse response, object result, JToken? id)
        {
            var jsonResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                result = result
            };

            string json = JsonConvert.SerializeObject(jsonResponse);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200;

            LocalLogger.WriteLine($"MCP response: {json}");

            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private async Task SendError(HttpListenerResponse response, int code, string message, JToken? id)
        {
            var jsonResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                error = new
                {
                    code = code,
                    message = message
                }
            };

            string json = JsonConvert.SerializeObject(jsonResponse);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200; // JSON-RPC errors still return 200

            LocalLogger.WriteLine($"MCP error response: {json}");

            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
    }
}
