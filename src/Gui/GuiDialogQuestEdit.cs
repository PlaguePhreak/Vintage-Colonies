using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The quest editor's window for one quest: its name, description, icon, shape, the quests it needs, its
/// switches, up to four tasks and three rewards. Nothing reaches the server until the book is saved.
/// </summary>
public class GuiDialogQuestEdit : GuiDialog
{
    const double Width = 620;
    const int TaskRows = 4, RewardRows = 3;

    /// <summary>Every kind of task the game can check, in the order the list shows them.</summary>
    public static readonly string[] TaskTypes =
    {
        "", ObjectiveTypes.Obtain, ObjectiveTypes.Deliver, ObjectiveTypes.Stock, ObjectiveTypes.Holds, ObjectiveTypes.Meal, ObjectiveTypes.BlockNear,
        ObjectiveTypes.EntityNear, ObjectiveTypes.Room, ObjectiveTypes.Mounted, ObjectiveTypes.Depth, ObjectiveTypes.Break, ObjectiveTypes.Place,
        ObjectiveTypes.Kill, ObjectiveTypes.Building, ObjectiveTypes.Buildings, ObjectiveTypes.Settlers, ObjectiveTypes.Job, ObjectiveTypes.Research,
        ObjectiveTypes.Happiness, ObjectiveTypes.AverageLevel, ObjectiveTypes.WorkHuts, ObjectiveTypes.TopSkill, ObjectiveTypes.Tour, ObjectiveTypes.Stat,
        ObjectiveTypes.Event, ObjectiveTypes.Age, ObjectiveTypes.Land, ObjectiveTypes.Couples, ObjectiveTypes.Children, ObjectiveTypes.Members,
        ObjectiveTypes.Camp, ObjectiveTypes.Colony,
    };

    static readonly string[] Shapes = { QuestShapes.Square, QuestShapes.Round, QuestShapes.Star, QuestShapes.Hexagon };

    readonly QuestDef quest;
    readonly Action<QuestDef> onSaved;
    readonly Action<QuestDef> onDelete;
    readonly Action<QuestDef> onCopy;

    // What the boxes hold while the window is open.
    string title, desc, icon, shape, needs, mod;
    bool any, soon, repeat, secret;
    readonly string[] taskType = new string[TaskRows], taskCode = new string[TaskRows];
    readonly int[] taskCount = new int[TaskRows], taskLevel = new int[TaskRows];
    readonly string[] rewardCode = new string[RewardRows];
    readonly int[] rewardCount = new int[RewardRows];

    public override string? ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.25;
    public override bool PrefersUngrabbedMouse => true;

    public GuiDialogQuestEdit(ICoreClientAPI capi, QuestDef quest, Action<QuestDef> onSaved, Action<QuestDef> onDelete, Action<QuestDef> onCopy) : base(capi)
    {
        this.quest = quest;
        this.onSaved = onSaved;
        this.onDelete = onDelete;
        this.onCopy = onCopy;
        title = quest.Title;
        desc = quest.Desc;
        icon = quest.Icon;
        shape = quest.Shape;
        needs = string.Join(", ", quest.Requires);
        mod = quest.Mod;
        any = quest.RequiresAny;
        soon = quest.Soon;
        repeat = quest.Repeatable;
        secret = quest.Secret;
        for (int i = 0; i < TaskRows; i++)
        {
            var o = i < quest.Objectives.Count ? quest.Objectives[i] : null;
            taskType[i] = o?.Type ?? "";
            taskCode[i] = o?.Code ?? "";
            taskCount[i] = o?.Count ?? 0;
            taskLevel[i] = o?.Level ?? 0;
        }
        for (int i = 0; i < RewardRows; i++)
        {
            var r = i < quest.Rewards.Count ? quest.Rewards[i] : null;
            rewardCode[i] = r?.Code ?? "";
            rewardCount[i] = r?.Count ?? 0;
        }
        Compose();
    }

    public bool Covers(int x, int y) => IsOpened() && SingleComposer?.Bounds.PointInside(x, y) == true;

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        var composer = capi.Gui.CreateCompo("createacolony-questedit", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("createacolony:questedit-title", quest.Code), () => TryClose())
            .BeginChildElements(bgBounds);

