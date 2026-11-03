using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
using Drova_Modding_API.Systems.Coop;
using Drova_Modding_API.Systems.Spawning;
using Drova_Modding_API.Systems.Spawning.Templates;
using Il2CppDrova;
using Il2CppDrova.Items;
using Il2CppDrova.StatusEffects;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;
using Il2CppCollections = Il2CppSystem.Collections.Generic;
using Random = UnityEngine.Random;

namespace TwitchMod.Interactions
{
    /// <summary>
    /// What a channel point reward does to the game. Every method returns false when it could not run, so the
    /// redemption gets refunded.
    /// </summary>
    internal static class RewardEffects
    {
        private const int MaxBandits = 5;
        private const float BanditSpread = 30f;
        private const int BleedShare = 4;
        private const int FoodGroup = 0;
        private const float FoodReplaceChance = 0.5f;

        /// <summary>
        /// Recipes, permanent stat items and the respec potion would skip progression. The rest matches the
        /// randomizer's exclusions.
        /// </summary>
        private static readonly string[] ExcludedItemIds =
            ["recipe_", "perma", "respec", "test", "template", "kai_", "_mock", "_bady_", "improvisedpickaxe", "kiepe"];
        private static readonly Vector2 MonsterRange = new(150f, 300f);
        private static readonly Vector2 BanditRange = new(260f, 420f);
        private static readonly ActorWorldLocator Locator = new();

        /// <summary>
        /// Hostile, non-boss creatures by the lowest player level they suit. Prefab levels are all 1, so the tiers
        /// follow each prefab's health and experience.
        /// </summary>
        private static readonly (int Level, string[] Creatures)[] MonsterTiers =
        [
            (0, [AddressableAccess.Creatures.WaterRat.AssetGUID, AddressableAccess.Creatures.Sprigga_Young_Normal.AssetGUID,
                AddressableAccess.Creatures.HungryRipper.AssetGUID, AddressableAccess.Creatures.Spitter_ExtraWeak.AssetGUID,
                AddressableAccess.Creatures.Snapper_ExtraWeak.AssetGUID]),
            (5, [AddressableAccess.Creatures.Frog_Melee.AssetGUID, AddressableAccess.Creatures.Frog_Ranged.AssetGUID,
                AddressableAccess.Creatures.Ripper.AssetGUID, AddressableAccess.Creatures.Spitter_Weak.AssetGUID,
                AddressableAccess.Creatures.Snapper_Weak.AssetGUID]),
            (8, [AddressableAccess.Creatures.Boar.AssetGUID, AddressableAccess.Creatures.Sprigga.AssetGUID,
                AddressableAccess.Creatures.Spitter.AssetGUID, AddressableAccess.Creatures.Snapper.AssetGUID]),
            (12, [AddressableAccess.Creatures.ShadowRipper.AssetGUID, AddressableAccess.Creatures.Boar_Luminous.AssetGUID,
                AddressableAccess.Creatures.EliteRipper.AssetGUID, AddressableAccess.Creatures.StoneFist.AssetGUID]),
            (18, [AddressableAccess.Creatures.Bear.AssetGUID, AddressableAccess.Creatures.LightBear.AssetGUID]),
        ];

