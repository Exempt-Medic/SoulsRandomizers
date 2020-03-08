﻿using SoulsFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using static RandomizerCommon.Events;
using static RandomizerCommon.LocationData;
using static RandomizerCommon.LocationData.ItemScope;
using static RandomizerCommon.LocationData.LocationKey;
using static RandomizerCommon.Permutation;
using static RandomizerCommon.Util;

namespace RandomizerCommon
{
    public class PermutationWriter
    {
        private static List<string> itemValueCells = new List<string> { "shopPrice", "Unk9", "Costvalue", "shopId" };

        private GameData game;
        private LocationData data;
        private AnnotationData ann;
        private Events events;
        private PARAM itemLots;
        private PARAM shops;
        private PARAM npcs;

        private readonly Dictionary<ItemKey, bool> finiteShopClassification = new Dictionary<ItemKey, bool>();
        private readonly Dictionary<PriceCategory, List<int>> prices = new Dictionary<PriceCategory, List<int>>();
        private readonly Dictionary<ItemKey, float> dropCost = new Dictionary<ItemKey, float>();
        private readonly Dictionary<int, int> lotCost = new Dictionary<int, int>();

        public PermutationWriter(GameData game, LocationData data, AnnotationData ann, Events events)
        {
            this.game = game;
            this.data = data;
            this.ann = ann;
            this.events = events;
            itemLots = game.Param("ItemLotParam");
            shops = game.Param("ShopLineupParam");
            npcs = game.Param("NpcParam");
        }

        public enum PriceCategory
        {
            // First three should match ItemType ordering. Non-goods are very broad, to give the chance for some really good deals.
            WEAPON, ARMOR, RING,
            // The rest are mainly goods
            SPELLS, FINITE_GOOD, INFINITE_GOOD, UPGRADE, TRANSPOSE,
            // Some Sekiro categories
            REGULAR_GOOD, UNIQUE_GOOD,
        }