        const double label = 110, row = 32;
        double y = 30;
        var font = GuiKit.Body();
        var detail = GuiKit.Detail();

        composer.AddStaticText(Lang.Get("createacolony:questedit-name"), font, ElementBounds.Fixed(0, y + 4, label, 24));
        composer.AddTextInput(ElementBounds.Fixed(label, y, Width - label, 28), t => title = t, detail, "title");
        y += row;
        composer.AddStaticText(Lang.Get("createacolony:questedit-desc"), font, ElementBounds.Fixed(0, y + 4, label, 24));
        composer.AddTextArea(ElementBounds.Fixed(label, y, Width - label, 96), t => desc = t, detail, "desc");
        y += 104;
        composer.AddStaticText(Lang.Get("createacolony:questedit-icon"), font, ElementBounds.Fixed(0, y + 4, label, 24));
        composer.AddTextInput(ElementBounds.Fixed(label, y, 250, 28), t => icon = t, detail, "icon");
        composer.AddSmallButton(Lang.Get("createacolony:questedit-held"), () => UseHeld(-1), ElementBounds.Fixed(label + 258, y, 110, 28));
        composer.AddDropDown(Shapes, Shapes.Select(s => Lang.Get("createacolony:questedit-shape-" + s)).ToArray(), Math.Max(0, Array.IndexOf(Shapes, shape)),
            (code, _) => shape = code, ElementBounds.Fixed(Width - 130, y, 130, 28), "shape");
        y += row;
        composer.AddStaticText(Lang.Get("createacolony:questedit-needs"), font, ElementBounds.Fixed(0, y + 4, label, 24));
        composer.AddTextInput(ElementBounds.Fixed(label, y, Width - label, 28), t => needs = t, detail, "needs");
        y += row;
        composer.AddStaticText(Lang.Get("createacolony:questedit-mod"), font, ElementBounds.Fixed(0, y + 4, label, 24));
        composer.AddTextInput(ElementBounds.Fixed(label, y, 200, 28), t => mod = t, detail, "mod");
        y += row + 4;

        // The switches, two to a row.
        (string key, bool on, Action<bool> set)[] switches =
        {
            ("repeat", repeat, v => repeat = v), ("any", any, v => any = v), ("soon", soon, v => soon = v), ("secret", secret, v => secret = v),
        };
        for (int i = 0; i < switches.Length; i++)
        {
            double sx = i % 2 * (Width / 2);
            var (key, _, set) = switches[i];
            composer.AddSwitch(set, ElementBounds.Fixed(sx, y, 26, 26), "sw-" + key, 24);
            composer.AddStaticText(Lang.Get("createacolony:questedit-" + key), font, ElementBounds.Fixed(sx + 34, y + 3, Width / 2 - 40, 24));
            if (i % 2 == 1) y += 30;
        }
        y += 6;

        composer.AddStaticText(Lang.Get("createacolony:questedit-tasks"), GuiKit.Heading(), ElementBounds.Fixed(0, y, Width, 24));
        y += 26;
        composer.AddStaticText(Lang.Get("createacolony:questedit-tasks-hint"), GuiKit.Muted(), ElementBounds.Fixed(0, y, Width, 20));
        y += 22;
        string[] typeNames = TaskTypes.Select(t => t.Length == 0 ? Lang.Get("createacolony:questedit-task-none") : Lang.Get("createacolony:questedit-task-" + t)).ToArray();
        for (int i = 0; i < TaskRows; i++)
        {
            int index = i;
            composer.AddDropDown(TaskTypes, typeNames, Math.Max(0, Array.IndexOf(TaskTypes, taskType[i])), (code, _) => taskType[index] = code, ElementBounds.Fixed(0, y, 190, 28), "tasktype" + i);
            composer.AddTextInput(ElementBounds.Fixed(196, y, 230, 28), t => taskCode[index] = t, detail, "taskcode" + i);
            composer.AddNumberInput(ElementBounds.Fixed(432, y, 60, 28), t => taskCount[index] = ParseInt(t), detail, "taskcount" + i);
            composer.AddNumberInput(ElementBounds.Fixed(498, y, 50, 28), t => taskLevel[index] = ParseInt(t), detail, "tasklevel" + i);
            composer.AddSmallButton(Lang.Get("createacolony:questedit-held-short"), () => UseHeld(index), ElementBounds.Fixed(554, y, Width - 554, 28));
            y += row;
        }