        /// <summary>
        /// Food, potion and negative effects. The reward picks a group first, so the few negative ones are not rare.
        /// The item gives the HUD tooltip its text.
        /// </summary>
        private static readonly StatusEffectChoice[][] StatusEffectPool =
        [
            [
                new("Meal", StatusEffectGuids.Consumable.MealT1),
                new("RoastedMeat", StatusEffectGuids.Buff.RoastedMeatSaturated, "item_roastedMeat"),
                new("Rootstew", StatusEffectGuids.Consumable.Rootstew),
                new("Fishpan", StatusEffectGuids.Consumable.Fishpan),
                new("Agilostew", StatusEffectGuids.Consumable.Agilostew),
                new("FarmersFood", StatusEffectGuids.Buff.FarmersFoodSaturated),
            ],
            [
                new("HealingSalve", StatusEffectGuids.Buff.HealingSalveSaturated, "item_salve_healing"),
                new("DarkBrew", StatusEffectGuids.Consumable.DarkBrewHealing, "consumable_potion_darkbrew_healing"),
                new("Blossom", StatusEffectGuids.Buff.BlossomSaturated, "item_healthPlant_2"),
                new("FlowPotion", StatusEffectGuids.Buff.WeakSourcePotionDurationEffect, "item_potion_flow_0"),
                new("StrongFlowPotion", StatusEffectGuids.Buff.StrongSourcePotionDurationEffect, "item_potion_flow_1"),
                new("Windwalker", StatusEffectGuids.Consumable.PotionWindwalker, "item_potion_windwalker"),
                new("Golemsalve", StatusEffectGuids.Consumable.Golemsalve, "item_golemsalve"),
                new("VoidEssence", StatusEffectGuids.Buff.VoidEssence, "item_essence_void"),
            ],
            [
                new("Bleeding", string.Empty),
                new("Poison", StatusEffectGuids.Other.Poison, Seconds: 30f),
                new("Drunk", StatusEffectGuids.Other.Intoxication, Stacks: 8),
            ],
        ];

        private static List<Item>? _itemPool;

#if DEBUG
        /// <summary>
        /// Addressable guid the monster reward spawns instead of a random pick, so tests know what comes.
        /// </summary>
        public static string? ForcedMonster;
#endif

        /// <summary>
        /// Gives one random weapon, armor or consumable of at most rare rarity.
        /// </summary>
        public static bool GiveRandomItem(Actor player)
        {
            List<Item>? pool = GetItemPool();
            if (pool == null || pool.Count == 0)
                return false;

            return AddToInventory(player, pool[Random.Range(0, pool.Count)], 1);
        }

        /// <summary>
        /// Gives <paramref name="amount"/> of the item with the given readable id.
        /// </summary>
        public static bool GiveItem(Actor player, string readableId, int amount)
        {
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null)
                return false;

            Item item = database.GetItemByReadableId(readableId);
            return item != null && AddToInventory(player, item, amount);
        }

        /// <summary>
        /// Spawns one hostile creature near <paramref name="origin"/>, picked by the requesting player's level.
        /// </summary>
        public static bool SpawnMonster(Vector2 origin, int level)
        {
            if (!CoopAccess.ShouldRun(CoopBehavior.AuthorityGated))
                return false;

            // The player's tier and the one below, so a high level never meets a 24 HP rat.
            int tier = Array.FindLastIndex(MonsterTiers, candidate => level >= candidate.Level);
            string[] creatures = MonsterTiers[Math.Max(0, tier - 1)..(tier + 1)].SelectMany(candidate => candidate.Creatures).ToArray();
            string creature = creatures[Random.Range(0, creatures.Length)];
#if DEBUG
            creature = ForcedMonster ?? creature;
#endif

            Vector2? position = FindSpawnPosition(origin, MonsterRange);
            return position != null && ActorSpawnAccess.TrySpawnFromAsset(creature, position.Value, out _);
        }

        /// <summary>
        /// Spawns a group of hostile bandits near <paramref name="origin"/>, scaled by the requesting player's level.
        /// </summary>
        public static bool SpawnBandits(Vector2 origin, int level)
        {
            if (!CoopAccess.ShouldRun(CoopBehavior.AuthorityGated))
                return false;

            Vector2? anchor = FindSpawnPosition(origin, BanditRange);
            if (anchor == null)
                return false;

            BanditDifficulty difficulty = level < 8 ? BanditDifficulty.Easy : level < 20 ? BanditDifficulty.Normal : BanditDifficulty.Hard;
            int count = Math.Min(2 + level / 10, MaxBandits);
            List<LazyActor> bandits = [];
            for (int i = 0; i < count; i++)
            {
                Vector2 position = anchor.Value + Random.insideUnitCircle * BanditSpread;
                bandits.Add(BanditCreator.CreateRandomBanditLazy("Bandit", position, difficulty));
            }
            EnemyAccess.MakeAggressiveGroup(bandits, BanditRange.y + BanditSpread);
            return true;
        }