        public void Write(Random random, Permutation permutation, RandomizerOptions opt)
        {
            foreach (string hintType in ann.HintCategories)
            {
                Console.WriteLine($"-- Hints for {hintType}:");
                foreach (KeyValuePair<SlotKey, SlotKey> assign in permutation.Hints[hintType].OrderBy(e => (game.Name(e.Key.Item), permutation.GetLogOrder(e.Value))))
                {
                    LocationScope scope = data.Location(assign.Value).LocScope;
                    Console.WriteLine($"{game.Name(assign.Key.Item)}: {ann.GetLocationHint(assign.Value, permutation.SpecialLocation(scope))}");
                }
                Console.WriteLine();
            }
            Console.WriteLine("-- End of hints");

            // Gather all potential prices to select from
            // TODO: More model stuff for Sekiro
            if (game.Sekiro)
            {
                foreach (KeyValuePair<ItemKey, ItemLocations> entry in data.Data)
                {
                    ItemKey item = entry.Key;
                    if (new[] { "Bulging Coin Purse", "Heavy Coin Purse", "Light Coin Purse", "Dragon's Blood Droplet" }.Contains(game.Name(item))) continue;
                    PARAM.Row row = game.Item(item);
                    foreach (ItemLocation itemLoc in entry.Value.Locations.Values)
                    {
                        foreach (LocationKey loc in itemLoc.Keys.Where(k => k.Type == LocationType.SHOP))
                        {
                            // No Promissory note
                            if (loc.ID % 100 >= 50) continue;
                            // No Offering box
                            if (loc.ID / 100 == 11005) continue;
                            PARAM.Row shop = shops[loc.ID];
                            int shopPrice = (int)shop["value"].Value;
                            if (shopPrice <= 0) continue;
                            AddMulti(prices, GetSekiroPriceCategory(item), shopPrice);
                            // Console.WriteLine($"Category for {game.Name(item)}: {shopPrice} / {GetSekiroPriceCategory(item)}");
                        }
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<ItemKey, ItemLocations> entry in data.Data)
                {
                    ItemKey item = entry.Key;
                    PARAM.Row row = game.Item(item);
                    int price = (int)row[itemValueCells[(int)item.Type]].Value;
                    int sellPrice = (int)row["sellValue"].Value;
                    foreach (ItemLocation itemLoc in entry.Value.Locations.Values)
                    {
                        bool material = itemLoc.Scope.Type == ScopeType.MATERIAL;
                        foreach (LocationKey loc in itemLoc.Keys.Where(k => k.Type == LocationType.SHOP))
                        {
                            PARAM.Row shop = shops[loc.ID];
                            int shopPrice = (int)shop["value"].Value;
                            if (price == -1 && shopPrice == -1) continue;
                            PriceCategory cat = GetPriceCategory(item, material);
                            // Don't price regular items toooo high - looking at you, 20k for Tower Key. Key items are priced separately anyway
                            if (cat == PriceCategory.FINITE_GOOD && price > 10000) continue;
                            AddMulti(prices, cat, shopPrice == -1 ? price : shopPrice);
                        }
                    }
                }
                // Gather 'costs' per item based on existing drop percentages, and the difficulty of enemies which drop them
                // Enemy difficulty is calculated as 2 * (HP of enemy) + (# of souls acquired after enemy is killed).
                // TODO: Is this really worth it? Doesn't matter as much with enemy randomizer anyway.
                float itemCost = 500f;
                HashSet<ScopeType> infiniteScopes = new HashSet<ScopeType> { ScopeType.MODEL, ScopeType.SHOP_INFINITE, ScopeType.SHOP_INFINITE_EVENT };
                foreach (KeyValuePair<ItemKey, ItemLocations> entry in data.Data)
                {
                    ItemKey item = entry.Key;
                    ItemLocations locs = entry.Value;
                    if (!locs.Locations.Keys.Any(loc => infiniteScopes.Contains(loc.Type))) continue;
                    double percentages = 0;
                    int costs = 0;
                    foreach (ItemLocation itemLoc in entry.Value.Locations.Values.Where(loc => loc.Scope.Type == ScopeType.MODEL))
                    {
                        double modelPercentages = 0;
                        int modelCosts = 0;
                        int count = 0;
                        foreach (LocationKey loc in itemLoc.Keys.Where(k => k.Type == LocationType.LOT))
                        {
                            SortedDictionary<int, string> npcInfo = new SortedDictionary<int, string>();
                            double locPercentages = 0;
                            int subcount = 0;
                            if (!lotCost.ContainsKey(loc.BaseID))
                            {
                                int locCosts = 0;
                                foreach (EntityId id in loc.Entities.Where(e => e.NPCParamID != -1))
                                {
                                    int npcId = id.NPCParamID;
                                    PARAM.Row npc = npcs[npcId];
                                    int hp = (int)npc["Hp"].Value;
                                    int soul = (int)npc["getSoul"].Value;
                                    locCosts += hp * 2 + soul;
                                    subcount++;
                                }
                                if (subcount == 0) continue;
                                locCosts /= subcount;
                                lotCost[loc.BaseID] = locCosts;
                            }
                            PARAM.Row row = itemLots[loc.ID];
                            int totalPoints = 0;
                            for (int i = 1; i <= 8; i++)
                            {
                                totalPoints += (short)row[$"LotItemBasePoint0{i}"].Value;
                            }
                            subcount = 0;
                            for (int i = 1; i <= 8; i++)
                            {
                                int id = (int)row[$"ItemLotId{i}"].Value;
                                if (id == item.ID)
                                {
                                    int points = (short)row[$"LotItemBasePoint0{i}"].Value;
                                    int quantity = game.Sekiro ? (ushort)row[$"NewLotItemNum{i}"].Value : (byte)row[$"LotItemNum{i}"].Value;
                                    double drop = (double)points / totalPoints;
                                    if (quantity > 1)
                                    {
                                        // If dropping more than one, reverse engineer drop rate for 1
                                        drop = Math.Pow(drop, 1.0 / quantity);
                                    }
                                    locPercentages += drop;
                                    subcount++;
                                }
                            }
                            if (subcount == 0) continue;
                            locPercentages /= subcount;
                            if (locPercentages < 0.75)
                            {
                                modelCosts += lotCost[loc.BaseID];
                                modelPercentages += locPercentages;
                                count++;
                            }
                        }
                        if (count > 0)
                        {
                            percentages += modelPercentages / count;
                            costs += modelCosts / count;
                        }
                    }
                    if (costs > 0)
                    {
                        // If any drops were observed for this item, update the item cost.
                        // Otherwise, use the same drop as the previous item, as adjacent items usually have similar desirability.
                        itemCost = (float)(costs / percentages);
                    }
                    dropCost[item] = itemCost;
                }
            }

            HashSet<int> spiritfalls = new HashSet<int> { 51100950, 51300910, 51700990, 51500910, 51110973, 52500940 };
            bool isPermanent(int eventFlag)
            {
                return eventFlag >= 6500 && eventFlag < 6800 || eventFlag == 6022 || spiritfalls.Contains(eventFlag);
            }
            HashSet<int> allEventFlags = new HashSet<int>(data.Data.Values.SelectMany(locs => locs.Locations.Values.Select(l => l.Scope.EventID).Where(l => l != -1)));
            List<(int, int)> itemLotFlags = itemLots.Rows.Select(r => ((int)r.ID, (int)r["getItemFlagId"].Value)).OrderBy(r => r.Item1).ToList();
            int eventFlagForLot(int itemLot)
            {
                int index = itemLotFlags.FindIndex(r => r.Item1 == itemLot);
                for (int i = index + 1; i < itemLotFlags.Count; i++)
                {
                    (int newLot, int flag) = itemLotFlags[i];
                    if (flag >= 50000000)
                    {
                        while (allEventFlags.Contains(flag))
                        {
                            flag++;
                        }
                        allEventFlags.Add(flag);
                        return flag;
                    }
                }
                throw new Exception($"{itemLot}, found at index {index}, can't event a dang flag");
            }
            // Mapping from old permanent event flag to slot key
            Dictionary<SlotKey, int> permanentSlots = new Dictionary<SlotKey, int>();
            foreach (KeyValuePair<ItemKey, ItemLocations> item in data.Data)
            {
                foreach (ItemLocation loc in item.Value.Locations.Values)
                {
                    if (loc.Scope.Type == ScopeType.EVENT)
                    {
                        int eventFlag = loc.Scope.ID;
                        if (isPermanent(eventFlag))
                        {
                            // Console.WriteLine($"Permanent {eventFlag}: {game.Name(item.Key)}");
                            SlotKey source = new SlotKey(item.Key, loc.Scope);
                            if (permanentSlots.ContainsKey(source)) throw new Exception($"{eventFlag}");
                            permanentSlots[source] = eventFlag;
                        }
                    }
                }
            }

            Dictionary<SlotKey, ItemSource> newRows = new Dictionary<SlotKey, ItemSource>();
            HashSet<int> deleteRows = new HashSet<int>();
            // Dump all target data per-source, before wiping it out
            foreach (KeyValuePair<RandomSilo, SiloPermutation> entry in permutation.Silos)
            {
                SiloPermutation silo = entry.Value;
                foreach (SlotKey sourceKey in silo.Mapping.Values.SelectMany(v => v))
                {
                    ItemLocation source = data.Location(sourceKey);
                    foreach (LocationKey locKey in source.Keys)
                    {
                        if (locKey.Type == LocationType.LOT)
                        {
                            deleteRows.Add(locKey.ID);
                        }
                    }
                    // Synthetic items, like Path of the Dragon
                    if (source.Keys.Count() == 0)
                    {
                        newRows[sourceKey] = new ItemSource(source, null);
                        continue;
                    }
                    // Pick one of the source for item data - they should be equivalent.
                    LocationKey key = source.Keys[0];
                    PARAM.Row row;
                    if (key.Type == LocationType.LOT)
                    {
                        row = itemLots[key.ID];
                    }
                    else
                    {
                        row = shops[key.ID];
                    }
                    Dictionary<string, object> rowDict = row.Cells.ToDictionary(c => c.Def.InternalName, c => c.Value);
                    newRows[sourceKey] = new ItemSource(source, rowDict);
                }
            }
            int dragonFlag = 0;
            Dictionary<int, int> memoryFlags = new Dictionary<int, int>();
            Dictionary<int, byte> itemRarity = Enumerable.ToDictionary(itemLots.Rows.Where(row => deleteRows.Contains((int)row.ID)), row => (int)row.ID, row => (byte)row["LotItemRarity"].Value); // new Dictionary<int, byte>();
            itemLots.Rows = itemLots.Rows.FindAll(row => !deleteRows.Contains((int)row.ID));

            List<string> raceModeInfo = new List<string>();
            Dictionary<int, int> rewrittenFlags = new Dictionary<int, int>();
            Dictionary<int, int> shopPermanentFlags = new Dictionary<int, int>();
            for (int i = 0; i < 20; i++) Console.WriteLine();
            Console.WriteLine($"-- Spoilers:");
            foreach (KeyValuePair<RandomSilo, SiloPermutation> siloEntry in permutation.Silos)
            {
                RandomSilo siloType = siloEntry.Key;
                SiloPermutation silo = siloEntry.Value;
                if (siloType == RandomSilo.REMOVE) continue;
                foreach (KeyValuePair<SlotKey, List<SlotKey>> mapping in silo.Mapping.OrderBy(e => permutation.GetLogOrder(e.Key)))
                {
                    SlotKey targetKey = mapping.Key;
                    ItemLocation targetLocation = data.Location(targetKey);
                    // Event flag - it just so happens that most of the time, we can use the scope to find the one event flag to use - scripts don't specially care about one vs the other.
                    int eventFlag = targetLocation.Scope.EventID;
                    foreach (SlotKey sourceKey in mapping.Value)
                    {
                        ItemKey item = sourceKey.Item;
                        int quantity = data.Location(sourceKey).Quantity;
                        string quantityStr = quantity == 1 ? "" : $" {quantity}x";
                        Console.WriteLine($"{game.Name(item)}{quantityStr}{ann.GetLocationDescription(targetKey, targetLocation.Keys)}");
                        if (opt["racemodeinfo"])
                        {
                            string desc = ann.GetLocationDescription(targetKey, targetLocation.Keys, true);
                            if (!string.IsNullOrEmpty(desc)) raceModeInfo.Add(desc);
                        }
                        bool isDragon = !game.Sekiro && item.Equals(new ItemKey(ItemType.GOOD, 9030));
                        ItemSource source = newRows[sourceKey];
                        Dictionary<string, object> shopCells = null;
                        Dictionary<string, object> lotCells = null;
                        int price = -1;
                        bool originalShop = false;
                        if (source.Row == null)
                        {
                            // Synthetic item - make up shop entry
                            shopCells = ShopCellsForItem(item);
                            MakeSellable(item);
                        }
                        else if (source.Row.ContainsKey("shopType"))
                        {
                            shopCells = source.Row;
                            originalShop = true;
                        }
                        else if (source.Row.ContainsKey("ItemLotId1"))
                        {
                            lotCells = source.Row;
                        }
                        else throw new Exception($"Unknown item source");
                        // TODO: Assigning enemy drops to other enemy drops/infinite shops, should scope which item is being referred to
                        int setEventFlag = -1;
                        foreach (LocationKey target in targetLocation.Keys)
                        {
                            // Console.WriteLine($"{game.Name(item)}: {source.Loc} -> {target.Text}");
                            if (target.Type == LocationType.LOT)
                            {
                                if (siloType == RandomSilo.MIXED)
                                {
                                    Warn($"Mixed silo {source.Loc} going to {target}");
                                    continue;
                                }
                                if (lotCells == null)
                                {
                                    Dictionary<string, object> sourceShop = shopCells;
                                    if (isDragon)
                                    {
                                        // If path of the dragon, there is an additional scripted award, so change base to ember to avoid confusing duplication
                                        sourceShop = new Dictionary<string, object>(sourceShop);
                                        sourceShop["EquipId"] = 500;
                                    }
                                    lotCells = ShopToItemLot(sourceShop, item, target.BaseID);
                                }
                                else if (targetLocation.Scope.Type == ScopeType.MODEL)
                                {
                                    lotCells = originalShop ? ShopToItemLot(shopCells, item, target.BaseID) : ProcessModelLot(lotCells, item, target.BaseID);
                                }
                                if (permanentSlots.TryGetValue(sourceKey, out int permanentFlag))
                                {
                                    lotCells["getItemFlagId"] = permanentFlag;
                                    rewrittenFlags[eventFlag] = permanentFlag;
                                }
                                else if (permanentSlots.TryGetValue(targetKey, out int flagToClear))
                                {
                                    int tempFlag = rewrittenFlags.TryGetValue(eventFlag, out int existingFlag) ? existingFlag : eventFlagForLot(target.BaseID);
                                    lotCells["getItemFlagId"] = tempFlag;
                                    rewrittenFlags[eventFlag] = tempFlag;
                                }
                                else
                                {
                                    lotCells["getItemFlagId"] = eventFlag;
                                }
                                setEventFlag = (int)lotCells["getItemFlagId"];
                                AddLot(target.BaseID, lotCells, itemRarity);
                            }
                            else
                            {
                                // Do some filtering for RandomSilo.MIXED
                                if (shopCells == null)
                                {
                                    if (siloType == RandomSilo.MIXED)
                                    {
                                        Warn($"Mixed silo {source.Loc} going to {target}");
                                        continue;
                                    }
                                    shopCells = ItemLotToShop(lotCells, item);
                                }
                                // If mixed, event flag is present or not based on which shop entry this is (infinite or not)
                                bool infiniteMixed = siloType == RandomSilo.MIXED && (short)shopCells["sellQuantity"] <= 0;
                                // Ignore scope event flag for shop assignment, because some shops also form multidrops
                                int shopEventFlag = (int)shops[target.ID]["EventFlag"].Value;
                                if (permanentSlots.TryGetValue(sourceKey, out int permanentFlag))
                                {
                                    shopPermanentFlags[shopEventFlag] = permanentFlag;
                                }
                                shopCells["EventFlag"] = infiniteMixed ? -1 : shopEventFlag; // (int) lotCells["getItemFlagId"];
                                setEventFlag = (int)shopCells["EventFlag"];
                                int baseShop = target.ID / 100;
                                if (price == -1)
                                {
                                    if (siloType == RandomSilo.SELF && shopCells.ContainsKey("value"))
                                    {
                                        price = (int)shopCells["value"];
                                    }
                                    else
                                    {
                                        bool isTranspose = game.Sekiro ? (baseShop == 10000 || baseShop == 25000) : targetLocation.Scope.Type == ScopeType.MATERIAL;
                                        price = Price(permutation, siloType, item, isTranspose, random);
                                    }
                                    Console.WriteLine($"  (cost: {price})");
                                }
                                // Ignoring selected price for offering box
                                int targetPrice = price;
                                if (game.Sekiro && baseShop == 11005)
                                {
                                    targetPrice = (int)shops[target.ID]["value"].Value;
                                    targetPrice = targetPrice / Math.Max(1, quantity);
                                }
                                // Dragon's Tally Board adjustments
                                if (game.Sekiro && target.ID % 100 >= 50 && targetPrice > 10)
                                {
                                    targetPrice = price - price / 10;
                                }
                                shopCells["value"] = targetPrice;
                                SetShop(target.ID, shopCells);
                            }
                        }
                        // Add special flags for specific items
                        if (game.Sekiro)
                        {
                            // Memories
                            if (item.Type == ItemType.GOOD && item.ID >= 5200 && item.ID <= 5213)
                            {
                                // -1 event flag probably shouldn't happen, but some finite items may still lack event flags, so just don't update memory count in this case
                                if (setEventFlag > 0)
                                {
                                    memoryFlags[item.ID] = setEventFlag;
                                }
                            }
                        }
                        else
                        {
                            if (isDragon)
                            {
                                if (setEventFlag == -1) throw new Exception("Path of the Dragon added to lot without event flag");
                                dragonFlag = setEventFlag;
                                // Don't need to add own item if there is a separate carrier for the event flag
                                if (mapping.Value.Count > 1) continue;
                            }
                        }
                    }
                }
            }
            itemLots.Rows = itemLots.Rows.OrderBy(r => r.ID).ToList();
            Console.WriteLine();

            // Hacky convenience function for generating race mode list
            if (opt["racemodeinfo"])
            {
                HashSet<string> visited = new HashSet<string>();
                foreach (AnnotationData.AreaAnnotation areaAnn in ann.Areas.Values)
                {
                    if (!visited.Add(areaAnn.Text)) continue;
                    List<string> items = raceModeInfo.Where(r => r != null && r.StartsWith($" in {areaAnn.Text}:")).ToList();
                    foreach (string item in items)
                    {
                        Console.WriteLine("In" + item.Substring(3));
                    }
                }
                // raceModeInfo
            }

            // Events
            if (game.Sekiro)
            {
                Dictionary<string, Dictionary<string, ESD>> talks = game.Talk();
                Dictionary<int, EventSpec> talkTemplates = events.Config.ItemTalks.ToDictionary(e => e.ID, e => e);
                bool parseMachineName(string mIdStr, out int mId)
                {
                    if (!int.TryParse(mIdStr, out mId))
                    {
                        if (mIdStr.StartsWith("x") && int.TryParse(mIdStr.Substring(1), out int diffpart))
                        {
                            mId = 0x7FFFFFFF - diffpart;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    return true;
                }
                foreach (KeyValuePair<string, ESD> entry in talks.SelectMany(s => s.Value))
                {
                    void rewriteCondition(ESD.Condition cond, Action<byte[]> rewriteExpr)
                    {
                        rewriteExpr(cond.Evaluator);
                        cond.PassCommands.ForEach(c => rewriteCommand(c, rewriteExpr));
                        cond.Subconditions.ForEach(c => rewriteCondition(c, rewriteExpr));
                    }
                    void rewriteCommand(ESD.CommandCall cmd, Action<byte[]> rewriteExpr)
                    {
                        cmd.Arguments.ForEach(rewriteExpr);
                    }
                    string esdName = entry.Key;
                    if (esdName.StartsWith("t") && int.TryParse(esdName.Substring(1), out int esdId) && talkTemplates.TryGetValue(esdId, out EventSpec spec))
                    {
                        ESD esd = entry.Value;
                        Dictionary<int, int> replaceInts = new Dictionary<int, int>();
                        HashSet<int> machines = new HashSet<int>();
                        foreach (ItemTemplate t in spec.ItemTemplate)
                        {
                            int machine = parseMachineName(t.Machine, out int mId) ? mId : throw new Exception($"Unknown machine id {t.Machine} of {esdName}");
                            machines.Add(machine);
                            int flag = int.Parse(t.EventFlag);
                            if (t.Type == "loc")
                            {
                                if (rewrittenFlags.TryGetValue(flag, out int newFlag))
                                {
                                    replaceInts[flag] = newFlag;
                                }
                            }
                            else if (t.Type == "isshin")
                            {
                                replaceInts[flag] = int.Parse(t.Replace);
                            }
                        }
                        if (replaceInts.Count == 0)
                        {
                            continue;
                        }
                        void rewriteExpr(byte[] b)
                        {
                            // For now, these can be rewritten in place. If AST were used instead, it could not be in place, as lengths may change.
                            foreach (KeyValuePair<int, int> replace in replaceInts)
                            {
                                int search = SearchInt(b, (uint)replace.Key);
                                if (search != -1)
                                {
                                    Array.Copy(BitConverter.GetBytes(replace.Value), 0, b, search, 4);
                                }
                            }
                        }
                        foreach (KeyValuePair<long, Dictionary<long, ESD.State>> machine in esd.StateGroups)
                        {
                            if (!machines.Contains((int)machine.Key)) continue;
                            foreach (KeyValuePair<long, ESD.State> stateEntry in machine.Value)
                            {
                                ESD.State state = stateEntry.Value;
                                state.Conditions.ForEach(c => rewriteCondition(c, rewriteExpr));
                                state.EntryCommands.ForEach(c => rewriteCommand(c, rewriteExpr));
                                state.ExitCommands.ForEach(c => rewriteCommand(c, rewriteExpr));
                                state.WhileCommands.ForEach(c => rewriteCommand(c, rewriteExpr));
                            }
                        }
                    }
                }

                Dictionary<string, EMEVD> emevds = game.Emevds;
                Dictionary<int, EventSpec> templates = events.Config.ItemEvents.ToDictionary(e => e.ID, e => e);

                HashSet<ItemTemplate> completedTemplates = new HashSet<ItemTemplate>();
                bool argSpec(string arg, out int pos)
                {
                    pos = 0;
                    if (arg.StartsWith("X") && int.TryParse(arg.Substring(1), out pos))
                    {
                        pos /= 4;
                        return true;
                    }
                    return false;
                }
                foreach (KeyValuePair<string, EMEVD> entry in emevds)
                {
                    Dictionary<int, EMEVD.Event> fileEvents = entry.Value.Events.ToDictionary(e => (int)e.ID, e => e);
                    foreach (EMEVD.Event e in entry.Value.Events)
                    {
                        OldParams initOld = OldParams.Preprocess(e);
                        for (int i = 0; i < e.Instructions.Count; i++)
                        {
                            Instr init = events.Parse(e.Instructions[i]);
                            if (!init.Init) continue;
                            int callee = init.Callee;
                            if (!templates.TryGetValue(callee, out EventSpec ev)) continue;
                            if (ev.ItemTemplate.Count == 0) throw new Exception($"event {callee} has no templates");
                            // Types: item, loc, any, remove
                            if (ev.ItemTemplate[0].Type == "remove")
                            {
                                // Remove action by removing initialization, for now. Can garbage collect later if desired.
                                e.Instructions[i] = new EMEVD.Instruction(1014, 69);
                                continue;
                            }
                            // Source flag and event to edit
                            List<(int, EMEVD.Event, ItemTemplate)> eventCopies = new List<(int, EMEVD.Event, ItemTemplate)>();
                            foreach (ItemTemplate t in ev.ItemTemplate)
                            {
                                if (t.Type != "loc")
                                {
                                    continue;
                                }
                                int argFlag = 0;
                                int flag;
                                if (argSpec(t.EventFlag, out int pos))
                                {
                                    argFlag = (int)init.Args[init.Offset + pos];
                                    if (argFlag == 0) continue;
                                    flag = argFlag;
                                }
                                else if (int.TryParse(t.EventFlag, out flag))
                                {
                                    // Use parsed value
                                }
                                else throw new Exception($"Internal error: Could not extract item flag {t.EventFlag} from {callee}");
                                if (fileEvents.TryGetValue(callee, out EMEVD.Event theEvent))
                                {
                                    eventCopies.Add((flag, theEvent, t));
                                }
                                else if (argFlag != 0)
                                {
                                    eventCopies.Add((flag, null, t));
                                }
                                else
                                {
                                    Console.WriteLine($"{t.Type} - {t.EventFlag} - {flag} - {argFlag}");
                                    throw new Exception($"Initialized event {callee} but absent from this file and not specified in args");
                                }
                            }
                            // Do in-place pass
                            foreach (ItemTemplate t in ev.ItemTemplate)
                            {
                                if (t.Type != "loc")
                                {
                                    if (t.IsDefault())
                                    {
                                        // This is fine, nothing to do if not chr
                                    }
                                    else if (fileEvents.TryGetValue(callee, out EMEVD.Event theEvent))
                                    {
                                        if (completedTemplates.Contains(t)) continue;
                                        completedTemplates.Add(t);
                                        eventCopies.Add((0, theEvent, t));
                                    }
                                    else if (t.Type == "carp")
                                    {
                                        eventCopies.Add((0, null, t));
                                    }
                                    else throw new Exception($"Can't find event {callee} with {t.Type} template");
                                }
                            }
                            foreach (var copy in eventCopies)
                            {
                                (int flag, EMEVD.Event e2, ItemTemplate t) = copy;
                                // Main entities to replace
                                Dictionary<int, int> reloc = new Dictionary<int, int>();
                                if (flag != 0)
                                {
                                    if (rewrittenFlags.TryGetValue(flag, out int newFlag) && flag != newFlag)
                                    {
                                        reloc[flag] = newFlag;
                                    }
                                    // TODO: Item lot replacement
                                }
                                if (t.Type == "carp")
                                {
                                    if (!argSpec(t.Entity, out int entityPos)) throw new Exception($"{callee}");
                                    if (!argSpec(t.ItemLot, out int lotPos)) throw new Exception($"{callee}");
                                    if (data.NewEntityLots.TryGetValue((int)init[init.Offset + entityPos], out int newLot))
                                    {
                                        reloc[(int)init[init.Offset + lotPos]] = newLot;
                                    }
                                }
                                // Add all edits
                                EventEdits edits = new EventEdits();
                                if (t.Remove != null)
                                {
                                    foreach (string remove in Regex.Split(t.Remove, @"\s*;\s*"))
                                    {
                                        events.RemoveMacro(edits, remove);
                                    }
                                }
                                if (t.Replace != null)
                                {
                                    foreach (string replace in Regex.Split(t.Replace, @"\s*;\s*"))
                                    {
                                        events.ReplaceMacro(edits, replace);
                                    }
                                }
                                // Also edit conditional flags present in both the event and the target
                                if (flag > 0)
                                {
                                    // TODO: rewrite flags to another value
                                }
                                if (t.RemoveArg != null)
                                {
                                    if (!argSpec(t.RemoveArg, out int removePos)) throw new Exception($"{callee} {t.RemoveArg}");
                                    init[init.Offset + removePos] = 0;
                                }

                                if (e2 != null)
                                {
                                    // TODO: check that all remove/replaces has been activated
                                    OldParams pre = OldParams.Preprocess(e2);
                                    // Also do startcmds here? Add any additional commands before any other processing
                                    if (t.Add != null)
                                    {
                                        events.AddMacro(edits, t.Add);
                                    }
                                    for (int j = 0; j < e2.Instructions.Count; j++)
                                    {
                                        Instr instr = events.Parse(e2.Instructions[j]);
                                        // Randomized events shouldn't have initializations, although we could probably also ignore them
                                        if (instr.Init) throw new Exception($"Unexpected event initialization in template event {e2.ID}");
                                        // We are either dealing with a copy of the event or the original one. So all edits are in-place
                                        // Remove/replace cases
                                        edits.ApplyEdits(instr, j);
                                        instr.Save();
                                        e2.Instructions[j] = instr.Val;
                                    }
                                    events.ApplyAdds(edits, e2);
                                    pre.Postprocess();
                                }
                                if (edits.PendingEdits.Count != 0)
                                {
                                    throw new Exception($"{callee} has unapplied edits: {string.Join("; ", edits.PendingEdits)}");
                                }
                                // Also add regions and multichr
                                // How to handle startcmd? Answer: maybe don't...
                                if (reloc.Count > 0)
                                {
                                    events.RewriteInts(init, reloc);
                                    init.Save();
                                    if (e2 != null)
                                    {
                                        for (int j = 0; j < e2.Instructions.Count; j++)
                                        {
                                            Instr instr = events.Parse(e2.Instructions[j]);
                                            if (instr.Init) throw new Exception($"Unexpected event initialization in template event {e.ID}");
                                            events.RewriteInts(instr, reloc);
                                            instr.Save();
                                        }
                                    }
                                }
                            }
                        }
                        initOld.Postprocess();
                    }
                    if (entry.Key == "common")
                    {
                        // Add custom event and item lot for granting display-only memories
                        // Use unused lot 3440 and unused event flag range 930-950ish
                        ItemKey memory = new ItemKey(ItemType.GOOD, 5400);
                        int memoryLot = 3440;  // unused lot
                        Dictionary<string, object> memCells = ShopToItemLot(ShopCellsForItem(memory), memory, memoryLot);
                        memCells["getItemFlagId"] = -1;
                        AddLot(memoryLot, memCells, itemRarity);

                        EMEVD.Event memEv = new EMEVD.Event(930, EMEVD.Event.RestBehaviorType.Default);
                        memEv.Instructions.Add(new EMEVD.Instruction(1003, 2, new List<object> { (byte)0, (byte)1, (byte)2, 0 }));  // End if self event flag + slot
                        memEv.Instructions.Add(new EMEVD.Instruction(3, 0, new List<object> { (byte)0, (byte)1, (byte)0, 0 }));  // If flag
                        memEv.Instructions.Add(new EMEVD.Instruction(2003, 4, new List<object> { memoryLot }));  // Grant item lot
                        memEv.Parameters.Add(new EMEVD.Parameter(1, 4, 0, 4));
                        entry.Value.Events.Add(memEv);
                        
                        int slot = 0;
                        foreach (KeyValuePair<int, int> mem in memoryFlags)
                        {
                            entry.Value.Events[0].Instructions.Add(new EMEVD.Instruction(2000, 0, new List<object> { slot++, (uint)930, (uint)mem.Value }));
                        }

                        // Mibu Breathing Technique item in shops should also grant the skill
                        entry.Value.Events[0].Instructions.Add(new EMEVD.Instruction(2000, 0, new List<object> { 6, (uint)460, (uint)11100621, (uint)2420, (uint)610 }));

                        // Add permanent shop placement flags
                        slot = 0;
                        foreach (KeyValuePair<int, int> shop in shopPermanentFlags)
                        {
                            entry.Value.Events[0].Instructions.Add(new EMEVD.Instruction(2000, 0, new List<object> { slot++, (uint)750, (uint)shop.Key, (uint)shop.Value }));
                        }
                    }
                }
            }
            // Misc DS3 edits
            if (!game.Sekiro)
            {
                // Remove Storm Ruler infinite shiny (gives Storm Ruler if got the original but somehow dropped it). This lot is not randomized
                itemLots[4600]["LotItemNum1"].Value = (byte)0;
                // Disable Firelink Shrine bonfire without Coiled Sword, with special event flag
                game.Param("ActionButtonParam")[9351]["grayoutFlag"].Value = 14005108;
                // Description for path of the dragon so it's not ?GoodsInfo?
                string dragonInfo = "A gesture of meditation channeling the eternal essence of the ancient dragons";
                string dragonCap = $"{dragonInfo}.\n\nThe path to ascendence can be achieved only by the most resolute of seekers. Proper utilization of this technique can grant deep inner focus.";
                game.SetMessage(GameData.MsgFile.GOODS_INFO, 9030, dragonInfo);
                game.SetMessage(GameData.MsgFile.GOODS_CAPTION, 9030, dragonCap);

                // Do the Path of the Dragon swap
                if (dragonFlag == 0) throw new Exception("Path of the Dragon not assigned to an event");
                game.ReplaceScript("common", 0x2BADCAFE, (uint)dragonFlag);
            }

            // Swap chests
            foreach (KeyValuePair<EntityId, EntityId> swap in permutation.EntitySwaps)
            {
                EntityId e1 = swap.Key;
                EntityId e2 = swap.Value;
                if (e1.MapName != e2.MapName) throw new Exception($"Different maps for entities to swap {e1}, {e2}");
                string col1 = ChestCollisionNames.ContainsKey(e1) ? ChestCollisionNames[e1] : null;
                string col2 = ChestCollisionNames.ContainsKey(e2) ? ChestCollisionNames[e2] : null;
                MSB3 msb = game.EditMap(RevLocationNames[e1.MapName]);
                MSB3.Part thing1 = msb.Parts.Objects.Find(p => p.Name == e1.EntityName);
                MSB3.Part thing2 = msb.Parts.Enemies.Find(p => p.Name == e2.EntityName);
                BasicLocation tmp = BasicLocation.Get(thing2, col2);
                BasicLocation.Get(thing1, col1).Set(thing2);
                tmp.Set(thing1);
            }
        }
        private class BasicLocation
        {
            public string CollisionName;
            public Vector3 Position;
            public Vector3 Rotation;
            public static BasicLocation Get(MSB3.Part part, string collisionName=null)
            {
                string col;
                if (part is MSB3.Part.Enemy e) col = e.CollisionName;
                else if (part is MSB3.Part.Object o) col = o.CollisionName;
                else throw new Exception(part.ModelName);
                if (col == null || col == "")
                {
                    if (collisionName == null) throw new Exception($"Bad location randomization for {part.Name}");
                    col = collisionName;
                }
                return new BasicLocation { CollisionName = col, Position = part.Position, Rotation = part.Rotation };
            }
            public void Set(MSB3.Part part)
            {
                if (part is MSB3.Part.Enemy e) e.CollisionName = CollisionName;
                else if (part is MSB3.Part.Object o) o.CollisionName = CollisionName;
                else throw new Exception(part.ModelName);
                part.Position = Position;
                part.Rotation = Rotation;
            }
        }

        private PriceCategory GetSekiroPriceCategory(ItemKey key)
        {
            return data.Data[key].Unique && !game.Name(key).Contains("Jizo") ? PriceCategory.UNIQUE_GOOD : PriceCategory.REGULAR_GOOD;
        }
        private PriceCategory GetPriceCategory(ItemKey key, bool isTranspose)
        {
            // Effectively don't use transpose category - instead use rules for base category.
            // if (isTranspose) return PriceCategory.TRANSPOSE;
            if (key.Type != ItemType.GOOD) return (PriceCategory)key.Type;
            if (key.ID >= 1200000) return PriceCategory.SPELLS;
            if (key.ID >= 1000 & key.ID <= 1030) return PriceCategory.UPGRADE;
            if (!finiteShopClassification.ContainsKey(key))
            {
                // If infinite shop, item is infinite
                // If finite shop, item is finite
                // If not in any shops, use lot finiteness
                bool finiteShop = false, infiniteShop = false, infiniteLot = false;
                foreach (ItemLocation loc in data.Data[key].Locations.Values)
                {
                    if (loc.Scope.Type == ScopeType.SHOP_INFINITE)
                    {
                        infiniteShop = true;
                    }
                    else if (loc.Scope.Type == ScopeType.MODEL)
                    {
                        infiniteLot = true;
                    }
                    else if (loc.Scope.Type == ScopeType.MATERIAL || (loc.Scope.Type == ScopeType.EVENT && loc.Keys.Any(k => k.Type == LocationType.SHOP)))
                    {
                        finiteShop = true;
                    }
                }
                bool isInfinite = infiniteShop || (!finiteShop && infiniteLot);
                finiteShopClassification[key] = !isInfinite;
            }
            return finiteShopClassification[key] ? PriceCategory.FINITE_GOOD : PriceCategory.INFINITE_GOOD;
        }

        // Use simple DS1 item randomizer type system for the moment
        private int Price(Permutation permutation, RandomSilo siloType, ItemKey item, bool isTranspose, Random random)
        {
            if (game.Sekiro)
            {
                // Simpler algorithm here
                if (isTranspose)
                {
                    return 0;
                }
                int price;
                if (permutation.ItemLateness.ContainsKey(item) && item.Type == ItemType.GOOD)
                {
                    // From 100 (with range) to 2000 (without range) based on game lateness. But shops are rarely super late.
                    double basePrice = 100 + permutation.ItemLateness[item] * (2000 / 1.5 - 100);
                    // On sale if not a key item
                    if (!permutation.KeyItems.Contains(item)) basePrice /= 2;
                    // 50% in either direction
                    basePrice = basePrice * (random.NextDouble() + 0.5);
                    // Rounding
                    List<int> rounds = new List<int> { 100, 100, 200 };
                    if (basePrice < 300) rounds.Add(50);
                    int round = Choice(random, rounds);
                    price = (((int)basePrice / round) + 1) * round;
                }
                else
                {
                    price = Choice(random, prices[GetSekiroPriceCategory(item)]);
                    // Could use a category for this, but meanwhile just make sure esoteric texts are reasonable
                    if (item.ID >= 2920 && item.ID < 2930) price = Math.Min(price, 200);
                }
                PARAM.Row row = game.Item(item);
                int sellPrice = (int)row["sellValue"].Value;
                if (price < sellPrice)
                {
                    price = sellPrice;
                }
                return price;
            }
            else
            {
                PriceCategory cat = GetPriceCategory(item, isTranspose);
                PARAM.Row row = game.Item(item);
                // Upgrade materials roughly same. Unique ones on sale because of how many are moved to shops usually.
                if (cat == PriceCategory.UPGRADE)
                {
                    int basePrice = (int)row[itemValueCells[(int)item.Type]].Value;
                    return siloType == RandomSilo.FINITE ? basePrice / 2 : basePrice;
                }
                int sellPrice = (int)row["sellValue"].Value;
                // If it's a soul, make it cost a more than the soul cost.
                if (cat == PriceCategory.FINITE_GOOD && sellPrice >= 2000)
                {
                    return sellPrice + 1000;
                }
                int price;
                if (permutation.ItemLateness.ContainsKey(item) && item.Type == ItemType.GOOD)
                {
                    // From 500 (with range) to 10k (without range) based on game lateness
                    double basePrice = 500 + permutation.ItemLateness[item] * (10000 / 1.5 - 500);
                    // On sale if not a key item
                    if (!permutation.KeyItems.Contains(item)) basePrice /= 2;
                    // 50% in either direction
                    basePrice = basePrice * (random.NextDouble() + 0.5);
                    // Round to next 100 (if less than 2000), 500 or 1000
                    List<int> rounds = new List<int> { 500, 1000 };
                    if (basePrice < 2000) rounds.Add(100);
                    int round = Choice(random, rounds);
                    price = (((int)basePrice / round) + 1) * round;
                }
                else
                {
                    price = Choice(random, prices[cat]);
                    // Here we could also hike up the price for especially good items
                }
                if (price < sellPrice)
                {
                    price = sellPrice;
                }
                if (isTranspose && random.NextDouble() < 0.4)
                {
                    price = 0;
                }
                return price;
            }
        }

        private void AddLot(int baseLot, Dictionary<string, object> cells, Dictionary<int, byte> itemRarity)
        {
            PARAM itemLots = game.Param("ItemLotParam");
            int targetLot = baseLot;
            while (itemLots[targetLot] != null)
            {
                targetLot++;
            }
            PARAM.Row row = game.AddRow("ItemLotParam", targetLot);
            foreach (KeyValuePair<string, object> cell in cells)
            {
                if (cell.Key == "LotItemRarity")
                {
                    continue;
                }
                row[cell.Key].Value = cell.Value;
            }
            if (itemRarity.ContainsKey(baseLot))
            {
                row["LotItemRarity"].Value = itemRarity[baseLot];
            }
        }

        private void SetShop(int targetShop, Dictionary<string, object> cells)
        {
            PARAM shops = game.Param("ShopLineupParam");
            PARAM.Row row = shops[targetShop];
            foreach (KeyValuePair<string, object> cell in cells)
            {
                if (cell.Key != "qwcID" && cell.Key != "mtrlId")  // This is right... right?
                {
                    row[cell.Key].Value = cell.Value;
                }
            }
        }

        private (int, int) DropRate(ItemKey key, int baseLot, int quantity)
        {
            // Cost is ratio of enemy cost to drop %
            float cost = dropCost.ContainsKey(key) ? dropCost[key] : 10000;
            int enemy = lotCost.ContainsKey(baseLot) ? lotCost[baseLot] : 500;
            double oneDrop = enemy / cost;
            // Keep increasing item quantity until drop rate becomes reasonable. If that is possible.
            int iter = 0;
            while (iter++ < 20)
            {
                // Forward: oneDrop = drop ^ (1/quantity)
                double drop = Math.Pow(oneDrop, quantity);
                int dropNum = (int)(drop * 1000);
                if (dropNum <= 900 || quantity >= 5)
                {
                    return (Math.Max(5, Math.Min(1000, dropNum)), quantity);
                }
                quantity *= 2;
                oneDrop /= 2;
            }
            return (100, 1);
        }

        private Dictionary<string, object> ProcessModelLot(Dictionary<string, object> lotCells, ItemKey key, int baseLot)
        {
            lotCells = new Dictionary<string, object>(lotCells);
            List<int> quantities = new List<int>();
            int totalPoints = 0;
            for (int i = 1; i <= 8; i++)
            {
                totalPoints += (short)lotCells[$"LotItemBasePoint0{i}"];
            }
            for (int i = 1; i <= 8; i++)
            {
                if ((int)lotCells[$"ItemLotId{i}"] == 0)
                {
                    continue;
                }
                ItemKey lotKey = new ItemKey(LocationData.LotTypes[(uint)lotCells[$"LotItemCategory0{i}"]], (int)lotCells[$"ItemLotId{i}"]);
                if (lotKey.Equals(key))
                {
                    quantities.Add(game.Sekiro ? (ushort)lotCells[$"NewLotItemNum{i}"] : (byte)lotCells[$"LotItemNum{i}"]);
                }
                lotCells[$"ItemLotId{i}"] = 0;
                lotCells[$"LotItemCategory0{i}"] = 0xFFFFFFFF;
                lotCells[$"LotItemBasePoint0{i}"] = (short)0;
                SetItemLotCount(lotCells, i, 0);
            }
            // For now, disable resource drops
            lotCells["LotItemNum1"] = (byte)0;
            if (quantities.Count == 0) quantities.Add(1);
            int cumDrop = 0;
            for (int i = 1; i <= 8; i++)
            {
                if (i <= quantities.Count)
                {
                    int count = quantities[i - 1];
                    (int drop, int quantity) = DropRate(key, baseLot, count);
                    lotCells[$"ItemLotId{i}"] = key.ID;
                    lotCells[$"LotItemCategory0{i}"] = RevLotTypes[key.Type];
                    lotCells[$"LotItemBasePoint0{i}"] = (short)drop;
                    SetItemLotCount(lotCells, i, quantity);
                    cumDrop += drop;
                    // This is a bit verbose for Sekiro
                    // Console.WriteLine($"  Drop chance for {quantity}: {100.0 * drop / 1000}%");
                }
                else if (i == quantities.Count + 1)
                {
                    lotCells[$"LotItemBasePoint0{i}"] = (short)Math.Max(0, 1000 - cumDrop);
                    break;
                }
            }
            return lotCells;
        }

        private Dictionary<string, object> ShopCellsForItem(ItemKey item)
        {
            return new Dictionary<string, object>()
            {
                { "EquipId", item.ID },
                { "equipType", (byte)item.Type },
                { "sellQuantity", (short)1 }
            };
        }

        private void SetItemLotCount(Dictionary<string, object> cells, int i, int quantity)
        {
            if (game.Sekiro)
            {
                cells[$"NewLotItemNum{i}"] = (ushort)quantity;
            }
            else
            {
                cells[$"LotItemNum{i}"] = (byte)quantity;
            }
        }
        
        private Dictionary<string, object> ShopToItemLot(Dictionary<string, object> shopCells, ItemKey key, int baseLot)
        {
            Dictionary<string, object> lotCells = new Dictionary<string, object>();
            lotCells["ItemLotId1"] = (int)shopCells["EquipId"];
            // Make default quantity 0, and also disable resource drop flag in Sekiro
            lotCells["LotItemNum1"] = (byte)0;
            lotCells["LotItemCategory01"] = LocationData.RevLotTypes[(ItemType)(byte)shopCells["equipType"]];
            int quantity = (short)shopCells["sellQuantity"];
            if (quantity > 0)
            {
                // Ring of sacrifice multi-drops do not work
                if (key.Equals(new ItemKey(ItemType.RING, 20210)) && quantity > 1)
                {
                    quantity = 1;
                }
                SetItemLotCount(lotCells, 1, quantity);
                lotCells["LotItemBasePoint01"] = (short)100;
            }
            else
            {
                (int drop, int quant) = DropRate(key, baseLot, 1);
                // Console.WriteLine($"  Drop chance for {quant}: {100.0 * drop / 1000}%");
                SetItemLotCount(lotCells, 1, quant);
                lotCells["LotItemBasePoint01"] = (short)drop;
                lotCells["LotItemCategory02"] = 0xFFFFFFFF;
                lotCells["LotItemBasePoint02"] = (short)(1000 - drop);
            }
            lotCells["cumulateNumFlagId"] = -1;
            return lotCells;
        }

        private Dictionary<string, object> ItemLotToShop(Dictionary<string, object> lotCells, ItemKey itemKey)
        {
            Dictionary<string, object> shopCells = new Dictionary<string, object>();
            // For an item like this, assume QWC id stays the same
            ItemKey lotKey = null;
            int totalPoints = 0;
            for (int i = 1; i <= 8; i++)
            {
                totalPoints += (short)lotCells[$"LotItemBasePoint0{i}"];
            }
            for (int i = 1; i <= 8; i++)
            {
                if ((int)lotCells[$"ItemLotId{i}"] == 0)
                {
                    continue;
                }
                lotKey = new ItemKey(LocationData.LotTypes[(uint)lotCells[$"LotItemCategory0{i}"]], (int)lotCells[$"ItemLotId{i}"]);
                if (!lotKey.Equals(itemKey))
                {
                    lotKey = null;
                    continue;
                }
                if (game.Sekiro && lotKey.Type == ItemType.WEAPON && lotKey.ID == 680000)
                {
                    shopCells["EquipId"] = 2420;
                    shopCells["equipType"] = (byte)3;
                }
                else
                {
                    shopCells["EquipId"] = lotKey.ID;
                    shopCells["equipType"] = (byte)lotKey.Type;
                }
                int basePoints = (short)lotCells[$"LotItemBasePoint0{i}"];
                if (basePoints == totalPoints)
                {
                    // TODO: If no event id or material id, this won't do much. But that is intended?
                    shopCells["sellQuantity"] = (short)(game.Sekiro ? (ushort)lotCells[$"NewLotItemNum{i}"] : (byte)lotCells[$"LotItemNum{i}"]);
                }
                else
                {
                    shopCells["sellQuantity"] = (short)-1;
                }
                break;
            }
            if (lotKey == null)
            {
                Console.WriteLine($"XX Invalid source location for {itemKey}!! {String.Join(", ", lotCells.Select(e => e.Key + " = " + e.Value))}");
                return null;
            }
            MakeSellable(lotKey);
            return shopCells;
        }

        private void MakeSellable(ItemKey key)
        {
            if (game.Sekiro)
            {
                // Low-level substitution done for Mibu Breathing Technique. Better to do it at low level, since item key is used for other things at high level.
                if (key.Type == ItemType.WEAPON && key.ID == 680000)
                {
                    key = new ItemKey(ItemType.GOOD, 2420);
                }
                if (key.Type != ItemType.GOOD) throw new Exception($"Trying to sell non-item {key} in Sekiro, todo need to handle this");
                PARAM.Row itemRow = game.Item(key);
                if ((int)itemRow["shopId"].Value == -1)
                {
                    itemRow["shopId"].Value = 100;
                }
            }
            else
            {
                PARAM.Row itemRow = game.Item(key);
                PARAM.Cell costCell = itemRow[itemValueCells[(int)key.Type]];
                // Items must have this field to be sold in shops
                if ((int)costCell.Value <= 0)
                {
                    costCell.Value = 1000;
                }
            }
        }

        private class ItemSource
        {
            public readonly ItemLocation Loc;
            // Maybe use a Row object? But it might be nice to edit shops in place...
            public readonly Dictionary<string, object> Row;
            public ItemSource(ItemLocation loc, Dictionary<string, object> row)
            {
                this.Loc = loc;
                this.Row = row;
            }
        }
    }
}
