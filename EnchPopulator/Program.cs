using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.FormKeys.SkyrimSE;

namespace EnchPopulator
{
    public class Settings {
        public List<ModKey> SourceMods = new() { ModKey.FromFileName("Skyrim.esm"), ModKey.FromFileName("Dragonborn.esm"), ModKey.FromFileName("Thaumaturgy.esp") };
        public Dictionary<string, List<string>> ItemsToPatch { get; set; } = new();
        public bool SkipBethItems = true;
        public bool AddSubLists = false;
    }

    public class Program
    {
        private static Lazy<Settings>? settings;
        private static List<ModKey> BethesdaSources = new() { ModKey.FromFileName("Skyrim.esm"), ModKey.FromFileName("Dragonborn.esm"), ModKey.FromFileName("Dawnguard.esm"), ModKey.FromFileName("HearthFires.esm") };
        private static Dictionary<FormKey, ILeveledItem> subLists = new();

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetAutogeneratedSettings("settings", "settings.json", out settings)
                .SetTypicalOpen(GameRelease.SkyrimSE, "EnchPopulator.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var settingsValue = settings?.Value ?? throw new Exception();
            var coreCache = settingsValue.SourceMods.Select(x => state.LoadOrder.GetIfEnabled(x)).ToImmutableLinkCache();

			var itemListsToPatch = settingsValue.ItemsToPatch
                .ToDictionary(x => state.LinkCache.Resolve<ISkyrimMajorRecordGetter>(x.Key),
                              x => x.Value.Select(y => state.LinkCache.Resolve<ILeveledItemGetter>(y).ToLink()).ToList());

            foreach (var (baseListOrItem, itemLists) in itemListsToPatch) {
                ProcessItems(state, coreCache, baseListOrItem, itemLists);
            }
        }

        public static void ProcessItems(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ILinkCache<ISkyrimMod, ISkyrimModGetter> coreCache, ISkyrimMajorRecordGetter baseListOrItem, List<IFormLink<ILeveledItemGetter>> itemLists) {
            var settingsValue = settings?.Value ?? throw new Exception();

            switch (baseListOrItem) {
                case ILeveledItemGetter baseList:
                    foreach (var entry in baseList.Entries!) {
                    	if (entry!.Data!.Reference!.TryResolve(state.LinkCache, out var baseItem)) {
                            if (settingsValue.SkipBethItems && BethesdaSources.Contains(baseItem.FormKey.ModKey)) continue;
                        	ProcessItems(state, coreCache, baseItem, itemLists);
                        }
                    }
                    break;
                case IItemGetter baseItem:
                    foreach (var itemList in itemLists)
                        if (itemList.TryResolve(coreCache, out var thaumList))
                            ProcessList(state, baseItem, thaumList, coreCache, settingsValue.AddSubLists);
                    break;
                default: break;
            }
        }

