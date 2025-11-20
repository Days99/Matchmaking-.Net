using System.Net.Sockets;

namespace MatchmakingCrossPlatform
{
    // Data structure to store player connection information
    public class PlayerInfo
    {
        public TcpClient Client { get; }
        public int Id { get; }

        public PlayerInfo(TcpClient client, int id)
        {
            Client = client;
            Id = id;
        }
    }

    // Data structure to store game session information
    public class SessionInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ServerIp { get; set; } = string.Empty;
        public int ServerPort { get; set; }
    }

}
