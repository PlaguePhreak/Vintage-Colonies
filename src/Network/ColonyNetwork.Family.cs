using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Config;

namespace CreateAColony.Network;

/// <summary>The family lists of the settler window and of a Residence's Residents tab.</summary>
public partial class ColonyNetwork
{
    FamilyMemberView FamilyView(CitizenRecord c, string relation) => new()
    {
        CitizenId = c.CitizenId,
        Name = c.Name,
        Relation = relation.Length == 0 ? "" : Lang.Get("createacolony:relation-" + relation),
        Job = ColonyManager.JobLabel(c),
        Child = c.IsChild,
        GrowsUpHours = Manager.HoursToGrowUp(c),
        Alive = c.Alive
    };

    /// <summary>A settler's partner, parents, children and brothers and sisters, in that order.</summary>
    List<FamilyMemberView> FamilyOf(Colony colony, CitizenRecord citizen)
    {
        var list = new List<FamilyMemberView>();
        if (Manager.PartnerOf(colony, citizen) is { } partner) list.Add(FamilyView(partner, "partner"));
        foreach (var p in Manager.ParentsOf(colony, citizen)) list.Add(FamilyView(p, p.Sex == "female" ? "mother" : "father"));
        foreach (var c in Manager.ChildrenOf(colony, citizen)) list.Add(FamilyView(c, c.Sex == "female" ? "daughter" : "son"));
        foreach (var s in Manager.SiblingsOf(colony, citizen)) list.Add(FamilyView(s, s.Sex == "female" ? "sister" : "brother"));
        return list;
    }

    /// <summary>For a couple, whether a child may come and if not why; empty for singles and children.</summary>
    string FamilyNote(Colony colony, CitizenRecord citizen)
    {
        if (citizen.IsChild || citizen.Visitor || Manager.PartnerOf(colony, citizen) is not { Alive: true } partner) return "";
        string? blocker = Manager.ChildBlocker(colony, citizen, partner, sapi!.World.Calendar.TotalDays);
        return Lang.Get(blocker ?? "createacolony:family-may-have-child");
    }

    void AddResidence(HutSnapshot snapshot, Colony colony, Building residence)
    {
        snapshot.IsResidence = true;
        var (residents, beds, free) = Manager.ResidenceInfo(colony, residence);
        snapshot.ResidenceBeds = beds;
        snapshot.ResidenceFreeBeds = free;
        foreach (var house in Manager.Households(colony, residents))
        {
            var head = house[0];
            var view = new HouseholdView { Surname = head.Surname, Note = FamilyNote(colony, head) };
            foreach (var c in house)
            {
                string relation = c == head ? "" : c.IsChild ? (c.Sex == "female" ? "daughter" : "son") : "partner";
                view.Members.Add(FamilyView(c, relation));
            }
            snapshot.Households.Add(view);
        }
        // Everyone grown who lives somewhere else (children move with their parents), for the Choose residents page.
        var boxes = Manager.ResidenceBoxes(colony);
        foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild && !residents.Contains(c)).OrderBy(c => c.Name))
        {
            var view = FamilyView(c, "");
            view.Relation = c.HomeBedPos == null ? "" : Manager.ResidenceAt(colony, c.HomeBedPos, boxes) is { } home ? ColonyManager.BuildingName(home) : Lang.Get("createacolony:residence-elsewhere");
            snapshot.ResidentChoices.Add(view);
        }
    }

    /// <summary>Moves a settler, with their partner and children, into a Residence or out of it.</summary>
    string? ResidenceAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var residence = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (residence == null || !ColonyManager.IsHome(residence.Type)) return "createacolony:bad-building";
        var citizen = colony.Citizens.Find(c => c.CitizenId == packet.CitizenId);
        if (citizen == null || !citizen.Alive || citizen.Visitor) return "createacolony:bad-citizen";
        return packet.Action == ColonyAction.MoveInResidence
            ? Manager.MoveIntoResidence(colony, residence, citizen)
            : Manager.MoveOutOfResidence(colony, residence, citizen);
    }
}
