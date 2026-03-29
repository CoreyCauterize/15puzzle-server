using SpacetimeDB;

public static partial class Module
{
        // Room table - represents game sessions
    [SpacetimeDB.Table(Accessor = "Room", Public = true)]
    public partial struct Room
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong RoomId;
        
        [SpacetimeDB.Unique]
        public string RoomCode;        // Human-readable room code like "ABCD"
        public Identity HostId;        // Who created the room
        public int MaxPlayers;         // Room capacity
        public bool IsActive;          // Room status
    }

        // Player table - represents players in rooms
    [SpacetimeDB.Table(Accessor = "Player", Public = true)]
    public partial struct Player
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong PlayerId;
        
        public ulong RoomId;           // Foreign key to Room
        public Identity UserId;       // Player's user identity
        public string PlayerName;     // Display name
        public Timestamp JoinedAt;
    }

    [SpacetimeDB.Reducer]
    public static void CreateRoom(ReducerContext ctx, string roomCode, int maxPlayers = 2)
    {
        var hostId = ctx.Sender; // The player creating the room is the host
        var newRoom = new Room
        {
            RoomCode = roomCode,
            HostId = hostId,
            MaxPlayers = maxPlayers,
            IsActive = true,
        };
        ctx.Db.Room.Insert(newRoom);
    }    

    [SpacetimeDB.Reducer]
    public static void JoinRoom(ReducerContext ctx, string roomCode, string playerName)
    {
        var userId = ctx.Sender; // The player joining the room
        
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found or inactive");
        }
        
        var newPlayer = new Player
        {
            RoomId = room.RoomId,
            UserId = userId,
            PlayerName = playerName,
            JoinedAt = ctx.Timestamp
        };
        ctx.Db.Player.Insert(newPlayer);
    }

}
