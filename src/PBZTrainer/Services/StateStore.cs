using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PBZTrainer.Models;

namespace PBZTrainer.Services
{
    /// <summary>
    /// 状态持久化：完全沿用原版 trainer_state.json 的字段结构
    /// （SelectedPage / AutoSave / OnlyWhenTargetOrPanelForeground / PanelToggleHotkey / Features{Enabled,Value,Hotkey}）。
    /// </summary>
    /// <summary>单个怪物的一键生成热键绑定（每个怪物各自一套参数）。</summary>
    public sealed class SpawnBinding
    {
        public string Hotkey { get; set; } = "";
        public string Name { get; set; } = "";
        public int Count { get; set; } = 1;
        public int Distance { get; set; } = 800;
        public bool Friendly { get; set; }
    }

    public sealed class StateStore
    {
        public string Directory { get; }
        public string StatePath => Path.Combine(Directory, "trainer_state.json");
        public string PrefsPath => Path.Combine(Directory, "preferences.json");

        public string SelectedPage { get; set; } = "基础功能";
        public bool AutoSave { get; set; } = true;
        public bool OnlyWhenTargetOrPanelForeground { get; set; } = true;
        /// <summary>启动时是否套用上次的开关与数值（默认开）。</summary>
        public bool RestoreSettings { get; set; } = true;
        public string PanelToggleHotkey { get; set; } = "F1";

        /// <summary>每个怪物各自的一键生成热键：怪物 id → 绑定（可同时绑多个）。</summary>
        public Dictionary<string, SpawnBinding> SpawnBindings { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>旧版单条绑定，仅用于迁移到 SpawnBindings。</summary>
        public string SpawnHotkey { get; set; } = "";
        public string SpawnHotkeyId { get; set; } = "";
        public string SpawnHotkeyName { get; set; } = "";
        public int SpawnHotkeyCount { get; set; } = 1;
        public int SpawnHotkeyDistance { get; set; } = 6;
        public bool SpawnHotkeyFriendly { get; set; }

        public string Theme { get; set; } = "雾紫晴岚";
        public List<string> Favorites { get; } = new();
        public bool WriteFailed { get; private set; }
        private readonly List<string> _recoveryNotes = new();
        private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
        public string RecoveryMessage => string.Join("\n", _recoveryNotes);

        public StateStore(string dir)
        {
            Directory = dir;
            System.IO.Directory.CreateDirectory(dir);
        }

        public void Load(IEnumerable<FeatureVm> items)
        {
            var map = new Dictionary<string, FeatureVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items) map[it.Id] = it;

            if (ReadWithBackup(StatePath) is JsonObject root)
            {
                SelectedPage = Str(root["SelectedPage"]) ?? SelectedPage;
                AutoSave = Bool(root["AutoSave"], AutoSave);
                OnlyWhenTargetOrPanelForeground = Bool(root["OnlyWhenTargetOrPanelForeground"], OnlyWhenTargetOrPanelForeground);
                PanelToggleHotkey = Str(root["PanelToggleHotkey"]) ?? PanelToggleHotkey;
                SpawnHotkey = Str(root["SpawnHotkey"]) ?? "";
                SpawnHotkeyId = Str(root["SpawnHotkeyId"]) ?? "";
                SpawnHotkeyName = Str(root["SpawnHotkeyName"]) ?? "";
                SpawnHotkeyCount = Int(root["SpawnHotkeyCount"], 1);
                SpawnHotkeyDistance = Int(root["SpawnHotkeyDistance"], 6);
                SpawnHotkeyFriendly = Bool(root["SpawnHotkeyFriendly"], false);

                SpawnBindings.Clear();
                if (root["SpawnBindings"] is JsonObject binds)
                {
                    foreach (var kv in binds)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is not JsonObject o) continue;
                        SpawnBindings[kv.Key] = new SpawnBinding
                        {
                            Hotkey = Str(o["Hotkey"]) ?? "",
                            Name = Str(o["Name"]) ?? "",
                            Count = Int(o["Count"], 1),
                            Distance = Int(o["Distance"], 800),
                            Friendly = Bool(o["Friendly"], false)
                        };
                    }
                }

                // 旧版单条绑定迁移成一条记录
                if (!string.IsNullOrEmpty(SpawnHotkey) && !string.IsNullOrEmpty(SpawnHotkeyId)
                    && !SpawnBindings.ContainsKey(SpawnHotkeyId))
                {
                    SpawnBindings[SpawnHotkeyId] = new SpawnBinding
                    {
                        Hotkey = SpawnHotkey,
                        Name = SpawnHotkeyName,
                        Count = SpawnHotkeyCount <= 0 ? 1 : SpawnHotkeyCount,
                        Distance = SpawnHotkeyDistance <= 0 ? 800 : SpawnHotkeyDistance,
                        Friendly = SpawnHotkeyFriendly
                    };
                }
                RestoreSettings = Bool(root["RestoreSettings"], true);
                if (root["Features"] is JsonObject feats)
                {
                    foreach (var kv in feats)
                    {
                        if (!map.TryGetValue(kv.Key, out var vm)) continue;
                        if (kv.Value is not JsonObject o) continue;
                        vm.Hotkey = Str(o["Hotkey"]) ?? "";
                        if (!RestoreSettings) continue;
                        if (vm.HasToggle && o["Enabled"] is JsonValue flag && flag.TryGetValue<bool>(out bool enabled))
                            vm.DesiredEnabled = enabled;
                        string val = Str(o["Value"]);
                        if (!string.IsNullOrEmpty(val)) vm.ValueText = val;
                    }
                }
            }