        composer.AddStaticText(Lang.Get("createacolony:questedit-rewards"), GuiKit.Heading(), ElementBounds.Fixed(0, y + 4, Width, 24));
        y += 30;
        for (int i = 0; i < RewardRows; i++)
        {
            int index = i;
            composer.AddTextInput(ElementBounds.Fixed(0, y, 426, 28), t => rewardCode[index] = t, detail, "rewardcode" + i);
            composer.AddNumberInput(ElementBounds.Fixed(432, y, 60, 28), t => rewardCount[index] = ParseInt(t), detail, "rewardcount" + i);
            composer.AddSmallButton(Lang.Get("createacolony:questedit-held-short"), () => UseHeld(100 + index), ElementBounds.Fixed(554, y, Width - 554, 28));
            y += row;
        }
        y += 8;

        composer.AddSmallButton(Lang.Get("createacolony:questedit-save"), Save, ElementBounds.Fixed(0, y, 130, 30));
        composer.AddSmallButton(Lang.Get("createacolony:questedit-copy"), () => { Apply(); TryClose(); onCopy(quest); return true; }, ElementBounds.Fixed(140, y, 130, 30));
        composer.AddSmallButton(Lang.Get("createacolony:questedit-delete"), () => { TryClose(); onDelete(quest); return true; }, ElementBounds.Fixed(280, y, 130, 30));
        composer.AddSmallButton(Lang.Get("createacolony:questedit-cancel"), () => TryClose(), ElementBounds.Fixed(Width - 130, y, 130, 30));

        SingleComposer = composer.EndChildElements().Compose();
        Fill();
    }

    /// <summary>Puts the values into the boxes.</summary>
    void Fill()
    {
        var c = SingleComposer;
        c.GetTextInput("title")?.SetValue(title);
        c.GetTextArea("desc")?.SetValue(desc);
        c.GetTextInput("icon")?.SetValue(icon);
        c.GetTextInput("needs")?.SetValue(needs);
        c.GetTextInput("mod")?.SetValue(mod);
        c.GetSwitch("sw-repeat")?.SetValue(repeat);
        c.GetSwitch("sw-any")?.SetValue(any);
        c.GetSwitch("sw-soon")?.SetValue(soon);
        c.GetSwitch("sw-secret")?.SetValue(secret);
        for (int i = 0; i < TaskRows; i++)
        {
            c.GetTextInput("taskcode" + i)?.SetValue(taskCode[i]);
            c.GetNumberInput("taskcount" + i)?.SetValue(taskCount[i] > 0 ? taskCount[i].ToString() : "");
            c.GetNumberInput("tasklevel" + i)?.SetValue(taskLevel[i] > 0 ? taskLevel[i].ToString() : "");
        }
        for (int i = 0; i < RewardRows; i++)
        {
            c.GetTextInput("rewardcode" + i)?.SetValue(rewardCode[i]);
            c.GetNumberInput("rewardcount" + i)?.SetValue(rewardCount[i] > 0 ? rewardCount[i].ToString() : "");
        }
    }

    static int ParseInt(string text) => int.TryParse(text, out int n) ? Math.Max(0, n) : 0;

    /// <summary>Fills a box with the code of the item in the player's hand: -1 the icon, 0 to 3 a task, 100 and up a reward.</summary>
    bool UseHeld(int target)
    {
        var held = capi.World.Player?.InventoryManager.ActiveHotbarSlot?.Itemstack;
        if (held == null)
        {
            capi.TriggerIngameError(this, "nohelditem", Lang.Get("createacolony:questedit-nothing-held"));
            return true;
        }
        string code = held.Collectible.Code.ToString();
        if (target < 0) SingleComposer.GetTextInput("icon")?.SetValue(icon = code);
        else if (target < 100)
        {
            SingleComposer.GetTextInput("taskcode" + target)?.SetValue(taskCode[target] = code);
            if (taskType[target].Length == 0)
            {
                taskType[target] = ObjectiveTypes.Obtain;
                SingleComposer.GetDropDown("tasktype" + target)?.SetSelectedValue(ObjectiveTypes.Obtain);
            }
        }
        else SingleComposer.GetTextInput("rewardcode" + (target - 100))?.SetValue(rewardCode[target - 100] = code);
        return true;
    }

    /// <summary>Writes the boxes back into the quest.</summary>
    void Apply()
    {
        quest.Title = title.Trim();
        quest.Desc = desc.Trim();
        quest.Icon = icon.Trim();
        quest.Shape = shape;
        quest.Mod = mod.Trim();
        quest.RequiresAny = any;
        quest.Soon = soon;
        quest.Repeatable = repeat;
        quest.Secret = secret;
        quest.Requires = needs.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(c => c != quest.Code).Distinct().ToList();
        quest.NoLine.RemoveAll(c => !quest.Requires.Contains(c));
        quest.Objectives = new List<QuestObjectiveDef>();
        for (int i = 0; i < TaskRows; i++)
        {
            if (taskType[i].Length == 0) continue;
            quest.Objectives.Add(new QuestObjectiveDef { Type = taskType[i], Code = taskCode[i].Trim(), Count = taskCount[i], Level = taskLevel[i] });
        }
        quest.Rewards = new List<QuestRewardDef>();
        for (int i = 0; i < RewardRows; i++)
        {
            if (rewardCode[i].Trim().Length == 0) continue;
            quest.Rewards.Add(new QuestRewardDef { Code = rewardCode[i].Trim(), Count = Math.Max(1, rewardCount[i]) });
        }
    }

    bool Save()
    {
        Apply();
        TryClose();
        onSaved(quest);
        return true;
    }
}

