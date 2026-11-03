using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Config;

namespace CreateAColony.Network;

/// <summary>The Research tab of the Archive's window (ColonyManager.Research.cs).</summary>
public partial class ColonyNetwork
{
    void AddResearch(HutSnapshot snapshot, Colony colony, Building archive)
    {
        var m = Manager;
        snapshot.IsArchive = true;
        snapshot.KnowledgeTokens = colony.KnowledgeTokens;
        snapshot.ArchiveTier = m.ArchiveTier(archive);
        snapshot.ResearchSlots = m.ResearchSlots(archive);
        snapshot.ResearchRunning = m.Running(colony, archive).Count;
        snapshot.ResearchSpeed = (int)System.Math.Round(m.ArchiveSpeed(colony, archive) * 100);
        var room = m.RoomOf(colony, archive);
        if (!room.Table) snapshot.ResearchNotes.Add(Lang.Get("createacolony:archive-no-table"));
        if (!room.Shelf) snapshot.ResearchNotes.Add(Lang.Get("createacolony:archive-no-shelf"));
        if (!room.Light) snapshot.ResearchNotes.Add(Lang.Get("createacolony:archive-no-light"));
        if (room.Noisy > 0) snapshot.ResearchNotes.Add(Lang.Get("createacolony:archive-noisy", room.Noisy));

        foreach (var def in m.ResearchDefs())
        {
            var view = new ResearchView { Code = def.Code, Branch = def.Branch, Tier = def.Tier, Cost = m.CostText(def), PointsNeeded = def.Points };
            var progress = colony.Research.Find(p => p.Code == def.Code);
            if (m.Researched(colony, def.Code)) view.State = 4;
            else if (progress != null)
            {
                view.Points = (int)progress.Points;
                bool running = m.RunsSomewhere(colony, progress);
                view.State = running ? 3 : 2;
                view.Elsewhere = running && progress.ArchiveId != archive.BuildingId;
                if (progress.Running && !progress.Paid && progress.ArchiveId == archive.BuildingId)
                {
                    string missing = m.MissingText(colony, archive, def);
                    view.Note = missing.Length > 0 ? Lang.Get("createacolony:research-waiting-for", missing) : Lang.Get("createacolony:research-paying");
                }
            }
            else
            {
                var blockers = m.ResearchBlockers(colony, def);
                view.State = blockers.Count == 0 ? 1 : 0;
                view.Note = string.Join(", ", blockers);
            }
            snapshot.Research.Add(view);
        }
    }

    string? ResearchAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        if (packet.Action == ColonyAction.PauseResearch)
        {
            Manager.PauseResearch(colony, packet.Text);
            return null;
        }
        var archive = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (archive == null) return "createacolony:bad-building";
        return Manager.StartResearch(colony, archive, packet.Text);
    }
}
