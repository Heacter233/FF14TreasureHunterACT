// Database.cs
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace FF14TreasureHunterACT
{
    /// <summary>历史页聚合事实：按 日期/小时/车次/物品/层级/宝物库 预聚合后的一行。</summary>
    public class HistoryFact
    {
        public string Date = "";
        public string Hour = "";
        public string RunLabel = "";
        public string Type = "";
        public string ItemName = "";
        public string LayerName = "";
        public string CustomName = "";
        public string VaultMap = "";
        public string Zone = "";
        public double Quantity;
        public int Records;
    }

    /// <summary>历史页“车次”汇总行（收入/成本按插件记录，活动量按事件）。</summary>
    public class HistoryRunRow
    {
        public string Label = "";
        public string FirstTs = "";
        public string LastTs = "";
        public int Maps;
        public int Seals;
        public double Gold;
        public int Events;
        public double Cost;
        public double ExtraCost;
        public double Elapsed;
        public bool HasSummary;
    }

    /// <summary>宝物库停留记录（用事件跨度估算时长，vault_segments.end_time 目前不落库）。</summary>
    public class HistoryVaultRow
    {
        public long Id;
        public string StartTime = "";
        public string MapName = "";
        public string LastTs = "";
        public int LootQty;
        public int LayerCount;
    }

    public class Database
    {
        private readonly string _dbPath;
        private SQLiteConnection _conn;
        private readonly object _lock = new object();

        public Database(string dbPath)
        {
            _dbPath = dbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath));
            _conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
            _conn.Open();
            CreateTables();
        }

        private void CreateTables()
        {
            string sql = @"
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    raw_log TEXT,
                    extra TEXT,
                    run_label TEXT NOT NULL DEFAULT '',
                    plugin_id TEXT DEFAULT NULL
                );

                CREATE TABLE IF NOT EXISTS loot_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER REFERENCES events(id),
                    item_name TEXT NOT NULL,
                    quantity INTEGER DEFAULT 1,
                    map_name TEXT,
                    vault_segment_id INTEGER
                );

                CREATE TABLE IF NOT EXISTS layer_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER REFERENCES events(id),
                    layer_name TEXT NOT NULL,
                    vault_segment_id INTEGER
                );

                CREATE TABLE IF NOT EXISTS custom_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER REFERENCES events(id),
                    record_name TEXT NOT NULL,
                    value REAL DEFAULT 0,
                    vault_segment_id INTEGER
                );

                CREATE TABLE IF NOT EXISTS segments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT,
                    start_time TEXT,
                    end_time TEXT
                );

                CREATE TABLE IF NOT EXISTS vault_segments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_time TEXT,
                    end_time TEXT,
                    map_name TEXT,
                    segment_id INTEGER REFERENCES segments(id)
                );

                CREATE TABLE IF NOT EXISTS player_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    self_name TEXT,
                    self_world TEXT,
                    current_map TEXT,
                    party_count INTEGER DEFAULT 1,
                    party_members TEXT
                );

                CREATE TABLE IF NOT EXISTS cost_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_label TEXT NOT NULL,
                    timestamp TEXT,
                    worker_count REAL,
                    salary_per_map REAL,
                    map_cost REAL,
                    total_maps INTEGER DEFAULT 0,
                    total_system_gold REAL DEFAULT 0.0,
                    item_details TEXT,
                    total_income REAL DEFAULT 0.0,
                    total_cost REAL DEFAULT 0.0,
                    profit REAL DEFAULT 0.0
                );

                CREATE TABLE IF NOT EXISTS run_summary (
                    run_label TEXT PRIMARY KEY,
                    total_income REAL DEFAULT 0.0,
                    total_cost REAL DEFAULT 0.0,
                    profit REAL DEFAULT 0.0,
                    extra_cost REAL DEFAULT 0.0,
                    elapsed_seconds REAL DEFAULT 0.0,
                    updated_at TEXT
                );

                CREATE TABLE IF NOT EXISTS segment_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    segment_id INTEGER REFERENCES segments(id),
                    snapshot TEXT,
                    created_at TEXT
                );

                -- 历史页按日期/车次/关联表查询，给老库补索引（幂等）
                CREATE INDEX IF NOT EXISTS idx_events_ts ON events(timestamp);
                CREATE INDEX IF NOT EXISTS idx_events_type_ts ON events(event_type, timestamp);
                CREATE INDEX IF NOT EXISTS idx_events_run_ts ON events(run_label, timestamp);
                CREATE INDEX IF NOT EXISTS idx_loot_event ON loot_items(event_id);
                CREATE INDEX IF NOT EXISTS idx_loot_vault ON loot_items(vault_segment_id);
                CREATE INDEX IF NOT EXISTS idx_layer_event ON layer_entries(event_id);
                CREATE INDEX IF NOT EXISTS idx_custom_event ON custom_records(event_id);

                INSERT OR IGNORE INTO player_state (id) VALUES (1);
            ";
            Execute(sql);
        }

        private void Execute(string sql)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public long LogEvent(string timestamp, string eventType, string rawLog = null,
                              string extra = null, string runLabel = "")
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO events (timestamp, event_type, raw_log, extra, run_label)
                                         VALUES (@ts, @type, @raw, @extra, @label);
                                         SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("@ts", timestamp);
                    cmd.Parameters.AddWithValue("@type", eventType);
                    cmd.Parameters.AddWithValue("@raw", (object)rawLog ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@extra", (object)extra ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@label", runLabel ?? "");
                    return (long)cmd.ExecuteScalar();
                }
            }
        }

        public void AddLoot(long eventId, string itemName, int quantity,
                             string mapName, long? vaultSegmentId = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO loot_items
                                         (event_id, item_name, quantity, map_name, vault_segment_id)
                                         VALUES (@eid, @name, @qty, @map, @vid);";
                    cmd.Parameters.AddWithValue("@eid", eventId);
                    cmd.Parameters.AddWithValue("@name", itemName);
                    cmd.Parameters.AddWithValue("@qty", quantity);
                    cmd.Parameters.AddWithValue("@map", (object)mapName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vid", (object)vaultSegmentId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void AddLayer(long eventId, string layerName, long? vaultSegmentId = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO layer_entries (event_id, layer_name, vault_segment_id)
                                         VALUES (@eid, @name, @vid);";
                    cmd.Parameters.AddWithValue("@eid", eventId);
                    cmd.Parameters.AddWithValue("@name", layerName);
                    cmd.Parameters.AddWithValue("@vid", (object)vaultSegmentId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void AddCustomRecord(long eventId, string recordName, double value,
                                     long? vaultSegmentId = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO custom_records
                                         (event_id, record_name, value, vault_segment_id)
                                         VALUES (@eid, @name, @val, @vid);";
                    cmd.Parameters.AddWithValue("@eid", eventId);
                    cmd.Parameters.AddWithValue("@name", recordName);
                    cmd.Parameters.AddWithValue("@val", value);
                    cmd.Parameters.AddWithValue("@vid", (object)vaultSegmentId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public int GetTotalMaps(string runLabel = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events WHERE event_type='map_complete'";
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText += " AND run_label=@label";
                        cmd.Parameters.AddWithValue("@label", runLabel);
                    }
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public int GetTotalMagicSeals(string runLabel = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events WHERE event_type='magic_seal'";
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText += " AND run_label=@label";
                        cmd.Parameters.AddWithValue("@label", runLabel);
                    }
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public double GetTotalSystemGold(string runLabel = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(SUM(CAST(extra AS REAL)), 0) FROM events WHERE event_type='system_gold'";
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText += " AND run_label=@label";
                        cmd.Parameters.AddWithValue("@label", runLabel);
                    }
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? 0.0 : Convert.ToDouble(result);
                }
            }
        }

        public List<(string ItemName, int Quantity)> GetLootSummary(string runLabel = null)
        {
            var list = new List<(string, int)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText = @"SELECT item_name, SUM(quantity) as total FROM loot_items
                                             JOIN events ON loot_items.event_id = events.id
                                             WHERE events.run_label=@label
                                             GROUP BY item_name";
                        cmd.Parameters.AddWithValue("@label", runLabel);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT item_name, SUM(quantity) as total FROM loot_items GROUP BY item_name";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add((reader.GetString(0), reader.GetInt32(1)));
                        }
                    }
                }
            }
            return list;
        }

        public void Close()
        {
            _conn?.Close();
            _conn?.Dispose();
            _conn = null;
        }
        // ---------- 队伍/玩家状态 ----------
        public void UpdatePlayerState(string selfName = null, string selfWorld = null,
                                       string currentMap = null, int? partyCount = null,
                                       string partyMembersJson = null)
        {
            var sets = new List<string>();
            var parameters = new List<(string, object)>();
            if (selfName != null) { sets.Add("self_name=@sn"); parameters.Add(("@sn", selfName)); }
            if (selfWorld != null) { sets.Add("self_world=@sw"); parameters.Add(("@sw", selfWorld)); }
            if (currentMap != null) { sets.Add("current_map=@cm"); parameters.Add(("@cm", currentMap)); }
            if (partyCount.HasValue) { sets.Add("party_count=@pc"); parameters.Add(("@pc", partyCount.Value)); }
            if (partyMembersJson != null) { sets.Add("party_members=@pm"); parameters.Add(("@pm", partyMembersJson)); }
            if (sets.Count == 0) return;

            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"UPDATE player_state SET {string.Join(", ", sets)} WHERE id=1";
                    foreach (var (name, val) in parameters)
                        cmd.Parameters.AddWithValue(name, val);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public (string SelfName, string SelfWorld, string CurrentMap, int PartyCount, string PartyMembers)?
            GetPlayerState()
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT self_name, self_world, current_map, party_count, party_members FROM player_state WHERE id=1";
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        return (
                            reader.IsDBNull(0) ? "" : reader.GetString(0),
                            reader.IsDBNull(1) ? "" : reader.GetString(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2),
                            reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                            reader.IsDBNull(4) ? "" : reader.GetString(4)
                        );
                    }
                }
            }
        }

        // ---------- 宝物库段 ----------
        public long CreateVaultSegment(string startTime, string endTime, string mapName, long? segmentId)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO vault_segments (start_time, end_time, map_name, segment_id)
                                 VALUES (@st, @et, @mn, @sid); SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("@st", startTime);
                    cmd.Parameters.AddWithValue("@et", (object)endTime ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mn", (object)mapName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sid", (object)segmentId ?? DBNull.Value);
                    return (long)cmd.ExecuteScalar();
                }
            }
        }

        public void CheckAndCleanEmptyVault(long vaultId)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT
                (SELECT COUNT(*) FROM loot_items WHERE vault_segment_id=@vid)
              + (SELECT COUNT(*) FROM layer_entries WHERE vault_segment_id=@vid)
              + (SELECT COUNT(*) FROM custom_records WHERE vault_segment_id=@vid)";
                    cmd.Parameters.AddWithValue("@vid", vaultId);
                    int total = Convert.ToInt32(cmd.ExecuteScalar());
                    if (total == 0)
                    {
                        using (var del = _conn.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM vault_segments WHERE id=@vid";
                            del.Parameters.AddWithValue("@vid", vaultId);
                            del.ExecuteNonQuery();
                        }
                    }
                }
            }
        }

        // ---------- 分段 ----------
        public long CreateSegment(string name, string startTime, string endTime = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO segments (name, start_time, end_time)
                                 VALUES (@n, @st, @et); SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("@n", name ?? "");
                    cmd.Parameters.AddWithValue("@st", startTime);
                    cmd.Parameters.AddWithValue("@et", (object)endTime ?? DBNull.Value);
                    return (long)cmd.ExecuteScalar();
                }
            }
        }

        public void EndSegment(long segmentId, string endTime)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE segments SET end_time=@et WHERE id=@id";
                    cmd.Parameters.AddWithValue("@et", endTime);
                    cmd.Parameters.AddWithValue("@id", segmentId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SaveSegmentSnapshot(long segmentId, string snapshotJson)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO segment_snapshots (segment_id, snapshot, created_at)
                                 VALUES (@sid, @s, @c)";
                    cmd.Parameters.AddWithValue("@sid", segmentId);
                    cmd.Parameters.AddWithValue("@s", snapshotJson ?? "{}");
                    cmd.Parameters.AddWithValue("@c", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ---------- Run Summary ----------
        public void UpsertRunSummary(string runLabel, double totalIncome, double totalCost,
                                      double profit, double extraCost, double elapsedSeconds)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
                INSERT INTO run_summary (run_label, total_income, total_cost, profit, extra_cost, elapsed_seconds, updated_at)
                VALUES (@rl, @ti, @tc, @pf, @ec, @es, @ua)
                ON CONFLICT(run_label) DO UPDATE SET
                    total_income=@ti, total_cost=@tc, profit=@pf,
                    extra_cost=@ec, elapsed_seconds=@es, updated_at=@ua";
                    cmd.Parameters.AddWithValue("@rl", runLabel);
                    cmd.Parameters.AddWithValue("@ti", totalIncome);
                    cmd.Parameters.AddWithValue("@tc", totalCost);
                    cmd.Parameters.AddWithValue("@pf", profit);
                    cmd.Parameters.AddWithValue("@ec", extraCost);
                    cmd.Parameters.AddWithValue("@es", elapsedSeconds);
                    cmd.Parameters.AddWithValue("@ua", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public (double TotalIncome, double TotalCost, double Profit, double ExtraCost, double Elapsed)?
            GetRunSummary(string runLabel)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT total_income, total_cost, profit, extra_cost, elapsed_seconds
                                 FROM run_summary WHERE run_label=@rl";
                    cmd.Parameters.AddWithValue("@rl", runLabel);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        return (
                            reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2),
                            reader.GetDouble(3), reader.GetDouble(4)
                        );
                    }
                }
            }
        }

        // ---------- 物品按地图分组 ----------
        public List<(string MapName, string ItemName, int Quantity)> GetLootSummaryByMap(string runLabel = null)
        {
            var list = new List<(string, string, int)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText = @"SELECT map_name, item_name, SUM(quantity) FROM loot_items
                                     JOIN events ON loot_items.event_id = events.id
                                     WHERE events.run_label=@rl
                                     GROUP BY map_name, item_name ORDER BY MIN(loot_items.id)";
                        cmd.Parameters.AddWithValue("@rl", runLabel);
                    }
                    else
                    {
                        cmd.CommandText = @"SELECT map_name, item_name, SUM(quantity) FROM loot_items
                                     GROUP BY map_name, item_name ORDER BY MIN(loot_items.id)";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add((
                                reader.IsDBNull(0) ? "" : reader.GetString(0),
                                reader.GetString(1),
                                reader.GetInt32(2)
                            ));
                        }
                    }
                }
            }
            return list;
        }

        // ---------- 层级 ----------
        public List<(string LayerName, int Count)> GetLayerSummary(string runLabel = null)
        {
            var list = new List<(string, int)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText = @"SELECT layer_name, COUNT(*) FROM layer_entries
                                     JOIN events ON layer_entries.event_id = events.id
                                     WHERE events.run_label=@rl
                                     GROUP BY layer_name";
                        cmd.Parameters.AddWithValue("@rl", runLabel);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT layer_name, COUNT(*) FROM layer_entries GROUP BY layer_name";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add((reader.GetString(0), reader.GetInt32(1)));
                    }
                }
            }
            return list;
        }

        public List<(string MapName, string LayerName, int Count)> GetLayerSummaryByMap(string runLabel = null)
        {
            var list = new List<(string, string, int)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // 层级事件的 events.extra 字段记录了当时的地图名
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText = @"SELECT COALESCE(events.extra, '未知'), layer_entries.layer_name, COUNT(*)
                                     FROM layer_entries
                                     JOIN events ON layer_entries.event_id = events.id
                                     WHERE events.event_type='layer' AND events.run_label=@rl
                                     GROUP BY events.extra, layer_entries.layer_name
                                     ORDER BY MIN(layer_entries.id)";
                        cmd.Parameters.AddWithValue("@rl", runLabel);
                    }
                    else
                    {
                        cmd.CommandText = @"SELECT COALESCE(events.extra, '未知'), layer_entries.layer_name, COUNT(*)
                                     FROM layer_entries
                                     JOIN events ON layer_entries.event_id = events.id
                                     WHERE events.event_type='layer'
                                     GROUP BY events.extra, layer_entries.layer_name
                                     ORDER BY MIN(layer_entries.id)";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                    }
                }
            }
            return list;
        }

        // ---------- 自定义记录 ----------
        public List<(string RecordName, double Total)> GetCustomRecordSummary(string runLabel = null)
        {
            var list = new List<(string, double)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(runLabel))
                    {
                        cmd.CommandText = @"SELECT record_name, SUM(value) FROM custom_records
                                     JOIN events ON custom_records.event_id = events.id
                                     WHERE events.run_label=@rl
                                     GROUP BY record_name";
                        cmd.Parameters.AddWithValue("@rl", runLabel);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT record_name, SUM(value) FROM custom_records GROUP BY record_name";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add((reader.GetString(0), reader.GetDouble(1)));
                    }
                }
            }
            return list;
        }

        // ---------- run_label ----------
        public List<string> GetDistinctRunLabels(string dateStr = null)
        {
            var list = new List<string>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(dateStr))
                    {
                        cmd.CommandText = "SELECT DISTINCT run_label FROM events WHERE substr(timestamp,1,10)=@d";
                        cmd.Parameters.AddWithValue("@d", dateStr);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT DISTINCT run_label FROM events";
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
                    }
                }
            }
            return list;
        }

        // ---------- 成本快照 ----------
        public long AddCostSnapshot(string runLabel, string timestamp, double workerCount,
                                     double salaryPerMap, double mapCost, int totalMaps,
                                     double totalSystemGold, string itemDetailsJson,
                                     double totalIncome, double totalCost, double profit)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
                INSERT INTO cost_snapshots
                (run_label, timestamp, worker_count, salary_per_map, map_cost, total_maps,
                 total_system_gold, item_details, total_income, total_cost, profit)
                VALUES (@rl, @ts, @wc, @sp, @mc, @tm, @tg, @id, @ti, @tc, @pf);
                SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("@rl", runLabel);
                    cmd.Parameters.AddWithValue("@ts", timestamp);
                    cmd.Parameters.AddWithValue("@wc", workerCount);
                    cmd.Parameters.AddWithValue("@sp", salaryPerMap);
                    cmd.Parameters.AddWithValue("@mc", mapCost);
                    cmd.Parameters.AddWithValue("@tm", totalMaps);
                    cmd.Parameters.AddWithValue("@tg", totalSystemGold);
                    cmd.Parameters.AddWithValue("@id", itemDetailsJson ?? "{}");
                    cmd.Parameters.AddWithValue("@ti", totalIncome);
                    cmd.Parameters.AddWithValue("@tc", totalCost);
                    cmd.Parameters.AddWithValue("@pf", profit);
                    return (long)cmd.ExecuteScalar();
                }
            }
        }

        public List<(double TotalIncome, double TotalCost, double Profit)> GetCostSnapshotsByLabel(string runLabel)
        {
            var list = new List<(double, double, double)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT total_income, total_cost, profit FROM cost_snapshots
                                 WHERE run_label=@rl ORDER BY id";
                    cmd.Parameters.AddWithValue("@rl", runLabel);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add((reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)));
                    }
                }
            }
            return list;
        }

        // ---------- 历史查询（用于 UI） ----------
        public List<string> GetAvailableDates()
        {
            var list = new List<string>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT substr(timestamp,1,10) FROM events ORDER BY 1 DESC";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add(reader.GetString(0));
                    }
                }
            }
            return list;
        }

        public List<(string Type, string Name, double Value, string Timestamp, string VaultMap)>
            GetDailyTimeline(string dateStr, string runLabel = null)
        {
            var list = new List<(string, string, double, string, string)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    string labelFilter = string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl";

                    // 用 UNION 合并五种事件；简化版：分别查询再合并排序
                    // 为了清晰，这里给出 loot / layer / custom / map_complete / magic_seal 五路
                    cmd.CommandText = @"
                SELECT 'loot' AS type, loot_items.item_name AS name, loot_items.quantity AS value,
                       events.timestamp, COALESCE(vault_segments.map_name, '') AS vault_map
                FROM loot_items
                JOIN events ON loot_items.event_id = events.id
                LEFT JOIN vault_segments ON loot_items.vault_segment_id = vault_segments.id
                WHERE substr(events.timestamp,1,10)=@d" + (string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl") + @"
                UNION ALL
                SELECT 'layer', layer_entries.layer_name, 1, events.timestamp,
                       COALESCE(vault_segments.map_name, '')
                FROM layer_entries
                JOIN events ON layer_entries.event_id = events.id
                LEFT JOIN vault_segments ON layer_entries.vault_segment_id = vault_segments.id
                WHERE substr(events.timestamp,1,10)=@d" + (string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl") + @"
                UNION ALL
                SELECT 'custom',
                       CASE custom_records.record_name
                           WHEN 'map_adjust'  THEN '手动调整(地图)'
                           WHEN 'seal_adjust' THEN '手动调整(魔纹)'
                           ELSE custom_records.record_name
                       END,
                       custom_records.value, events.timestamp,
                       COALESCE(vault_segments.map_name, '')
                FROM custom_records
                JOIN events ON custom_records.event_id = events.id
                LEFT JOIN vault_segments ON custom_records.vault_segment_id = vault_segments.id
                WHERE substr(events.timestamp,1,10)=@d" + (string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl") + @"
                UNION ALL
                SELECT 'map_complete', '', 0, events.timestamp, ''
                FROM events WHERE substr(events.timestamp,1,10)=@d AND event_type='map_complete'" +
                        (string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl") + @"
                UNION ALL
                SELECT 'magic_seal', '', 0, events.timestamp, ''
                FROM events WHERE substr(events.timestamp,1,10)=@d AND event_type='magic_seal'" +
                        (string.IsNullOrEmpty(runLabel) ? "" : " AND events.run_label=@rl") + @"
                ORDER BY timestamp ASC";

                    cmd.Parameters.AddWithValue("@d", dateStr);
                    if (!string.IsNullOrEmpty(runLabel))
                        cmd.Parameters.AddWithValue("@rl", runLabel);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add((
                                reader.GetString(0),
                                reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Convert.ToDouble(reader.GetValue(2)),
                                reader.GetString(3),
                                reader.IsDBNull(4) ? "" : reader.GetString(4)
                            ));
                        }
                    }
                }
            }
            return list;
        }

        // ============================================================
        // 历史分析（网页面板）
        // ============================================================

        /// <summary>给历史查询拼 run_label IN (...) 条件，并注册参数。</summary>
        private static string BuildRunFilter(List<string> runLabels, SQLiteCommand cmd, string alias = "e")
        {
            if (runLabels == null || runLabels.Count == 0) return "";
            var names = new List<string>();
            for (int i = 0; i < runLabels.Count; i++)
            {
                string p = "@rl" + i;
                names.Add(p);
                cmd.Parameters.AddWithValue(p, runLabels[i]);
            }
            return " AND " + alias + ".run_label IN (" + string.Join(",", names) + ")";
        }

        /// <summary>
        /// 历史页用的聚合事实：只按 (日期, 小时, 车次, 物品/层级/自定义名, 宝物库, 区域) 聚合，
        /// 由上层再按任意维度透视，避免为每种行列组合写一套 SQL。
        /// </summary>
        public List<HistoryFact> GetHistoryFacts(string from, string to, List<string> runLabels)
        {
            var list = new List<HistoryFact>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    string rf = BuildRunFilter(runLabels, cmd);
                    cmd.CommandText = @"
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'loot', l.item_name, '', '',
       COALESCE(v.map_name,''), COALESCE(l.map_name,''),
       CAST(SUM(l.quantity) AS REAL), COUNT(*)
FROM loot_items l
JOIN events e ON l.event_id = e.id
LEFT JOIN vault_segments v ON l.vault_segment_id = v.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3,5,8,9
UNION ALL
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'layer', '', le.layer_name, '',
       COALESCE(v.map_name,''), COALESCE(e.extra,''),
       CAST(COUNT(*) AS REAL), COUNT(*)
FROM layer_entries le
JOIN events e ON le.event_id = e.id
LEFT JOIN vault_segments v ON le.vault_segment_id = v.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3,6,8,9
UNION ALL
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'map_complete', '', '', '', '', '',
       CAST(COUNT(*) AS REAL), COUNT(*)
FROM events e
WHERE e.event_type='map_complete' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3
UNION ALL
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'magic_seal', '', '', '', '', '',
       CAST(COUNT(*) AS REAL), COUNT(*)
FROM events e
WHERE e.event_type='magic_seal' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3
UNION ALL
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'system_gold', '', '', '', '', '',
       CAST(COALESCE(SUM(CAST(e.extra AS REAL)),0) AS REAL), COUNT(*)
FROM events e
WHERE e.event_type='system_gold' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3
UNION ALL
SELECT substr(e.timestamp,1,10), substr(e.timestamp,12,2), e.run_label,
       'custom', '', '', c.record_name, '', '',
       CAST(COALESCE(SUM(c.value),0) AS REAL), COUNT(*)
FROM custom_records c
JOIN events e ON c.event_id = e.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + @"
GROUP BY 1,2,3,7
";
                    cmd.Parameters.AddWithValue("@from", from);
                    cmd.Parameters.AddWithValue("@to", to);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new HistoryFact
                            {
                                Date = reader.GetString(0),
                                Hour = reader.GetString(1),
                                RunLabel = reader.GetString(2),
                                Type = reader.GetString(3),
                                ItemName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                                LayerName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                CustomName = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                VaultMap = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                Zone = reader.IsDBNull(8) ? "" : reader.GetString(8),
                                Quantity = Convert.ToDouble(reader.GetValue(9)),
                                Records = Convert.ToInt32(reader.GetValue(10))
                            });
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>历史明细（带筛选与分页），供透视下钻/CSV 导出。</summary>
        public List<(string Ts, string RunLabel, string Type, string Name,
                     double Qty, double Gold, string Vault, string Zone)>
            GetHistoryDetail(string from, string to, List<string> runLabels, string type,
                             List<string> items, string layer, string vault, string hour,
                             string weekday, string zone, string custom, string keyword,
                             int page, int size, out int total)
        {
            total = 0;
            var list = new List<(string, string, string, string, double, double, string, string)>();
            if (page < 1) page = 1;
            if (size < 1) size = 100;
            if (size > 5000) size = 5000;

            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    string rf = BuildRunFilter(runLabels, cmd);
                    string extraCond = "";
                    if (!string.IsNullOrEmpty(hour))
                    {
                        extraCond += " AND substr(e.timestamp,12,2)=@hour";
                        cmd.Parameters.AddWithValue("@hour", hour);
                    }
                    if (!string.IsNullOrEmpty(weekday))
                    {
                        extraCond += " AND CAST(strftime('%w', e.timestamp) AS INTEGER)=@weekday";
                        cmd.Parameters.AddWithValue("@weekday", weekday);
                    }
                    if (!string.IsNullOrEmpty(vault))
                        cmd.Parameters.AddWithValue("@vault", vault);
                    if (!string.IsNullOrEmpty(zone))
                        cmd.Parameters.AddWithValue("@zone", zone);
                    if (!string.IsNullOrEmpty(keyword))
                        cmd.Parameters.AddWithValue("@kw", "%" + keyword + "%");
                    if (!string.IsNullOrEmpty(layer))
                        cmd.Parameters.AddWithValue("@layer", layer);
                    if (!string.IsNullOrEmpty(custom))
                        cmd.Parameters.AddWithValue("@custom", custom);

                    string itemCond = "";
                    bool hasItems = items != null && items.Count > 0;
                    if (hasItems)
                    {
                        var ps = new List<string>();
                        for (int i = 0; i < items.Count; i++)
                        {
                            string p = "@it" + i;
                            ps.Add(p);
                            cmd.Parameters.AddWithValue(p, items[i]);
                        }
                        itemCond = " AND l.item_name IN (" + string.Join(",", ps) + ")";
                    }

                    bool wantAll = string.IsNullOrEmpty(type)
                                   && !hasItems && string.IsNullOrEmpty(layer) && string.IsNullOrEmpty(custom)
                                   && string.IsNullOrEmpty(vault) && string.IsNullOrEmpty(zone);
                    bool want(string t)
                    {
                        if (wantAll) return true;
                        if (!string.IsNullOrEmpty(type) && type != t) return false;
                        if (hasItems && t != "loot") return false;
                        if (!string.IsNullOrEmpty(layer) && t != "layer") return false;
                        if (!string.IsNullOrEmpty(custom) && t != "custom") return false;
                        if (!string.IsNullOrEmpty(vault) &&
                            t != "loot" && t != "layer" && t != "custom") return false;
                        if (!string.IsNullOrEmpty(zone) && t != "loot" && t != "layer") return false;
                        return true;
                    }

                    string kwLoot = string.IsNullOrEmpty(keyword) ? "" : " AND l.item_name LIKE @kw";
                    string kwLayer = string.IsNullOrEmpty(keyword) ? "" : " AND le.layer_name LIKE @kw";
                    string kwCustom = string.IsNullOrEmpty(keyword) ? "" : " AND c.record_name LIKE @kw";
                    string vCond = string.IsNullOrEmpty(vault) ? "" : " AND COALESCE(v.map_name,'')=@vault";
                    string zLoot = string.IsNullOrEmpty(zone) ? "" : " AND COALESCE(l.map_name,'')=@zone";
                    string zLayer = string.IsNullOrEmpty(zone) ? "" : " AND COALESCE(e.extra,'')=@zone";

                    var parts = new List<string>();
                    if (want("loot"))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'loot', l.item_name, CAST(l.quantity AS REAL), 0.0,
       COALESCE(v.map_name,''), COALESCE(l.map_name,'')
FROM loot_items l
JOIN events e ON l.event_id = e.id
LEFT JOIN vault_segments v ON l.vault_segment_id = v.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond + itemCond + vCond + zLoot + kwLoot);
                    if (want("layer"))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'layer', le.layer_name, 1.0, 0.0,
       COALESCE(v.map_name,''), COALESCE(e.extra,'')