/// <summary>A one-line question for the quest editor: a chapter's or a group's name.</summary>
public class GuiDialogQuestName : GuiDialog
{
    string text;

    public override string? ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.25;
    public override bool PrefersUngrabbedMouse => true;

    public GuiDialogQuestName(ICoreClientAPI capi, string heading, string initial, Action<string> onOk) : base(capi)
    {
        text = initial;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        SingleComposer = capi.Gui.CreateCompo("createacolony-questname", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(heading, () => TryClose())
            .BeginChildElements(bgBounds)
            .AddTextInput(ElementBounds.Fixed(0, 30, 360, 28), t => text = t, GuiKit.Body(), "name")
            .AddSmallButton(Lang.Get("createacolony:questedit-save"), () =>
            {
                TryClose();
                if (text.Trim().Length > 0) onOk(text.Trim());
                return true;
            }, ElementBounds.Fixed(0, 68, 130, 30))
            .AddSmallButton(Lang.Get("createacolony:questedit-cancel"), () => TryClose(), ElementBounds.Fixed(230, 68, 130, 30))
            .EndChildElements().Compose();
        SingleComposer.GetTextInput("name")?.SetValue(initial);
    }
}

/// <summary>The quest book's settings: the player's own choices, kept on their computer.</summary>
public class GuiDialogQuestSettings : GuiDialog
{
    public override string? ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.25;
    public override bool PrefersUngrabbedMouse => true;

    public GuiDialogQuestSettings(ICoreClientAPI capi, bool showLocked, Action<bool> onShowLocked) : base(capi)
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        SingleComposer = capi.Gui.CreateCompo("createacolony-questsettings", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("createacolony:questbook-settings-title"), () => TryClose())
            .BeginChildElements(bgBounds)
            .AddSwitch(on => onShowLocked(on), ElementBounds.Fixed(0, 32, 26, 26), "showlocked", 24)
            .AddStaticText(Lang.Get("createacolony:questbook-show-locked"), GuiKit.Body(), ElementBounds.Fixed(36, 35, 330, 24))
            .AddStaticText(Lang.Get("createacolony:questbook-show-locked-hint"), GuiKit.Muted(), ElementBounds.Fixed(0, 68, 366, 44))
            .AddSmallButton(Lang.Get("createacolony:questbook-settings-close"), () => TryClose(), ElementBounds.Fixed(236, 118, 130, 28))
            .EndChildElements().Compose();
        SingleComposer.GetSwitch("showlocked")?.SetValue(showLocked);
    }
}
