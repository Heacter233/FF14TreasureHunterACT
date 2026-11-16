// MapConfigManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    public class MapItem
    {
        public string Name { get; set; } = "";
        public double Price { get; set; } = 0;
        public double Bonus { get; set; } = 0;
    }

    public class MapLayer
    {
        public string Keyword { get; set; } = "";
        public string Nickname { get; set; } = "";
    }

    public class MapConfig
    {
        public string Id { get; set; } = "";               // 稳定 id，用于合并去重
        public string Name { get; set; } = "";             // 显示名 "G18"
        public string MapItemPrefix { get; set; } = "";    // 简称前缀 "G18"
        public string MapName { get; set; } = "";          // 副本名 "巡梦金库"
        public List<MapItem> Items { get; set; } = new List<MapItem>();
        public List<MapLayer> Layers { get; set; } = new List<MapLayer>();
        public bool Published { get; set; } = false;       // 是否纳入发布包
        public bool Enabled { get; set; } = true;
    }

    public class MapConfigManager
    {
        public string ConfigPath { get; private set; }
        public Dictionary<string, MapConfig> Maps { get; private set; }
            = new Dictionary<string, MapConfig>();

        public MapConfigManager(string configPath)
        {
            ConfigPath = configPath;
            Load();
        }

        private void Load()
        {
            if (!File.Exists(ConfigPath))
            {
                CreateDefault();
            }
            try
            {
                var json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                var root = JObject.Parse(json);
                var mapsArr = root["maps"] as JArray;
                if (mapsArr == null) return;

                Maps.Clear();
                foreach (var m in mapsArr)
                {
                    var mc = new MapConfig
                    {
                        Id = m["id"]?.ToString() ?? "",
                        Name = m["name"]?.ToString() ?? "",
                        MapItemPrefix = m["map_item_prefix"]?.ToString() ?? "",
                        MapName = m["map_name"]?.ToString() ?? "",
                        Published = m["published"]?.ToObject<bool>() ?? false,
                        Enabled = m["enabled"]?.ToObject<bool>() ?? true
                    };
                    if (m["items"] is JArray itemsArr)
                    {
                        foreach (var item in itemsArr)
                        {
                            mc.Items.Add(new MapItem
                            {
                                Name = item["name"]?.ToString() ?? "",
                                Price = item["price"]?.ToObject<double>() ?? 0,
                                Bonus = item["bonus"]?.ToObject<double>() ?? 0
                            });
                        }
                    }

                    if (m["layers"] is JArray layersArr)
                    {
                        foreach (var layer in layersArr)
                        {
                            mc.Layers.Add(new MapLayer
                            {
                                Keyword = layer["keyword"]?.ToString() ?? "",
                                Nickname = layer["nickname"]?.ToString() ?? ""
                            });
                        }
                    }

                    if (!string.IsNullOrEmpty(mc.Name))
                        Maps[mc.Name] = mc;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MapConfig] 加载失败: {ex.Message}");
            }
        }

        public void Save()
        {
            var root = new JObject();
            var arr = new JArray();
            foreach (var mc in Maps.Values)
            {
                var m = new JObject
                {
                    ["id"] = mc.Id,
                    ["name"] = mc.Name,
                    ["map_item_prefix"] = mc.MapItemPrefix,
                    ["map_name"] = mc.MapName,
                    ["published"] = mc.Published,
                    ["enabled"] = mc.Enabled
                }; var items = new JArray();
                foreach (var it in mc.Items)
                    items.Add(new JObject { ["name"] = it.Name, ["price"] = it.Price, ["bonus"] = it.Bonus });
                m["items"] = items;

                var layers = new JArray();
                foreach (var ly in mc.Layers)
                    layers.Add(new JObject { ["keyword"] = ly.Keyword, ["nickname"] = ly.Nickname });
                m["layers"] = layers;

                arr.Add(m);
            }
            root["maps"] = arr;

            try
            {
                File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented),
                                  System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MapConfig] 保存失败: {ex.Message}");
            }
        }

        private void CreateDefault()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            var g18 = new MapConfig
            {
                Name = "G18",
                MapItemPrefix = "G18",
                MapName = "巡梦金库",
                Items = new List<MapItem>
                {
                    new MapItem { Name = "羊角笛",      Price = 2900, Bonus = 100 },
                    new MapItem { Name = "小小羊",      Price = 300,  Bonus = 0   },
                    new MapItem { Name = "幻梦武器箱",  Price = 750,  Bonus = 10  },
                    new MapItem { Name = "靛蓝斜纹布",  Price = 100,  Bonus = 0   },
                    new MapItem { Name = "运动布",      Price = 100,  Bonus = 0   }
                },
                Layers = new List<MapLayer>
                {
                    new MapLayer { Keyword = "中梦悦性", Nickname = "中梦" },
                    new MapLayer { Keyword = "小梦怡情", Nickname = "小梦" },
                    new MapLayer { Keyword = "大梦安神", Nickname = "大梦" },
                    new MapLayer { Keyword = "终梦修身", Nickname = "终梦" },
                    new MapLayer { Keyword = "90秒之内", Nickname = "奇梦（箱）" }
                }
            };
            Maps[g18.Name] = g18;
            Save();
        }

        // ---------- 查询方法 ----------

        public MapConfig GetMapByName(string name)
        {
            return Maps.TryGetValue(name, out var mc) ? mc : null;
        }

        /// <summary>物品名 → (MapConfig, MapItem)；用于反向定位地图简称</summary>
        public (MapConfig, MapItem) GetItemByName(string itemName)
        {
            foreach (var mc in Maps.Values)
                foreach (var it in mc.Items)
                    if (it.Name == itemName)
                        return (mc, it);
            return (null, null);
        }

        /// <summary>层级关键词/昵称 → MapConfig</summary>
        public (MapConfig, MapLayer) GetLayerByKeyword(string keyword)
        {
            foreach (var mc in Maps.Values)
                foreach (var ly in mc.Layers)
                    if (ly.Keyword == keyword || ly.Nickname == keyword)
                        return (mc, ly);
            return (null, null);
        }

        /// <summary>遍历所有启用地图，检查 content 是否命中任意层级关键词（多关键词 OR）</summary>
        public (MapConfig Map, MapLayer Layer, string Keyword) MatchLayerContent(string content)
        {
            foreach (var mc in Maps.Values)
            {
                if (!mc.Enabled) continue;
                foreach (var ly in mc.Layers)
                {
                    var keywords = ly.Keyword.Split(',');
                    foreach (var kw in keywords)
                    {
                        var k = kw.Trim();
                        if (!string.IsNullOrEmpty(k) && content.Contains(k))
                            return (mc, ly, k);
                    }
                }
            }
            return (null, null, null);
        }
        // ========== 供设置页使用的 JSON 接口 ==========

        /// <summary>把所有地图序列化为 JSON 字符串</summary>
        public string GetAllAsJson()
        {
            var arr = new JArray();
            foreach (var mc in Maps.Values)
            {
                var m = new JObject
                {
                    ["id"] = mc.Id,
                    ["name"] = mc.Name,
                    ["map_item_prefix"] = mc.MapItemPrefix,
                    ["map_name"] = mc.MapName,
                    ["published"] = mc.Published,
                    ["enabled"] = mc.Enabled
                };
                var items = new JArray();
                foreach (var it in mc.Items)
                    items.Add(new JObject
                    {
                        ["name"] = it.Name,
                        ["price"] = it.Price,
                        ["bonus"] = it.Bonus
                    });
                m["items"] = items;

                var layers = new JArray();
                foreach (var ly in mc.Layers)
                    layers.Add(new JObject
                    {
                        ["keyword"] = ly.Keyword,
                        ["nickname"] = ly.Nickname
                    });
                m["layers"] = layers;

                arr.Add(m);
            }
            return new JObject { ["maps"] = arr }.ToString(Formatting.None);
        }

        /// <summary>保存/覆盖一个地图（同 name 覆盖），并持久化</summary>
        public bool SaveMapFromJson(JObject mapObj)
        {
            try
            {
                if (mapObj == null) return false;
                string name = mapObj["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) return false;

                var mc = new MapConfig
                {
                    Id = mapObj["id"]?.ToString() ?? "",
                    Name = name,
                    MapItemPrefix = mapObj["map_item_prefix"]?.ToString() ?? "",
                    MapName = mapObj["map_name"]?.ToString() ?? "",
                    Published = mapObj["published"]?.ToObject<bool>() ?? false,
                    Enabled = mapObj["enabled"]?.ToObject<bool>() ?? true
                };
                if (mapObj["items"] is JArray itemsArr)
                {
                    foreach (var it in itemsArr)
                        mc.Items.Add(new MapItem
                        {
                            Name = it["name"]?.ToString() ?? "",
                            Price = it["price"]?.ToObject<double>() ?? 0,
                            Bonus = it["bonus"]?.ToObject<double>() ?? 0
                        });
                }

                if (mapObj["layers"] is JArray layersArr)
                {
                    foreach (var ly in layersArr)
                        mc.Layers.Add(new MapLayer
                        {
                            Keyword = ly["keyword"]?.ToString() ?? "",
                            Nickname = ly["nickname"]?.ToString() ?? ""
                        });
                }

                Maps[name] = mc;
                Save();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MapConfig] SaveMapFromJson 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>按 name 删除地图</summary>
        public bool DeleteMap(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return false;
                if (!Maps.ContainsKey(name)) return false;
                Maps.Remove(name);
                Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按 id 查找地图（用于合并去重）</summary>
        public MapConfig GetMapById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var mc in Maps.Values)
                if (mc.Id == id)
                    return mc;
            return null;
        }

        // ========== id 与发布 ==========

        /// <summary>为所有缺失 id 的地图生成 id 并保存（幂等）</summary>
        public void EnsureIds()
        {
            bool changed = false;
            foreach (var mc in Maps.Values)
            {
                if (string.IsNullOrEmpty(mc.Id))
                {
                    mc.Id = GenerateId(mc.Name);
                    changed = true;
                }
            }
            if (changed) Save();
        }

        /// <summary>从显示名生成稳定 id，例如 "G18" → "map_g18"</summary>
        public static string GenerateId(string name)
        {
            if (string.IsNullOrEmpty(name)) return "map_unknown";
            string safe = name.ToLower().Trim();
            safe = System.Text.RegularExpressions.Regex.Replace(
                safe, @"[^a-z0-9\u4e00-\u9fff]+", "_");
            return "map_" + safe;
        }

        /// <summary>导出已发布的地图（剥离 published 字段）</summary>
        public string GetPublishableMapsJson()
        {
            var arr = new JArray();
            foreach (var mc in Maps.Values)
            {
                if (!mc.Published) continue;

                var m = new JObject
                {
                    ["id"] = mc.Id,
                    ["name"] = mc.Name,
                    ["map_item_prefix"] = mc.MapItemPrefix,
                    ["map_name"] = mc.MapName,
                    ["enabled"] = mc.Enabled
                };
                var items = new JArray();
                foreach (var it in mc.Items)
                    items.Add(new JObject { ["name"] = it.Name, ["price"] = it.Price, ["bonus"] = it.Bonus });
                m["items"] = items;

                var layers = new JArray();
                foreach (var ly in mc.Layers)
                    layers.Add(new JObject { ["keyword"] = ly.Keyword, ["nickname"] = ly.Nickname });
                m["layers"] = layers;

                arr.Add(m);
            }
            return new JObject { ["maps"] = arr }.ToString(Formatting.None);
        }
    }
}