        public static void ProcessList(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, IItemGetter baseItem, ILeveledItemGetter list, ILinkCache<ISkyrimMod, ISkyrimModGetter> coreCache, bool useSubLists) {
            Console.WriteLine($"{list.EditorID}");
            foreach (var entry in list.Entries ?? throw new Exception()) {
                var curRef = entry.Data!.Reference;

                if (curRef.TryResolve(coreCache, out var source)) {
                    switch (source) {
                        case ILeveledItemGetter sublist:
                            ProcessList(state, baseItem, sublist, coreCache, useSubLists);
                            break;
                        case IWeaponGetter weaponRecord:
                            Console.WriteLine($"{weaponRecord.Name}");
                            if (list.ToLink().TryResolveContext<ISkyrimMod, ISkyrimModGetter, ILeveledItem, ILeveledItemGetter>(coreCache, out var weaponListContext)) {
                                if (baseItem is not IWeaponGetter baseWeapon) break;
                                if (!weaponRecord.ObjectEffect.TryResolve(state.LinkCache, out var effect)) break;

                                if (useSubLists) {
                                    if (!subLists.ContainsKey(baseWeapon.FormKey)) {
                                        subLists[baseWeapon.FormKey] = state.PatchMod.LeveledItems.AddNew();
                                        subLists[baseWeapon.FormKey].EditorID = $"{baseWeapon.EditorID}Sublist";
                                        subLists[baseWeapon.FormKey].Entries ??= new();
                                    }
                                }
                                
                                var localList = weaponListContext.GetOrAddAsOverride(state.PatchMod);
                                var eId = $"{baseWeapon.EditorID}{effect.EditorID}";

                                if (!state.LinkCache.TryResolve<IWeapon>(eId, out var newItem)) {
                                    newItem = state.PatchMod.Weapons.AddNew(eId);
                                    newItem.BasicStats ??= new();

                                    newItem.ObjectEffect.SetTo(weaponRecord.ObjectEffect);
                                    newItem.EnchantmentAmount = weaponRecord.EnchantmentAmount;
                                    newItem.Template.SetTo(baseWeapon);
                                    if (weaponRecord.Template.TryResolve(coreCache, out var templateRecord))
                                        newItem.Name = weaponRecord!.Name?.ToString()?.Replace($"{templateRecord.Name}", $"{baseWeapon.Name}") ?? string.Empty;
                                    newItem.BasicStats.Value = baseWeapon.BasicStats?.Value ?? 0;
                                }

                                if (useSubLists) {
                                    if (!subLists[baseWeapon.FormKey]?.Entries?.Any(x => x?.Data?.Reference.Equals(subLists[baseWeapon.FormKey]) ?? false) ?? false)
                                        subLists[baseWeapon.FormKey]?.Entries?.Add(new LeveledItemEntry
                                            {
                                                Data = new LeveledItemEntryData
                                                {
                                                    Count = entry.Data!.Count,
                                                    Level = entry.Data!.Level,
                                                    Reference = newItem.ToLink()
                                                }
                                            });

                                    if (!localList?.Entries?.Any(x => x?.Data?.Reference.Equals(subLists[baseWeapon.FormKey]) ?? false) ?? false)
                                        localList?.Entries?.Add(new LeveledItemEntry
                                        {
                                            Data = new LeveledItemEntryData
                                            {
                                                Count = 1,
                                                Level = 1,
                                                Reference = subLists[baseWeapon.FormKey].ToLink()
                                            }
                                        });
                                } else {
                                    if (!localList?.Entries?.Any(x => x?.Data?.Reference.Equals(newItem) ?? false) ?? false)
                                        localList?.Entries?.Add(new LeveledItemEntry
                                        {
                                            Data = new LeveledItemEntryData
                                            {
                                                Count = entry.Data!.Count,
                                                Level = entry.Data!.Level,
                                                Reference = newItem.ToLink()
                                            }
                                        });
                                }
                            }
                            break;
                        case IArmorGetter armorRecord:
                            Console.WriteLine($"{armorRecord.Name}");
                            if (list.ToLink().TryResolveContext<ISkyrimMod, ISkyrimModGetter, ILeveledItem, ILeveledItemGetter>(coreCache, out var armorListContext)) {
                                if (baseItem is not IArmorGetter baseArmor) break;
                                if (!armorRecord.ObjectEffect.TryResolve(state.LinkCache, out var effect)) break;
                                
                                var localList = armorListContext.GetOrAddAsOverride(state.PatchMod);
                                var eId = $"{baseArmor.EditorID}{effect.EditorID}";

                                if (useSubLists) {
                                    if (!subLists.ContainsKey(baseArmor.FormKey)) {
                                        subLists[baseArmor.FormKey] = state.PatchMod.LeveledItems.AddNew();
                                        subLists[baseArmor.FormKey].EditorID = $"{baseArmor.EditorID}Sublist";
                                        subLists[baseArmor.FormKey].Entries ??= new();
                                    }
                                }
                                
                                if (!state.LinkCache.TryResolve<IArmor>(eId, out var newItem)) {
                                    newItem = state.PatchMod.Armors.AddNew(eId);

                                    newItem.ObjectEffect.SetTo(armorRecord.ObjectEffect);
                                    newItem.TemplateArmor.SetTo(baseArmor);
                                    if (armorRecord.TemplateArmor.TryResolve(coreCache, out var templateRecord))
                                        newItem.Name = armorRecord!.Name?.ToString()?.Replace($"{templateRecord.Name}", $"{baseArmor.Name}") ?? string.Empty;
                                    newItem.Value = baseArmor.Value;
                                }

                                if (useSubLists) {
                                    if (!subLists[baseArmor.FormKey]?.Entries?.Any(x => x?.Data?.Reference.Equals(newItem) ?? false) ?? false)
                                        subLists[baseArmor.FormKey]?.Entries?.Add(new LeveledItemEntry
                                            {
                                                Data = new LeveledItemEntryData
                                                {
                                                    Count = entry.Data!.Count,
                                                    Level = entry.Data!.Level,
                                                    Reference = newItem.ToLink()
                                                }
                                            });

                                    if (!localList?.Entries?.Any(x => x?.Data?.Reference.Equals(subLists[baseArmor.FormKey]) ?? false) ?? false)
                                        localList?.Entries?.Add(new LeveledItemEntry
                                        {
                                            Data = new LeveledItemEntryData
                                            {
                                                Count = 1,
                                                Level = 1,
                                                Reference = subLists[baseArmor.FormKey].ToLink()
                                            }
                                        });
                                    } else {
                                        if (!localList?.Entries?.Any(x => x?.Data?.Reference.Equals(newItem) ?? false) ?? false)
                                            localList?.Entries?.Add(new LeveledItemEntry
                                            {
                                                Data = new LeveledItemEntryData
                                                {
                                                    Count = entry.Data!.Count,
                                                    Level = entry.Data!.Level,
                                                    Reference = newItem.ToLink()
                                                }
                                            });
                                    }
                            }
                            break;
                        default: break;
                    }
                }
            }
        }
    }
}