        /// <summary>
        /// Gives a random food, potion or negative status effect.
        /// </summary>
        public static bool GiveRandomStatusEffect(Actor player)
        {
            int index = Random.Range(0, StatusEffectPool.Length);
            // A new food replaces the active one, so that happens only half the time.
            if (index == FoodGroup && HasFood(player) && Random.value >= FoodReplaceChance)
                index = Random.Range(FoodGroup + 1, StatusEffectPool.Length);

            StatusEffectChoice[] group = StatusEffectPool[index];
            return GiveStatusEffect(player, group[Random.Range(0, group.Length)]);
        }

        private static bool HasFood(Actor player)
        {
            StatusEffectHandler handler = player.GetStatusEffectHandler();
            if (handler == null)
                return false;

            Il2CppCollections.List<StatusEffect> active = new();
            handler.GetAllGuiEffects(active);
            foreach (StatusEffect effect in active)
            {
                if (effect.IsFoodBuff())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gives the status effect of the pool with the given key, for tests.
        /// </summary>
        public static bool GiveStatusEffect(Actor player, string key)
        {
            StatusEffectChoice? choice = StatusEffectPool.SelectMany(group => group).FirstOrDefault(candidate => candidate.Key == key);
            return choice != null && GiveStatusEffect(player, choice);
        }

        private static bool GiveStatusEffect(Actor player, StatusEffectChoice choice)
        {
            MelonLogger.Msg("[Twitch] Status effect: " + choice.Key);
            if (choice.Key == "Bleeding")
            {
                Health health = player.GetHealth();
                return health != null && StatusEffectAccess.TryApplyBleeding(player, Math.Max(1, health.MaxHealth / BleedShare));
            }

            if (!StatusEffectAccess.TryGet(choice.Guid, out StatusEffect? effect))
                return false;

            SubDatabase_Item? items = ProviderAccess.ItemDatabase;
            Item? item = choice.ItemId != null && items != null ? items.GetItemByReadableId(choice.ItemId) : null;
            return StatusEffectAccess.TryApply(player, effect, item, choice.Stacks, choice.Seconds);
        }

        private static bool AddToInventory(Actor player, Item item, int amount)
        {
            Il2CppDrova.InventorySystem.Inventory inventory = player.GetInventory();
            if (inventory == null)
                return false;

            inventory.AddItem(new ItemStack(item, amount), true);
            return true;
        }

        /// <summary>
        /// The items <see cref="GiveRandomItem"/> draws from, or null before the item database is loaded.
        /// </summary>
        public static List<Item>? GetItemPool()
        {
            if (_itemPool != null)
                return _itemPool;

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null)
                return null;

            _itemPool = [];
            foreach (SubDatabase_Item.Entry entry in database._entries)
            {
                Item item = entry.Item;
                if (item == null || item.Category == null || string.IsNullOrEmpty(item.ReadableId))
                    continue;

                string id = item.ReadableId.ToLowerInvariant();
                if (item.Category.MainCategory is ItemMainCategory.Weapon or ItemMainCategory.Armor or ItemMainCategory.Consumable
                    && item.BuyValue > 0 && item.SellValue > 0
                    && !item.IsQuestItem && !item.IsInQuestCategory
                    && item.Rarity <= Item.ItemRarity.Rare
                    && !ExcludedItemIds.Any(id.Contains))
                    _itemPool.Add(item);
            }
            return _itemPool;
        }

        private sealed record StatusEffectChoice(string Key, string Guid, string? ItemId = null, int Stacks = 0, float Seconds = 0f);

        private static Vector2? FindSpawnPosition(Vector2 origin, Vector2 range)
        {
            Locator.SetMinMaxRange(range);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector2? position = Locator.GetRandomFreePosition(origin);
                if (position != null && position.Value != Vector2.zero)
                    return position;
            }
            return null;
        }
    }
}
