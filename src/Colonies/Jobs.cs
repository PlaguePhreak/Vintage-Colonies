namespace CreateAColony.Colonies;

/// <summary>The job codes settlers can be given.</summary>
public static class Jobs
{
    public const string Builder = "builder";
    public const string Courier = "courier";
    public const string Forester = "forester";
    public const string Farmer = "farmer";
    public const string Guard = "guard";
    public const string Potter = "potter";
    public const string Cook = "cook";
    public const string Prospector = "prospector";
    public const string Miner = "miner";
    public const string Quarrier = "quarrier";
    public const string Smelter = "smelter";
    public const string Blacksmith = "blacksmith";
    public const string Armourer = "armourer";
    public const string Weaponsmith = "weaponsmith";
    public const string Sawyer = "sawyer";
    public const string Craftsman = "craftsman";
    public const string Tailor = "tailor";
    public const string Jeweller = "jeweller";
    public const string Healer = "healer";
    public const string Explorer = "explorer";
    public const string Gatherer = "gatherer";
    public const string Hunter = "hunter";
    /// <summary>Works at the Butcher's Hut: cuts up dead animals for meat, hides, fat and bones.</summary>
    public const string Butcher = "butcher";
    /// <summary>Herders keep animals in pens at their huts (ColonyManager.Herding.cs).</summary>
    public const string ChickenHerder = "chickenherder";
    public const string Swineherd = "swineherd";
    public const string Shepherd = "shepherd";
    public const string Goatherd = "goatherd";
    /// <summary>Keeps any farm animal, other mods' too.</summary>
    public const string Herdsman = "herdsman";
    /// <summary>Makes leather in barrels at the Tannery.</summary>
    public const string Tanner = "tanner";
    /// <summary>Looks after the beehives at the Apiary (ColonyManager.Beekeeping.cs).</summary>
    public const string Beekeeper = "beekeeper";
    /// <summary>Brings the dead to the Graveyard and buries them (ColonyManager.Graveyard.cs).</summary>
    public const string Undertaker = "undertaker";
    /// <summary>One per colony, at the Town Hall: answers wanderers, hands out jobs, oversees the fields and collects rent (ColonyManager.Bailiff.cs).</summary>
    public const string Bailiff = "bailiff";
    /// <summary>Strikes coins at the Royal Mint.</summary>
    public const string Minter = "minter";
    /// <summary>Studies at the Archive and makes Research Points (ColonyManager.Research.cs).</summary>
    public const string Researcher = "researcher";
    /// <summary>Works at the Church: holds services and funerals, keeps the records. One of them can be the Bishop (WorkRole, see ChurchRoles).</summary>
    public const string Priest = "priest";

    /// <summary>All assignable jobs; an empty code means unemployed.</summary>
    public static readonly string[] All = { "", Builder, Courier, Forester, Farmer, Guard, Potter, Cook, Prospector, Miner, Quarrier, Smelter, Blacksmith, Armourer, Weaponsmith, Sawyer, Craftsman, Tailor, Jeweller, Healer, Explorer, Gatherer, Hunter, Butcher, ChickenHerder, Swineherd, Shepherd, Goatherd, Herdsman, Tanner, Beekeeper, Undertaker, Minter, Researcher, Priest, Bailiff };

    /// <summary>The job whose skill a settler's work trains and uses: a prospector on Mine ore works as a miner.</summary>
    public static string? WorkJob(CitizenRecord c) => c.JobCode == Prospector && c.WorkRole == ProspectorRoles.Mine ? Miner : c.JobCode;

    /// <summary>Jobs that make things at a crafting hut, which Specialized Craftsmen speeds up (ColonyManager.Research.cs).</summary>
    public static bool IsCrafter(string? code) => code is Potter or Cook or Smelter or Blacksmith or Armourer or Weaponsmith or Sawyer or Craftsman or Tailor or Jeweller or Minter;

    public static bool IsValid(string code) => System.Array.IndexOf(All, code) >= 0;

    /// <summary>Jobs that keep their own tools, weapons and supplies in their inventory, so couriers leave it alone.</summary>
    public static bool CarriesOwnLoad(string? code) => code is Builder or Forester or Farmer or Guard or Potter or Cook or Prospector or Miner or Quarrier or Smelter or Blacksmith or Armourer or Weaponsmith or Sawyer or Craftsman or Tailor or Jeweller or Healer or Explorer or Gatherer or Hunter or Butcher or Minter or Priest or Researcher or ChickenHerder or Swineherd or Shepherd or Goatherd or Herdsman or Tanner or Beekeeper or Undertaker;
}
