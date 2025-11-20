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
                        InterpretClientMessage(message, stream);
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
        private void InterpretClientMessage(string message, NetworkStream stream)
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
                            // Response format: "s|id|name|serverip|serverport|"
                            response += $"{session.Id}|{session.Name}|{session.ServerIp}|{session.ServerPort}|";
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
            // Expected parameters: name, serverip, serverport (parts[1], parts[2], parts[3])
            else if (cmd == 'h' && parts.Length >= 4)
            {
                // Parse parameters
                string sessionName = parts[1];
                string serverIp = parts[2];

                if (!int.TryParse(parts[3], out int serverPort))
                {
                    Console.WriteLine("Error: Invalid port number received.");
                    return;
                }

                // Create and store the new session
                SessionInfo session;
                lock (_lock)
                {
                    session = new SessionInfo
                    {
                        Id = _sessionCount++,
                        Name = sessionName,
                        ServerIp = serverIp,
                        ServerPort = serverPort
                    };
                    _sessions.Add(session);
                }

                // Send confirmation back to client
                // Response format: "o|serverport|"
                string confirmationMessage = $"o|{serverPort}|";

                Console.WriteLine($"New session hosted: {session.Name} at {session.ServerIp}:{session.ServerPort}");
                SendResponse(confirmationMessage, stream);
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
    }
}
