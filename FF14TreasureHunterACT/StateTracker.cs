// StateTracker.cs
using System;
using System.Collections.Generic;
using System.Text;

namespace FF14TreasureHunterACT
{
    public class PlayerInfo
    {
        public string Name { get; set; } = "";
        public string World { get; set; } = "";

        public PlayerInfo() { }
        public PlayerInfo(string name, string world) { Name = name; World = world; }
    }

    public class PartyState
    {
        public List<PlayerInfo> Members { get; set; } = new List<PlayerInfo>();
        public int Count { get; set; } = 0;
    }

    /// <summary>
    /// 宝物库状态机：IDLE → (魔纹出现) → WAITING_MAP_SWITCH → (地图切换) → INSIDE_VAULT
    /// 再次地图切换时退出并清理
    /// 对应 Python 的 VaultSegmentStateMachine
    /// </summary>
    public class VaultSegmentStateMachine
    {
        public enum State { Idle = 0, WaitingMapSwitch = 1, InsideVault = 2 }

        private readonly Database _db;
        public State CurrentState { get; private set; } = State.Idle;
        public string EntryTime { get; private set; }
        public string EntryMap { get; private set; }
        public long? CurrentVaultSegmentId { get; private set; }

        public VaultSegmentStateMachine(Database db)
        {
            _db = db;
        }

        public void OnMagicSeal(string timestamp)
        {
            if (CurrentState == State.Idle)
                CurrentState = State.WaitingMapSwitch;
        }

        public void OnMapChange(string timestamp, string mapName)
        {
            if (CurrentState == State.WaitingMapSwitch)
            {
                CurrentState = State.InsideVault;
                EntryTime = timestamp;
                EntryMap = mapName;
                CurrentVaultSegmentId = _db.CreateVaultSegment(timestamp, null, mapName, null);
            }
            else if (CurrentState == State.InsideVault)
            {
                CurrentState = State.Idle;
                if (CurrentVaultSegmentId.HasValue)
                    _db.CheckAndCleanEmptyVault(CurrentVaultSegmentId.Value);

                EntryTime = null;
                EntryMap = null;
                CurrentVaultSegmentId = null;
            }
        }

        /// <summary>用于 UI 重置时手动清理状态</summary>
        public void Reset()
        {
            CurrentState = State.Idle;
            EntryTime = null;
            EntryMap = null;
            CurrentVaultSegmentId = null;
        }
    }

    /// <summary>
    /// 全局状态跟踪器：玩家自身、地图、队伍、玩家缓存
    /// 对应 Python 的 state_tracker.py
    /// </summary>
    public class StateTracker
    {
        private readonly Database _db;
        private readonly object _lock = new object();

        public string SelfName { get; private set; } = "";
        public string SelfWorld { get; private set; } = "";
        public string CurrentMap { get; private set; } = "";
        public string SelfEntityId { get; private set; } = null;
        public PartyState Party { get; private set; } = new PartyState();
        public bool Ready { get; private set; } = false;

        private readonly Dictionary<string, PlayerInfo> _entityCache
            = new Dictionary<string, PlayerInfo>();

        public VaultSegmentStateMachine VaultSm { get; private set; }

        // 供 UI 消费的"待办事件"标志（主线程轮询）
        public volatile bool PendingAutoSegment = false;
        public volatile string PendingAutoSend = null;   // "tell" / "party" / null

        public StateTracker(Database db)
        {
            _db = db;
            VaultSm = new VaultSegmentStateMachine(db);

            // 从数据库恢复
            var saved = _db.GetPlayerState();
            if (saved != null)
            {
                SelfName = saved.Value.SelfName ?? "";
                SelfWorld = saved.Value.SelfWorld ?? "";
                CurrentMap = saved.Value.CurrentMap ?? "";
            }
        }

        // ---------- 更新方法 ----------

        public void UpdateSelf(string name, string world)
        {
            lock (_lock)
            {
                SelfName = name;
                SelfWorld = world;
                _db.UpdatePlayerState(selfName: name, selfWorld: world);
                CheckReady();
            }
        }

        public void UpdateMap(string mapName)
        {
            lock (_lock)
            {
                CurrentMap = mapName;
                _db.UpdatePlayerState(currentMap: mapName);
                VaultSm.OnMapChange(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), mapName);
                CheckReady();
            }
        }

        public void UpdateParty(List<PlayerInfo> members, int totalCount)
        {
            lock (_lock)
            {
                Party.Members = members;
                Party.Count = totalCount;
                _db.UpdatePlayerState(partyCount: totalCount,
                                       partyMembersJson: SerializeMembers(members));
            }
        }

        public void UpdatePlayerInfo(string entityId, string name, string world)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(entityId)) return;
                _entityCache[entityId] = new PlayerInfo(name, world);

                // 若与自己 Entity 相同，则补充自己的 world
                if (!string.IsNullOrEmpty(SelfEntityId) && entityId == SelfEntityId
                    && string.IsNullOrEmpty(SelfWorld))
                {
                    SelfWorld = world;
                    _db.UpdatePlayerState(selfWorld: world);
                    CheckReady();
                }
            }
        }

        public void UpdateSelfEntity(string entityId)
        {
            lock (_lock)
            {
                SelfEntityId = entityId;
            }
        }

        /// <summary>
        /// 解析 PartyList 事件（parts[2..] 是 entity_id 列表），
        /// 从 _entityCache 反查名字和服务器
        /// </summary>
        public void UpdatePartyFromEvent(List<string> memberIds, int count)
        {
            var members = new List<PlayerInfo>();
            lock (_lock)
            {
                foreach (var eid in memberIds)
                {
                    if (_entityCache.TryGetValue(eid, out var info))
                        members.Add(info);
                }
            }
            UpdateParty(members, count);
        }

        public int GetWorkerCount(int adjustment = 0)
        {
            lock (_lock)
            {
                int baseCount = Math.Max(0, Party.Count - 1);
                return Math.Max(0, baseCount + adjustment);
            }
        }

        private void CheckReady()
        {
            if (!string.IsNullOrEmpty(SelfName) && !string.IsNullOrEmpty(CurrentMap))
                Ready = true;
        }

        private string SerializeMembers(List<PlayerInfo> members)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"name\":\"").Append(Escape(members[i].Name)).Append("\",")
                  .Append("\"world\":\"").Append(Escape(members[i].World)).Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}