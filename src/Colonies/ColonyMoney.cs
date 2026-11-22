using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace CreateAColony.Colonies;

/// <summary>
/// What the colony's money is (0.177.0): vanilla rusty gears, or the Coinage mod's coins when that mod is
/// installed. Works on both sides, so windows can say "gears" or "coins". Coinage is optional: nothing of it is
/// bundled, it is only looked for by its item codes.
/// </summary>
public static class ColonyMoney
{
    public const string GearCode = "game:gear-rusty";
    /// <summary>The mod's old copper coin (before 0.177.0). Still loaded, hidden, so saves keep it; colonies swap it for gears.</summary>
    public const string OldCoinCode = "createacolony:coin-copper";
    /// <summary>The Coinage mod's coins, as item codes with wildcards (config OtherCoins overrides this on the server).</summary>
    public static string[] CoinagePatterns = { "coinage:coin", "coinage:coin-*" };
    public const string CoinageModId = "coinage";

    static readonly ConditionalWeakTable<IWorldAccessor, object> coinageByWorld = new();
    /// <summary>The last answer of CoinageLoaded, for text made where no api is at hand.</summary>
    static bool lastLoaded;

    /// <summary>True for one of the Coinage mod's coins.</summary>
    public static bool IsCoinageCoin(ItemStack? s)
    {
        if (s?.Collectible?.Code is not { } code) return false;
        return CoinagePatterns.Any(p => WildcardUtil.Match(new AssetLocation(p), code));
    }

    /// <summary>True when the Coinage mod is installed (its mod id, or any item matching its coin codes).</summary>
    public static bool CoinageLoaded(ICoreAPI api)
    {
        var world = api.World;
        if (coinageByWorld.TryGetValue(world, out var known)) return (bool)known;
        bool loaded = api.ModLoader.IsModEnabled(CoinageModId)
            || world.Items.Any(i => i?.Code != null && CoinagePatterns.Any(p => WildcardUtil.Match(new AssetLocation(p), i.Code)));
        coinageByWorld.AddOrUpdate(world, loaded);
        lastLoaded = loaded;
        return loaded;
    }

    /// <summary>The first Coinage coin the game has, for colonies that have not picked one yet; null without Coinage.</summary>
    public static ItemStack? FirstCoinageCoin(IWorldAccessor world)
    {
        var item = world.Items.FirstOrDefault(i => i?.Code != null && CoinagePatterns.Any(p => WildcardUtil.Match(new AssetLocation(p), i.Code)));
        return item == null ? null : new ItemStack(item);
    }

    /// <summary>"gears" or "coins", for text.</summary>
    public static string Word(ICoreAPI api) => Lang.Get(CoinageLoaded(api) ? "createacolony:money-coins" : "createacolony:money-gears");

    /// <summary>Puts the money word in for [money] in a piece of text.</summary>
    public static string Fill(ICoreAPI api, string text) => text.Contains("[money]") ? text.Replace("[money]", Word(api)) : text;

    /// <summary>Lang.Get with [money] filled in, from the last CoinageLoaded answer (for static code).</summary>
    public static string Get(string key, params object[] args)
    {
        string text = Lang.Get(key, args);
        return text.Contains("[money]") ? text.Replace("[money]", Lang.Get(lastLoaded ? "createacolony:money-coins" : "createacolony:money-gears")) : text;
    }

    /// <summary>Lang.Get with [money] filled in.</summary>
    public static string Get(ICoreAPI api, string key, params object[] args) => Fill(api, Lang.Get(key, args));
}
