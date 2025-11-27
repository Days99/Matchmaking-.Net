using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MatchmakingCrossPlatform
{
    public class MatchmakingServer
    {
        private const int Port = 8856;
        private const int RcvBufSize = 1024;
        private const string MessageTerminator = "#";
        private const char Separator = '|';

        // Global, thread-safe lists for players and sessions
        // Using List and manual locks for simplicity, but ConcurrentQueue/ConcurrentBag 
        // are often better in high-concurrency .NET applications.
        private readonly List<PlayerInfo> _players = new();
        private readonly List<SessionInfo> _sessions = new();
        private readonly object _lock = new();

        private int _playerCount = 0;
        private int _sessionCount = 0;

        public async Task StartAsync()
        {
            // IPAddress.Any is equivalent to htonl(INADDR_ANY)
            TcpListener listener = new TcpListener(IPAddress.Any, Port);

            try
            {
                listener.Start();
                Console.WriteLine("Server Started on port 8856!");

                while (true)
                {
                    // AcceptTcpClientAsync is the non-blocking equivalent of accept()
                    TcpClient client = await listener.AcceptTcpClientAsync();

                    // Assign ID and store player information
                    PlayerInfo player;
                    lock (_lock)
                    {
                        player = new PlayerInfo(client, _playerCount++);
                        _players.Add(player);
                    }

                    Console.WriteLine($"Connection from {((IPEndPoint)client.Client.RemoteEndPoint!).Address}. Player ID: {player.Id}");

                    // Run the client handling logic in a separate, non-blocking Task
                    // The '_' discards the result, allowing the main loop to continue immediately.
                    _ = HandleClientAsync(player);
                }
            }
            catch (SocketException e)
            {
                Console.WriteLine($"Socket error: {e.Message}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"An unexpected error occurred: {e.Message}");
            }
            finally
            {
                // Clean up the listener
                listener.Stop();
            }
        }

        /// <summary>
        /// Reads messages asynchronously from a connected client.
        /// </summary>
        private async Task HandleClientAsync(PlayerInfo player)
        {
            TcpClient client = player.Client;
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[RcvBufSize];

            try
            {
                int bytesRead;
                // ReadAsync is the non-blocking equivalent of recv()
                // Loop continues until client closes connection (bytesRead = 0) or an error occurs.
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    // Convert received bytes to a string
                    string message = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    // The C++ code assumed a message was fully received in one recv().
                    // In a robust C# server, we must handle message termination explicitly.
                    if (message.Contains(MessageTerminator))
                    {
                        // Assuming the message is complete and ends with '#'
                        InterpretClientMessage(message, stream, player);
                    }

                    // Clear the buffer for the next read operation (optional, but good practice)
                    Array.Clear(buffer, 0, bytesRead);
                }
            }
            catch (IOException)
            {
                // Client closed the connection (e.g., reset, graceful shutdown)
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error handling client {player.Id}: {e.Message}");
            }
            finally
            {
                // Client disconnected, close the TcpClient (equivalent to close() or closesocket())
                Console.WriteLine($"Player {player.Id} disconnected.");

                // Handle session cleanup if player was in a session
                HandlePlayerDisconnect(player);

                client.Close();

                // Remove player from the global list
                lock (_lock)
                {
                    _players.Remove(player);
                }
            }
        }

        /// <summary>
        /// Parses the client command and sends the appropriate response.
        /// </summary>
        private void InterpretClientMessage(string message, NetworkStream stream, PlayerInfo player)
        {
            // 1. Split the message by the separator ('|')
            // We use StringSplitOptions.RemoveEmptyEntries to avoid issues with double pipes.
            string[] parts = message.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return;

            char cmd = parts[0][0];

            // Command 'g': Get list of all available game sessions
            if (cmd == 'g')
            {
                string response = "s|";

                // Build response message
                lock (_lock)
                {
                    if (_sessions.Count > 0)
                    {
                        foreach (var session in _sessions)
                        {
                            // Response format: "s|id|name|serverip|serverport|playercount|"
                            response += $"{session.Id}|{session.Name}|{session.ServerIp}|{session.ServerPort}|{session.PlayerIds.Count}|";
                        }
                    }
                    else
                    {
                        // No sessions available, send null response
                        response += "null|";
                    }
                }

                // Send the response
                SendResponse(response, stream);
            }
            // Command 'h': Host a new game session
            // Expected parameters: name (parts[1])
            else if (cmd == 'h' && parts.Length >= 2)
            {
                // Parse parameters
                string sessionName = parts[1];

                // Your preset IP (replace with your actual value)
                string serverIp = "68.221.160.33";

                // Pick a port from the allowed range
                int serverPort = GetFreePort();
                if (serverPort == -1)
                {
                    Console.WriteLine("Error: No available ports to host a session.");
                    SendResponse("e|NoAvailablePorts|", stream);
                    return;
                }

                // Start dedicated server instance
                Process? serverProcess = StartDedicatedServer(serverPort);

                // Create and store the session
                SessionInfo session;
                lock (_lock)
                {
                    session = new SessionInfo
                    {
                        Id = _sessionCount++,
                        Name = sessionName,
                        ServerIp = serverIp,
                        ServerPort = serverPort,
                        ServerProcess = serverProcess,
                        HostPlayerId = player.Id
                    };
                    // Add the host player to the session
                    session.PlayerIds.Add(player.Id);
                    player.SessionId = session.Id;

                    _sessions.Add(session);
                }

                // Send confirmation back to client
                // Response format: "o|sessionid|serverip|serverport|"
                string confirmationMessage = $"o|{session.Id}|{session.ServerIp}|{session.ServerPort}|";

                Console.WriteLine($"New session hosted: {session.Name} (ID: {session.Id}) at {session.ServerIp}:{session.ServerPort} by Player {player.Id}");
                SendResponse(confirmationMessage, stream);
            }
            // Command 'j': Join a session
            // Expected parameters: sessionid (parts[1])
            else if (cmd == 'j' && parts.Length >= 2)
            {
                if (int.TryParse(parts[1], out int sessionId))
                {
                    lock (_lock)
                    {
                        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
                        if (session != null)
                        {
                            session.PlayerIds.Add(player.Id);
                            player.SessionId = sessionId;

                            Console.WriteLine($"Player {player.Id} joined session {sessionId}. Total players: {session.PlayerIds.Count}");
                            SendResponse($"j|success|{session.ServerIp}|{session.ServerPort}|", stream);
                        }
                        else
                        {
                            SendResponse("e|SessionNotFound|", stream);
                        }
                    }
                }
            }
            // Command 'd': Disconnect from current session
            else if (cmd == 'd')
            {
                HandlePlayerLeaveSession(player);
                SendResponse("d|success|", stream);
            }
            // Command 'k': Kill/Shutdown session (only host can do this)
            // Expected parameters: sessionid (parts[1])
            else if (cmd == 'k' && parts.Length >= 2)
            {
                if (int.TryParse(parts[1], out int sessionId))
                {
                    lock (_lock)
                    {
                        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
                        if (session != null)
                        {
                            // Check if the player is the host
                            if (session.HostPlayerId == player.Id)
                            {
                                Console.WriteLine($"Player {player.Id} (host) requested shutdown of session {sessionId}");
                                ShutdownSession(session);
                                SendResponse("k|success|", stream);
                            }
                            else
                            {
                                Console.WriteLine($"Player {player.Id} attempted to shutdown session {sessionId} but is not the host");
                                SendResponse("e|NotAuthorized|", stream);
                            }
                        }
                        else
                        {
                            SendResponse("e|SessionNotFound|", stream);
                        }
                    }
                }
            }
            else
            {
                // Unknown command received
                Console.WriteLine($"Unknown message or invalid format: {message.TrimEnd('#')}");
            }
        }

        /// <summary>
        /// Helper to convert string response to bytes and send it.
        /// </summary>
        private void SendResponse(string response, NetworkStream stream)
        {
            // Ensure the response always ends with the terminator if needed by the client
            if (!response.EndsWith(MessageTerminator))
            {
                response += MessageTerminator;
            }

            byte[] data = Encoding.ASCII.GetBytes(response);

            try
            {
                // WriteAsync is the non-blocking equivalent of send()
                stream.Write(data, 0, data.Length);
                stream.Flush(); // Ensure data is immediately sent
            }
            catch (Exception e)
            {
                Console.WriteLine($"send() failed: {e.Message}");
            }
        }

        private int _minPort = 7777;
        private int _maxPort = 8090;
        private HashSet<int> _usedPorts = new HashSet<int>();
        private object _portLock = new object();

        private int GetFreePort()
        {
            lock (_portLock)
            {
                for (int port = _minPort; port <= _maxPort; port++)
                {
                    if (!_usedPorts.Contains(port))
                    {
                        _usedPorts.Add(port);
                        return port;
                    }
                }
            }
            return -1;
        }

        private Process? StartDedicatedServer(int port)
        {
            Console.WriteLine("Start Dedicated Server");

            var scriptPath = "./ExampleProjectServer.sh";
            var workingDir = "../DedicatedServer/";

            Console.WriteLine($"Working directory: {Path.GetFullPath(workingDir)}");
            Console.WriteLine($"Script path: {Path.GetFullPath(Path.Combine(workingDir, scriptPath))}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"{scriptPath} -port {port}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Console.WriteLine($"Launching command: /bin/bash {startInfo.Arguments}");

            var process = Process.Start(startInfo);

            if (process == null)
            {
                Console.WriteLine($"Failed to start dedicated server on port {port}");
                return null;
            }

            Console.WriteLine($"Started server process PID={process.Id} on port {port}");

            // stdout
            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Console.WriteLine($"[SERVER-{port}] {args.Data}");
            };

            // stderr
            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Console.WriteLine($"[SERVER-{port} ERR] {args.Data}");
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // early exit detection
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                if (process.HasExited)
                    Console.WriteLine($"[SERVER-{port}] Process exited early with code {process.ExitCode}");
            });

            return process;
        }

        /// <summary>
        /// Handles player disconnect - removes them from their session and cleans up if needed
        /// </summary>
        private void HandlePlayerDisconnect(PlayerInfo player)
        {
            if (player.SessionId.HasValue)
            {
                Console.WriteLine($"Player {player.Id} disconnected while in session {player.SessionId.Value}");
                HandlePlayerLeaveSession(player);
            }
        }

        /// <summary>
        /// Removes player from their current session and checks if session should be shut down
        /// </summary>
        private void HandlePlayerLeaveSession(PlayerInfo player)
        {
            if (!player.SessionId.HasValue) return;

            lock (_lock)
            {
                var session = _sessions.FirstOrDefault(s => s.Id == player.SessionId.Value);
                if (session != null)
                {
                    session.PlayerIds.Remove(player.Id);
                    Console.WriteLine($"Player {player.Id} left session {session.Id}. Remaining players: {session.PlayerIds.Count}");

                    // If no players left, shut down the session
                    if (session.PlayerIds.Count == 0)
                    {
                        Console.WriteLine($"Session {session.Id} has no players left. Shutting down...");
                        ShutdownSession(session);
                    }
                }
                player.SessionId = null;
            }
        }

        /// <summary>
        /// Shuts down a session: kills the dedicated server and removes it from the session list
        /// </summary>
        private void ShutdownSession(SessionInfo session)
        {
            // This method assumes the lock is already held by the caller

            // Shut down the dedicated server
            session.ShutdownServer();

            // Free up the port
            ReleasePort(session.ServerPort);

            // Remove all players from the session
            foreach (var playerId in session.PlayerIds.ToList())
            {
                var player = _players.FirstOrDefault(p => p.Id == playerId);
                if (player != null)
                {
                    player.SessionId = null;
                }
            }
            session.PlayerIds.Clear();

            // Remove the session from the list
            _sessions.Remove(session);

            Console.WriteLine($"Session {session.Id} ({session.Name}) has been shut down and removed.");
        }

        /// <summary>
        /// Releases a port back to the available pool
        /// </summary>
        private void ReleasePort(int port)
        {
            lock (_portLock)
            {
                if (_usedPorts.Contains(port))
                {
                    _usedPorts.Remove(port);
                    Console.WriteLine($"Port {port} has been released back to the pool.");
                }
            }
        }
    }
}