FROM layer_entries le
JOIN events e ON le.event_id = e.id
LEFT JOIN vault_segments v ON le.vault_segment_id = v.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond + 
                            (string.IsNullOrEmpty(layer) ? "" : " AND le.layer_name=@layer") +
                            vCond + zLayer + kwLayer);
                    if (want("map_complete") && !hasItems && string.IsNullOrEmpty(layer) && string.IsNullOrEmpty(custom))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'map_complete', '', 1.0, 0.0, '', ''
FROM events e
WHERE e.event_type='map_complete' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond);
                    if (want("magic_seal") && !hasItems && string.IsNullOrEmpty(layer) && string.IsNullOrEmpty(custom))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'magic_seal', '', 1.0, 0.0, '', ''
FROM events e
WHERE e.event_type='magic_seal' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond);
                    if (want("system_gold") && !hasItems && string.IsNullOrEmpty(layer) && string.IsNullOrEmpty(custom))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'system_gold', '', 0.0,
       CAST(COALESCE(CAST(e.extra AS REAL),0) AS REAL), '', ''
FROM events e
WHERE e.event_type='system_gold' AND substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond);
                    if (want("custom"))
                        parts.Add(@"
SELECT e.timestamp, e.run_label, 'custom', c.record_name, CAST(c.value AS REAL), 0.0,
       COALESCE(v.map_name,''), ''
FROM custom_records c
JOIN events e ON c.event_id = e.id
LEFT JOIN vault_segments v ON c.vault_segment_id = v.id
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to" + rf + extraCond +
                            (string.IsNullOrEmpty(custom) ? "" : " AND c.record_name=@custom") +
                            vCond + kwCustom);

                    cmd.Parameters.AddWithValue("@from", from);
                    cmd.Parameters.AddWithValue("@to", to);

                    if (parts.Count == 0) return list;
                    string unionSql = string.Join(" UNION ALL ", parts);

                    cmd.CommandText = "SELECT COUNT(*) FROM (" + unionSql + ")";
                    total = Convert.ToInt32(cmd.ExecuteScalar());

                    cmd.CommandText = "SELECT * FROM (" + unionSql + @") ORDER BY 1 DESC LIMIT @lim OFFSET @off";
                    cmd.Parameters.AddWithValue("@lim", size);
                    cmd.Parameters.AddWithValue("@off", (page - 1) * size);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add((
                                reader.GetString(0),
                                reader.IsDBNull(1) ? "" : reader.GetString(1),
                                reader.GetString(2),
                                reader.IsDBNull(3) ? "" : reader.GetString(3),
                                Convert.ToDouble(reader.GetValue(4)),
                                Convert.ToDouble(reader.GetValue(5)),
                                reader.IsDBNull(6) ? "" : reader.GetString(6),
                                reader.IsDBNull(7) ? "" : reader.GetString(7)
                            ));
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>按车次汇总活动量与财务记录（收入口径由上层重算，这里只取原始成本）。</summary>
        public List<HistoryRunRow> GetHistoryRuns(string from, string to)
        {
            var list = new List<HistoryRunRow>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT e.run_label, MIN(e.timestamp), MAX(e.timestamp),
       SUM(CASE WHEN e.event_type='map_complete' THEN 1 ELSE 0 END),
       SUM(CASE WHEN e.event_type='magic_seal' THEN 1 ELSE 0 END),
       CAST(COALESCE(SUM(CASE WHEN e.event_type='system_gold'
                              THEN CAST(e.extra AS REAL) ELSE 0 END),0) AS REAL),
       COUNT(*)
FROM events e
WHERE substr(e.timestamp,1,10) BETWEEN @from AND @to
GROUP BY e.run_label
ORDER BY MIN(e.timestamp) DESC";
                    cmd.Parameters.AddWithValue("@from", from);
                    cmd.Parameters.AddWithValue("@to", to);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var row = new HistoryRunRow
                            {
                                Label = reader.GetString(0),
                                FirstTs = reader.GetString(1),
                                LastTs = reader.GetString(2),
                                Maps = Convert.ToInt32(reader.GetValue(3)),
                                Seals = Convert.ToInt32(reader.GetValue(4)),
                                Gold = Convert.ToDouble(reader.GetValue(5)),
                                Events = Convert.ToInt32(reader.GetValue(6))
                            };
                            list.Add(row);
                        }
                    }
                }

                // 财务记录：优先 run_summary；没有时退回 cost_snapshots 累加
                foreach (var row in list)
                {
                    var summary = GetRunSummary(row.Label);
                    if (summary.HasValue)
                    {
                        row.HasSummary = true;
                        row.Cost = summary.Value.TotalCost;
                        row.ExtraCost = summary.Value.ExtraCost;
                        row.Elapsed = summary.Value.Elapsed;
                    }
                    else
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COALESCE(SUM(total_cost),0), COALESCE(SUM(profit),0) FROM cost_snapshots WHERE run_label=@rl";
                            cmd.Parameters.AddWithValue("@rl", row.Label);
                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    row.Cost = Convert.ToDouble(reader.GetValue(0));
                                }
                            }
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>宝物库停留记录：时长用“进入时间 → 该次最后一条掉落/层级事件”估算。</summary>
        public List<HistoryVaultRow> GetHistoryVaults(string from, string to)
        {
            var list = new List<HistoryVaultRow>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT v.id, COALESCE(v.start_time,''), COALESCE(v.map_name,''),
       COALESCE(
         (SELECT MAX(e.timestamp) FROM loot_items l JOIN events e ON l.event_id=e.id
           WHERE l.vault_segment_id=v.id),
         (SELECT MAX(e2.timestamp) FROM layer_entries le JOIN events e2 ON le.event_id=e2.id
           WHERE le.vault_segment_id=v.id),
         ''
       ) AS last_ts,
       COALESCE((SELECT SUM(l.quantity) FROM loot_items l WHERE l.vault_segment_id=v.id),0),
       COALESCE((SELECT COUNT(*) FROM layer_entries le WHERE le.vault_segment_id=v.id),0)
FROM vault_segments v
WHERE substr(COALESCE(v.start_time,''),1,10) BETWEEN @from AND @to
ORDER BY v.start_time ASC";
                    cmd.Parameters.AddWithValue("@from", from);
                    cmd.Parameters.AddWithValue("@to", to);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new HistoryVaultRow
                            {
                                Id = Convert.ToInt64(reader.GetValue(0)),
                                StartTime = reader.GetString(1),
                                MapName = reader.GetString(2),
                                LastTs = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                LootQty = Convert.ToInt32(reader.GetValue(4)),
                                LayerCount = Convert.ToInt32(reader.GetValue(5))
                            });
                        }
                    }
                }
            }
            return list;
        }

        public List<string> GetDistinctLootItems()
        {
            var list = new List<string>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT item_name FROM loot_items WHERE item_name IS NOT NULL ORDER BY item_name";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read()) list.Add(reader.GetString(0));
                    }
                }
            }
            return list;
        }

        public List<string> GetDistinctLayerNames()
        {
            var list = new List<string>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT layer_name FROM layer_entries WHERE layer_name IS NOT NULL ORDER BY layer_name";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read()) list.Add(reader.GetString(0));
                    }
                }
            }
            return list;
        }
    }
}