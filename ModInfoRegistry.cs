using KitchenData;
using KitchenMods;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Entities;

namespace ModContentCache
{
    public static class ModInfoRegistry
    {
        private const string PRELOAD_FILE_NAME = "ModInfoRegistry_Preload.json";
        private const string SAVE_FILE_NAME = "ModInfoRegistry.json";

        private static Dictionary<ulong, ModInfo> _modInfos = new Dictionary<ulong, ModInfo>();
        private static Dictionary<ulong, ModInfo> ModInfos
        {
            get
            {
                if (_modInfos == null)
                {
                    Initialise();
                }
                return _modInfos;
            }
        }
        public struct ModMetadata
        {
            public string Name;

            public ulong ID;
        }

        private class ModInfo
        {
            public string Name;
            public ulong ID;

            [JsonProperty]
            private Dictionary<ulong, string> UnityTypeHashes;

            [JsonIgnore]
            public int UnityTypeHashCount => UnityTypeHashes?.Count ?? 0;

            [JsonIgnore]
            public bool HasContent => UnityTypeHashes.Any();

            public ModInfo()
            {
                Name = "Unknown";
                ID = 0;
                UnityTypeHashes = new Dictionary<ulong, string>();
            }

            public ModInfo(string name, ulong id)
            {
                Name = name;
                ID = id;
                UnityTypeHashes = new Dictionary<ulong, string>();
            }

            public ModInfo(Mod mod)
            {
                Name = mod.Name;
                ID = mod.ID;
                UnityTypeHashes = new Dictionary<ulong, string>();

                IEnumerable<Type> types = mod.GetPacks<AssemblyModPack>().SelectMany(x => x.Asm.GetTypes());
                try
                {
                    UnityTypeHashes = TypeManager.GetAllTypes()
                        .Where(x => types.Contains(x.Type))
                        .ToDictionary(x => x.StableTypeHash, x => x.Type.Name);
                }
                catch (Exception ex)
                {
                    Main.LogError($"Failed to populate UnityTypeHashes for {Name ?? "Unknown"} ({ID})\n{ex.Message}\n{ex.StackTrace}");
                }
            }

            public static ModInfo FromMod(Mod mod)
            {
                return new ModInfo(mod);
            }

            public ModInfo MergeWith(ModInfo other)
            {
                foreach (KeyValuePair<ulong, string> kvp in other.UnityTypeHashes)
                    UnityTypeHashes[kvp.Key] = kvp.Value;

                return this;
            }

            public bool IsUnityTypeSource(ulong stableTypeHash)
            {
                return UnityTypeHashes?.ContainsKey(stableTypeHash) ?? false;
            }

            public bool IsUnityTypeSource(IEnumerable<ulong> stableTypeHashes)
            {
                return UnityTypeHashes?.Select(x => x.Key).Intersect(stableTypeHashes).Any() ?? false;
            }
        }

        internal static void Initialise()
        {
            _modInfos.Clear();

            if (!Directory.Exists(Main.FolderPath))
                Directory.CreateDirectory(Main.FolderPath);

            void AddToModInfos(IEnumerable<ModInfo> modInfosToAdd)
            {
                foreach (ModInfo modInfo in modInfosToAdd)
                {
                    if (!_modInfos.TryGetValue(modInfo.ID, out ModInfo existingModInfo))
                    {
                        _modInfos[modInfo.ID] = modInfo;
                        continue;
                    }
                    existingModInfo.MergeWith(modInfo);
                }
            }

            bool TryLoadFromFile(string filename, out Dictionary<ulong, ModInfo> modInfos)
            {
                string filepath = Path.Combine(Main.FolderPath, filename);
                if (File.Exists(filepath))
                {
                    try
                    {
                        modInfos = Deserialise(File.ReadAllText(filepath));
                        Main.LogInfo($"Load from {filepath}.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Main.LogError($"Failed to load {filepath}\n{ex.Message}\n{ex.StackTrace}");
                    }
                }
                else
                {
                    Main.LogWarning($"Skipping load from {filepath}. File does not exist.");
                }
                modInfos = default;
                return false;
            }

            Dictionary<ulong, ModInfo> Deserialise(string data)
            {
                return JsonConvert.DeserializeObject<Dictionary<ulong, ModInfo>>(data);
            }

            bool TryReadFromPreload(out Dictionary<ulong, ModInfo> modInfos)
            {
                try
                {
                    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ModContentCache.Preloads.{PRELOAD_FILE_NAME}");
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        modInfos = Deserialise(reader.ReadToEnd());
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Main.LogError($"{ex.Message}\n{ex.StackTrace}");
                    modInfos = default;
                    return false;
                }
            }

            AddToModInfos(ModPreload.Mods
                .Where(mod => mod.ID != 0)
                .Select(ModInfo.FromMod));

            if (TryReadFromPreload(out var preloadModInfos))
            {
                AddToModInfos(preloadModInfos.Values);
            }

            foreach (string filename in new string[] { SAVE_FILE_NAME })
            {
                if (TryLoadFromFile(filename, out var modInfos))
                    AddToModInfos(modInfos.Values);
            }

            File.WriteAllText(Path.Combine(Main.FolderPath, SAVE_FILE_NAME), JsonConvert.SerializeObject(_modInfos.Where(kvp => kvp.Value.HasContent).ToDictionary(kvp => kvp.Key, kvp => kvp.Value), Formatting.Indented));
        }

        public static bool FindUnityTypeHashSource(ulong hash, out ModMetadata modMetadata)
        {
            modMetadata = default;
            var foundModInfos = ModInfos.Select(x => x.Value).Where(modInfo => modInfo.IsUnityTypeSource(hash));
            if (!foundModInfos.Any())
                return false;

            ModInfo matchingModInfo = foundModInfos.First();
            modMetadata = new ModMetadata()
            {
                Name = matchingModInfo.Name,
                ID = matchingModInfo.ID
            };
            return true;
        }

        public static bool FindUnityTypeHashSource(IEnumerable<ulong> hashes, out IEnumerable<ModMetadata> modMetadatas)
        {
            modMetadatas = default;
            if (!hashes.Any())
            {
                Main.LogWarning("\tNo hashes");
                return false;
            }

            var foundModInfos = ModInfos.Select(x => x.Value).Where(modInfo => modInfo.IsUnityTypeSource(hashes));
            if (!foundModInfos.Any())
            {
                Main.LogWarning("\tNo mod infos found");
                return false;
            }

            modMetadatas = foundModInfos.Select(modInfo => new ModMetadata()
            {
                Name = modInfo.Name,
                ID = modInfo.ID
            });
            return true;
        }

        public static string GetModName(ulong modID)
        {
            return ModInfos.TryGetValue(modID, out ModInfo modInfo) ? modInfo.Name : $"Unknown ({modID})";
        }
    }
}