            if (ReadWithBackup(PrefsPath) is JsonObject p)
            {
                Theme = Str(p["Theme"]) ?? Theme;
                if (p["Favorites"] is JsonArray arr)
                {
                    Favorites.Clear();
                    foreach (var n in arr) if (Str(n) is string s) Favorites.Add(s);
                }
            }

            foreach (var f in Favorites)
                if (map.TryGetValue(f, out var vm)) vm.IsFavorite = true;
        }

        public string FindHotkeyOwner(string combo, IEnumerable<FeatureVm> features,
            bool exceptPanel = false, FeatureVm exceptFeature = null, string exceptSpawnId = null)
        {
            if (string.IsNullOrWhiteSpace(combo)) return null;
            string normalized = HotkeyService.Normalize(combo);
            bool Matches(string key) => !string.IsNullOrWhiteSpace(key) &&
                string.Equals(HotkeyService.Normalize(key), normalized, StringComparison.OrdinalIgnoreCase);

            if (!exceptPanel && Matches(PanelToggleHotkey)) return "唤出 / 隐藏修改器";
            foreach (var feature in features)
                if (feature != exceptFeature && Matches(feature.Hotkey)) return feature.Title;
            foreach (var (id, binding) in SpawnBindings)
            {
                if (string.Equals(id, exceptSpawnId, StringComparison.OrdinalIgnoreCase) || !Matches(binding?.Hotkey)) continue;
                return "生成怪物 · " + (string.IsNullOrWhiteSpace(binding.Name) ? id : binding.Name);
            }
            return null;
        }

        public void Save(IEnumerable<FeatureVm> items)
        {
            try
            {
                var feats = new JsonObject();
                foreach (var vm in items)
                {
                    feats[vm.Id] = new JsonObject
                    {
                        ["Enabled"] = vm.HasToggle ? vm.DesiredEnabled : null,
                        ["Value"] = vm.ValueText ?? "",
                        ["Hotkey"] = vm.Hotkey ?? ""
                    };
                }

                var spawnBinds = new JsonObject();
                foreach (var kv in SpawnBindings)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrEmpty(kv.Value?.Hotkey)) continue;
                    spawnBinds[kv.Key] = new JsonObject
                    {
                        ["Hotkey"] = kv.Value.Hotkey,
                        ["Name"] = kv.Value.Name ?? "",
                        ["Count"] = kv.Value.Count,
                        ["Distance"] = kv.Value.Distance,
                        ["Friendly"] = kv.Value.Friendly
                    };
                }

                var root = new JsonObject
                {
                    ["SelectedPage"] = SelectedPage,
                    ["AutoSave"] = AutoSave,
                    ["OnlyWhenTargetOrPanelForeground"] = OnlyWhenTargetOrPanelForeground,
                    ["PanelToggleHotkey"] = PanelToggleHotkey,
                    ["SpawnBindings"] = spawnBinds,
                    ["RestoreSettings"] = RestoreSettings,
                    ["Features"] = feats
                };

                var opt = new JsonSerializerOptions { WriteIndented = true };
                WriteAtomic(StatePath, root.ToJsonString(opt));

                var favs = new JsonArray();
                foreach (var vm in items) if (vm.IsFavorite) favs.Add(vm.Id);
                var prefs = new JsonObject
                {
                    ["Theme"] = Theme,
                    ["Favorites"] = favs
                };
                WriteAtomic(PrefsPath, prefs.ToJsonString(opt));
                WriteFailed = false;
            }
            catch
            {
                WriteFailed = true;
            }
        }

        private JsonObject ReadWithBackup(string path)
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    var document = ReadDocument(candidate);
                    if (candidate != path) _recoveryNotes.Add("已从备份恢复 " + Path.GetFileName(path));
                    return document;
                }
                catch (FileNotFoundException) { }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    // 保留原件，等下一次成功的原子替换将其移到单独的恢复文件。
                    _recoveryNotes.Add(Path.GetFileName(candidate) + " 已损坏，原文件将保留");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _unreadable.Add(path);
                    _recoveryNotes.Add(Path.GetFileName(candidate) + " 无法读取，本次保留磁盘配置");
                }
            }
            return null;
        }

        private static JsonObject ReadDocument(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new JsonException("配置根节点应为对象");
            // JsonObject 延迟解析键；在采用文件前确认所有对象均可枚举。
            static void Validate(JsonNode node)
            {
                if (node is JsonObject obj) foreach (var entry in obj) Validate(entry.Value);
                else if (node is JsonArray array) foreach (var item in array) Validate(item);
            }
            Validate(root);
            return root;
        }

        private void WriteAtomic(string path, string text)
        {
            if (_unreadable.Contains(path)) throw new IOException("配置读取失败，保留磁盘文件");
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
                    stream.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return;
                }

                string backup = path + ".bak";
                try { ReadDocument(path); }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    backup = path + ".corrupt-" + Guid.NewGuid().ToString("N");
                }
                File.Replace(tmp, path, backup);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        private static string Str(JsonNode n)
        {
            if (n == null) return null;
            try { return n.GetValue<string>(); } catch { return null; }
        }

        private static int Int(JsonNode n, int def)
        {
            if (n == null) return def;
            try { return n.GetValue<int>(); } catch { }
            try { return (int)n.GetValue<double>(); } catch { return def; }
        }

        private static bool Bool(JsonNode n, bool def)
        {
            if (n == null) return def;
            try { return n.GetValue<bool>(); } catch { return def; }
        }
    }
}
