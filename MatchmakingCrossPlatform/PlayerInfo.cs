using System.Diagnostics;
using System.Net.Sockets;

namespace MatchmakingCrossPlatform
{
    // Data structure to store player connection information
    public class PlayerInfo
    {
        public TcpClient Client { get; }
        public int Id { get; }
        public int? SessionId { get; set; } // Track which session the player is in

        public PlayerInfo(TcpClient client, int id)
        {
            Client = client;
            Id = id;
            SessionId = null;
        }
    }

    // Data structure to store game session information
    public class SessionInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ServerIp { get; set; } = string.Empty;
        public int ServerPort { get; set; }
        public Process? ServerProcess { get; set; } // Track the dedicated server process
        public HashSet<int> PlayerIds { get; set; } = new HashSet<int>(); // Track players in this session
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int HostPlayerId { get; set; } // Track who created the session

        /// <summary>
        /// Shuts down the dedicated server process if it's running
        /// </summary>
        public void ShutdownServer()
        {
            if (ServerProcess != null && !ServerProcess.HasExited)
            {
                try
                {
                    Console.WriteLine($"[SESSION-{Id}] Shutting down dedicated server process PID={ServerProcess.Id}");
                    ServerProcess.Kill(entireProcessTree: true);
                    ServerProcess.WaitForExit(5000); // Wait up to 5 seconds
                    ServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SESSION-{Id}] Error shutting down server: {ex.Message}");
                }
            }
        }
    }

}
