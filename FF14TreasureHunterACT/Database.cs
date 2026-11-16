// Database.cs
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace FF14TreasureHunterACT
{
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
    }
}