using SpacetimeDB;

public static partial class Module
{
        // Room table - represents game sessions
    [SpacetimeDB.Table(Accessor = "Room", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "by_Players", Columns = new[] {nameof(CurrentPlayers), nameof(MaxPlayers)})]
    public partial struct Room
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong RoomId;
        
        [SpacetimeDB.Unique]
        public string RoomCode;        // Human-readable room code like "ABCD"
        public int MaxPlayers;         // Room capacity
        public int CurrentPlayers;     // Current number of players in the room
        public bool IsActive;          // Room status
        public Timestamp StartTime;        // When the game started

        public List<UInt16> TileOrder; // Current order of tiles in the game (serialized as a list of tile numbers)
    }
    [SpacetimeDB.Table(Name = "game_timer", Public = true,Scheduled = nameof(GameTimerReducer),ScheduledAt = nameof(GameTimer.Schedule))]
    public partial struct GameTimer
    {        
        [SpacetimeDB.PrimaryKey,SpacetimeDB.AutoInc]
        public ulong TimerId; // Single row with a fixed ID (e.g., 1) to trigger the reducer
        public ScheduleAt Schedule; // Schedule for the timer (e.g., every second)
    }


        // Player table - represents players in rooms
    [SpacetimeDB.Table(Accessor = "Player", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "by_room", Columns = new[] { nameof(RoomId) })]
    public partial struct Player
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong PlayerId;

        public ulong RoomId;           // Foreign key to Room
        [SpacetimeDB.Unique]
        public Identity UserId;       // Player's user identity (unique - one player per user)
        public string PlayerName;     // Display name
        public Timestamp EndTime;        // When the player finished the game (if applicable)
        public bool isReady;          // Is the player ready to start?
    }

    // Scheduled table for delayed actions (timers)
    [SpacetimeDB.Table(Accessor = "Timer", Scheduled = nameof(ProcessTimer))]
    public partial struct Timer
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong TimerId;
        
        public ulong RoomId;           // Which room this timer is for
        public string TimerType;       // "game_start", "game_timeout", etc.
        public string Data;            // Any additional data (JSON)
        public ScheduleAt ScheduledAt; // When to trigger (REQUIRED for scheduled tables)
    }

    [SpacetimeDB.Table(Name = "GameBoard", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "by_room", Columns = new[] { nameof(RoomId) })]
    [SpacetimeDB.Index.BTree(Accessor = "by_player", Columns = new[] { nameof(PlayerId) })]
    public partial struct GameBoard
    {
        [SpacetimeDB.PrimaryKey]
        [SpacetimeDB.AutoInc]
        public ulong BoardId;
        
        public ulong RoomId;           // Foreign key to Room (multiple boards per room allowed)
        public Identity PlayerId;      // Which player owns this board
        public string BoardState;      // Serialized game state (e.g., JSON)
        public Timestamp UpdatedAt;
        public int MoveCount;           // Number of moves made by the player
        public bool IsCompleted;       // Has this player finished?
    }
    [SpacetimeDB.Type]
    public partial struct TimeAndPlayerName
    {
        public double Time;
        public string PlayerName;
    }

    [SpacetimeDB.Reducer]
    public static void GameTimerReducer(ReducerContext ctx, GameTimer timer)
    {
        // runs once to start the timer of the p
    }


    [SpacetimeDB.Reducer]
    public static void CreateRoom(ReducerContext ctx, string roomCode, int maxPlayers = 2, string playerName = "Host")
    {
        // Check if the room code is already taken
        if (ctx.Db.Room.RoomCode.Find(roomCode) != null)
        {
            throw new Exception("Room code already exists");
        }

        var hostId = ctx.Sender; // The player creating the room is the host
        var rng  = ctx.Rng;
        UInt16 remainingNumbers =0; 
        List<UInt16> tileOrder = new List<UInt16>(16);
        for (int i = 0; i < 16; i++)
        {
            UInt16 tileNumber=0;
            do
            {
                tileNumber = (UInt16)rng.NextInt64(0, 16); // Generate a random tile number between 0 and 15
            } while ((remainingNumbers & (1 << tileNumber)) != 0); // Check if the tile number has already been used
            remainingNumbers |= (UInt16)(1 << tileNumber);
            tileOrder.Add(tileNumber);
        }
        

        var newRoom = new Room
        {
            RoomCode = roomCode,
            MaxPlayers = maxPlayers,
            CurrentPlayers = 0,  // Initialize to 0
            IsActive = true,
            TileOrder = tileOrder
        };
        ctx.Db.Room.Insert(newRoom);

        JoinRoom(ctx, roomCode, playerName);
    }    

    [SpacetimeDB.Reducer]
    public static void JoinRoom(ReducerContext ctx, string roomCode, string playerName)
    {
        var userId = ctx.Sender; // The player joining the room
        
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found or inactive");
        }
        if(!room.IsActive)
        {
            throw new Exception("Room is not active");
        }
        if(room.CurrentPlayers >= room.MaxPlayers)
        {
            throw new Exception("Room is full");
        }
        
        // Check if player is already in any room (UserId is unique, so Find is sufficient)
        if(ctx.Db.Player.UserId.Find(userId) is Player existingPlayer)
        {
            throw new Exception("Player is already in a room");
        }

        var newPlayer = new Player
        {
            RoomId = room.RoomId,
            UserId = userId,
            PlayerName = playerName,
            isReady = false
        };
        ctx.Db.Player.Insert(newPlayer);
       
        int newCurrentPlayers = room.CurrentPlayers + 1;
        
        Log.Info($"Player {playerName} joined room {roomCode}. Current players: {newCurrentPlayers}/{room.MaxPlayers}");
        
        // Update room with current players count and optionally start time if full
        if (newCurrentPlayers == room.MaxPlayers)
        {
            // Room is now full - update both CurrentPlayers and StartTime in single operation
            ctx.Db.Room.RoomId.Update(room with 
            { 
                CurrentPlayers = newCurrentPlayers,
                StartTime = ctx.Timestamp
            });
            Log.Info($"Room {roomCode} is now full. Game starting!");
        }
        else
        {
            // Room not full yet - just update CurrentPlayers
            ctx.Db.Room.RoomId.Update(room with 
            { 
                CurrentPlayers = newCurrentPlayers
            });
        }
        
        var tileOrder = room.TileOrder;
        
        string tileOrderStr = string.Join(",", tileOrder);
        //give player a game board with the initial tile order
        ctx.Db.GameBoard.Insert(new GameBoard
        {
            RoomId = room.RoomId,
            PlayerId = userId,
            BoardState = tileOrderStr, 
            UpdatedAt = ctx.Timestamp,
            MoveCount = 0,
            IsCompleted = false
        });


    }

    [SpacetimeDB.Reducer]
    public static void LeaveRoom(ReducerContext ctx, string roomCode)
    {
        var userId = ctx.Sender; // The player leaving the room
        
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found");
        }
        
        var roomId = room.RoomId;
        // Check if room is empty after player leaves
        int remainingPlayers = 0;
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (p.RoomId == roomId && p.UserId != userId)
            {
                remainingPlayers++;
            }
        }
        
        // Remove the leaving player
        foreach (var player in ctx.Db.Player.Iter())
        {
            if (player.RoomId == roomId && player.UserId == userId)
            {
                ctx.Db.Player.PlayerId.Delete(player.PlayerId);
                break;
            }
        }
        
        // If no players left, delete the room
        if (remainingPlayers == 0)
        {
            ctx.Db.Room.RoomId.Delete(roomId);
        }
        else
        {
            // Update the current player count
            ctx.Db.Room.RoomId.Update(room with 
            { 
                CurrentPlayers = remainingPlayers 
            });
        }
    }

    [SpacetimeDB.Reducer]
    public static void SetPlayerReady(ReducerContext ctx, string roomCode, bool isReady)
    {
        var userId = ctx.Sender; // The player setting ready status
        
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found");
        }
        
        var roomId = room.RoomId;
        
        foreach (var player in ctx.Db.Player.Iter())
        {
            if (player.RoomId == roomId && player.UserId == userId)
            {
                ctx.Db.Player.PlayerId.Update(player with 
                { 
                    isReady = isReady
                });
                break;
            }
        }
    }

    [SpacetimeDB.Reducer]
    public static void UpdateGameBoard(ReducerContext ctx, string roomCode, string boardState)
    {
        // Verify room exists
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found");
        }
        
        var roomId = room.RoomId;
        
        // More efficient: filter by room first, then find this player's board
        GameBoard? existingBoard = null;
        foreach (var board in ctx.Db.GameBoard.by_room.Filter(roomId))
        {
            if (board.PlayerId == ctx.Sender)
            {
                existingBoard = board;
                break;
            }
        }
        
        if (existingBoard.HasValue)
        {
            var moveCount = existingBoard.Value.MoveCount + 1; // Increment move count
            // Update existing board state for this player
            ctx.Db.GameBoard.BoardId.Update(existingBoard.Value with 
            {
                BoardState = boardState,
                UpdatedAt = ctx.Timestamp,
                MoveCount = moveCount
            });
        }
        else
        {
            // Create new board entry for this player
            ctx.Db.GameBoard.Insert(new GameBoard
            {
                RoomId = roomId,
                PlayerId = ctx.Sender,
                BoardState = boardState,
                UpdatedAt = ctx.Timestamp,
                MoveCount = 1,
                IsCompleted = false
            });
        }
    }

    [SpacetimeDB.Reducer]
    public static void MarkPuzzleCompleted(ReducerContext ctx, string roomCode)
    {
        // Verify room exists
        if (ctx.Db.Room.RoomCode.Find(roomCode) is not Room room)
        {
            throw new Exception("Room not found");
        }
        
        var roomId = room.RoomId;
        
        // More efficient: filter by room first, then find this player's board
        var counter = ctx.Db.GameBoard.by_room.Filter(roomId).Count();
        foreach (var board in ctx.Db.GameBoard.by_room.Filter(roomId))
        {
            if (board.PlayerId == ctx.Sender)
            {
                ctx.Db.GameBoard.BoardId.Update(board with 
                { 
                    IsCompleted = true,
                    UpdatedAt = ctx.Timestamp 
                });
                Log.Info($"Player {ctx.Sender} completed puzzle in room {roomId}!");
                counter--;
                if(counter == 0) {
                    Log.Info($"All players completed puzzle in room {roomId}!");
                    return;                
                }
            }
        }
        
        // If we get here, player doesn't have a board in this room
        Log.Info($"Player {ctx.Sender} has no board in room {roomId}");
    }

    // Timer management reducers
    [SpacetimeDB.Reducer]
    public static void StartGameCountdown(ReducerContext ctx, ulong roomId, uint delaySeconds)
    {
        if (ctx.Db.Room.RoomId.Find(roomId) is not Room room)
        {
            throw new Exception("Room not found");
        }
        
        var triggerTime = ctx.Timestamp + TimeSpan.FromSeconds(delaySeconds);
        
        ctx.Db.Timer.Insert(new Timer
        {
            RoomId = roomId,
            TimerType = "game_start",
            Data = "", // Could store JSON data if needed
            ScheduledAt = new ScheduleAt.Time(triggerTime)
        });
        
        Log.Info($"Game will start in {delaySeconds} seconds for room {room.RoomCode}");
    }

    // Scheduled reducer - called automatically when timer fires
    [SpacetimeDB.Reducer]
    public static void ProcessTimer(ReducerContext ctx, Timer timer)
    {
        Log.Info($"Timer fired: {timer.TimerType} for room {timer.RoomId}");
        
        StartGameTimer(ctx, timer.RoomId);
 
        // Timer row is automatically deleted after this reducer completes
    }

    // Helper methods for timer actions
    private static void StartGameTimer(ReducerContext ctx, ulong roomId)
    {
        if (ctx.Db.Room.RoomId.Find(roomId) is Room room)
        {
            ctx.Db.Room.RoomId.Update(room with 
            { 
                StartTime = ctx.Timestamp,
                IsActive = true 
            });
            Log.Info($"Game started in room {room.RoomCode}");
        }
    }

    //Get the difference in seconds between the players's end time and the room's start time
    [SpacetimeDB.View(Accessor = "PlayerElapsedTime", Public = true)]
    public static List<TimeAndPlayerName> GetPlayersElapsedTime(ViewContext ctx)
    {
        var result = new List<TimeAndPlayerName>();
        if(ctx.Db.Player.UserId.Find(ctx.Sender) is not Player currentPlayer) // Ensure the player exists
        {
            return result;
        }
        if(ctx.Db.Room.RoomId.Find(currentPlayer.RoomId) is not Room room) // Ensure the room exists
        {
            return result;
        }
        var startTime = room.StartTime.MicrosecondsSinceUnixEpoch;
        // Get all players in the same room and calculate their elapsed times
        foreach (var roomPlayer in ctx.Db.Player.by_room.Filter(currentPlayer.RoomId))
        {
            // Only calculate for players who have finished (EndTime > 0)
            if (roomPlayer.EndTime.MicrosecondsSinceUnixEpoch > 0)
            {
                var startTimeMs = startTime;
                var endTimeMs = roomPlayer.EndTime.MicrosecondsSinceUnixEpoch;
                var elapsedSeconds = (endTimeMs - startTimeMs) / 1000000.0; // Convert to seconds
                result.Add(new TimeAndPlayerName
                {
                    Time = elapsedSeconds,
                    PlayerName = roomPlayer.PlayerName
                });
            }
        }
        return result;
    }

}
