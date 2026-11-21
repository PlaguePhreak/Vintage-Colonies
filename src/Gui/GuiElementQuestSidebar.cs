using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CreateAColony.Gui;

/// <summary>What the quest book's side list remembers while the window redraws: folded groups and the scroll place.</summary>
public class QuestSidebarState
{
    public readonly HashSet<string> Folded = new();
    public double Scroll;
}

/// <summary>One line of the side list: a group heading, or a chapter under it.</summary>
public class QuestSidebarRow
{
    public bool IsGroup;
    public string GroupCode = "";
    public string ChapterCode = "";
    public string Name = "";
    public int Done, Total;
    /// <summary>How many quests here have a reward waiting; shows the red "!".</summary>
    public int Waiting;
    public bool Selected;
    public ItemStack? Icon;
}

/// <summary>
/// The quest book's side list, as in FTB Quests: every group as a heading that folds, its chapters under it with
/// an icon, how many quests are done, and a red "!" when a reward is waiting. Clicking a heading folds or unfolds
/// it, clicking a chapter opens its map, the mouse wheel scrolls.
/// </summary>
public class GuiElementQuestSidebar : GuiElement
{
    const double GroupHeight = 28, ChapterHeight = 26, IconSize = 18;

    readonly List<QuestSidebarRow> rows;
    readonly QuestSidebarState state;
    readonly Action<QuestSidebarRow> onPick;
    LoadedTexture texture;
    bool dirty = true;
    QuestSidebarRow? hovered;
    bool pressed;

    public GuiElementQuestSidebar(ICoreClientAPI capi, ElementBounds bounds, List<QuestSidebarRow> rows, QuestSidebarState state, Action<QuestSidebarRow> onPick) : base(capi, bounds)
    {
        this.rows = rows;
        this.state = state;
        this.onPick = onPick;
        texture = new LoadedTexture(capi);
    }

    /// <summary>The rows on show (chapters of folded groups are left out), each with its top and height in screen pixels before scrolling.</summary>
    List<(QuestSidebarRow Row, double Top, double Height)> Layout()
    {
        var list = new List<(QuestSidebarRow, double, double)>();
        double y = scaled(4);
        foreach (var row in rows)
        {
            if (!row.IsGroup && state.Folded.Contains(row.GroupCode)) continue;
            double h = scaled(row.IsGroup ? GroupHeight : ChapterHeight);
            list.Add((row, y, h));
            y += h;
        }
        return list;
    }

    double ContentHeight(List<(QuestSidebarRow Row, double Top, double Height)> layout) =>
        layout.Count == 0 ? 0 : layout[^1].Top + layout[^1].Height + scaled(4);

    void ClampScroll(List<(QuestSidebarRow Row, double Top, double Height)> layout)
    {
        double max = Math.Max(0, ContentHeight(layout) - Bounds.InnerHeight);
        state.Scroll = Math.Clamp(state.Scroll, 0, max / scaled(1));
    }

    QuestSidebarRow? RowAt(double y)
    {
        var layout = Layout();
        double scroll = scaled(state.Scroll);
        foreach (var (row, top, height) in layout)
        {
            if (y >= top - scroll && y < top - scroll + height) return row;
        }
        return null;
    }

    // ---- Mouse ----------------------------------------------------------------------------

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (args.Button == EnumMouseButton.Left) pressed = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (!pressed) return;
        pressed = false;
        if (!Bounds.PointInside(args.X, args.Y)) return;
        var row = RowAt(args.Y - Bounds.absY);
        if (row == null) return;
        if (row.IsGroup)
        {
            if (!state.Folded.Remove(row.GroupCode)) state.Folded.Add(row.GroupCode);
            dirty = true;
        }
        onPick(row);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        var under = Bounds.PointInside(args.X, args.Y) ? RowAt(args.Y - Bounds.absY) : null;
        if (under != hovered)
        {
            hovered = under;
            dirty = true;
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;
        state.Scroll -= args.delta * ChapterHeight * 2;
        dirty = true;
        args.SetHandled(true);
    }

    // ---- Drawing --------------------------------------------------------------------------

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        dirty = true;
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (dirty) Redraw();
        if (texture.TextureId != 0) api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, Bounds.renderX, Bounds.renderY, Bounds.InnerWidth, Bounds.InnerHeight);

        // The chapter icons, clipped to the list.
        api.Render.PushScissor(Bounds);
        var layout = Layout();
        double scroll = scaled(state.Scroll);
        float size = (float)scaled(IconSize);
        foreach (var (row, top, height) in layout)
        {
            if (row.IsGroup || row.Icon == null) continue;
            double cy = top - scroll + height / 2;
            if (cy < -height || cy > Bounds.InnerHeight + height) continue;
            // Clipped to its own little box, so a long item cannot spill over the lines below.
            double box = scaled(22);
            if (!GuiKit.ClipTo(api, Bounds, Bounds.renderX + scaled(22) - box / 2, Bounds.renderY + cy - box / 2, box, box)) continue;
            api.Render.RenderItemstackToGui(new DummySlot(row.Icon), Bounds.renderX + scaled(22), Bounds.renderY + cy, 100, size, ColorUtil.WhiteArgb, true, false, false);
        }
        api.Render.PopScissor();
    }

    void Redraw()
    {
        dirty = false;
        int width = Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth)), height = Math.Max(1, (int)Math.Ceiling(Bounds.InnerHeight));
        using var surface = new ImageSurface(Format.Argb32, width, height);
        using (var ctx = new Context(surface)) Draw(ctx, width, height);
        api.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
    }

    void Draw(Context ctx, int width, int height)
    {
        ctx.SetSourceRGBA(0.11, 0.10, 0.09, 1);
        ctx.Rectangle(0, 0, width, height);
        ctx.Fill();

        var layout = Layout();
        ClampScroll(layout);
        double scroll = scaled(state.Scroll);
        double pad = scaled(8);

        foreach (var (row, top, rowHeight) in layout)
        {
            double y = top - scroll;
            if (y + rowHeight < 0 || y > height) continue;
            bool isHovered = row == hovered;
            double mid = y + rowHeight / 2;

            if (row.IsGroup)
            {
                ctx.SetSourceRGBA(1, 1, 1, isHovered ? 0.10 : 0.05);
                ctx.Rectangle(0, y + 1, width, rowHeight - 2);
                ctx.Fill();
                // The fold arrow: pointing down when open, right when folded.
                bool folded = state.Folded.Contains(row.GroupCode);
                double a = scaled(4.5), ax = pad + a, ay = mid;
                ctx.NewPath();
                if (folded) { ctx.MoveTo(ax - a * 0.6, ay - a); ctx.LineTo(ax + a * 0.8, ay); ctx.LineTo(ax - a * 0.6, ay + a); }
                else { ctx.MoveTo(ax - a, ay - a * 0.6); ctx.LineTo(ax + a, ay - a * 0.6); ctx.LineTo(ax, ay + a * 0.8); }
                ctx.ClosePath();
                ctx.SetSourceRGBA(0.85, 0.8, 0.7, 1);
                ctx.Fill();

                ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
                ctx.SetFontSize(scaled(15));
                double right = width - pad;
                if (row.Waiting > 0)
                {
                    double br = scaled(7);
                    GuiElementQuestMap.Badge(ctx, right - br, mid, br);
                    right -= br * 2 + scaled(4);
                }
                ctx.SetSourceRGBA(0.95, 0.9, 0.8, 1);
                ShowFitted(ctx, row.Name, pad + scaled(16), mid, right - (pad + scaled(16)));
                continue;
            }

            if (row.Selected)
            {
                ctx.SetSourceRGBA(0.95, 0.8, 0.45, 0.22);
                ctx.Rectangle(0, y, width, rowHeight);
                ctx.Fill();
                ctx.SetSourceRGBA(0.95, 0.8, 0.45, 1);
                ctx.Rectangle(0, y, scaled(3), rowHeight);
                ctx.Fill();
            }
            else if (isHovered)
            {
                ctx.SetSourceRGBA(1, 1, 1, 0.08);
                ctx.Rectangle(0, y, width, rowHeight);
                ctx.Fill();
            }

            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(scaled(14));
            double rightEdge = width - pad;
            if (row.Waiting > 0)
            {
                double br = scaled(7);
                GuiElementQuestMap.Badge(ctx, rightEdge - br, mid, br);
                rightEdge -= br * 2 + scaled(4);
            }
            else if (row.Total > 0)
            {
                // How many are done, small and to the right.
                string count = row.Done + "/" + row.Total;
                ctx.SetFontSize(scaled(12));
                double cw = ctx.TextExtents(count).XAdvance;
                bool all = row.Done >= row.Total;
                ctx.SetSourceRGBA(all ? 0.5 : 0.62, all ? 0.85 : 0.6, all ? 0.45 : 0.56, 1);
                ctx.MoveTo(rightEdge - cw, mid + ctx.FontExtents.Ascent / 2 - ctx.FontExtents.Descent / 2);
                ctx.ShowText(count);
                rightEdge -= cw + scaled(6);
                ctx.SetFontSize(scaled(14));
            }
            bool finished = row.Total > 0 && row.Done >= row.Total;
            if (row.Selected) ctx.SetSourceRGBA(1, 1, 1, 1);
            else if (finished) ctx.SetSourceRGBA(0.55, 0.85, 0.5, 1);
            else ctx.SetSourceRGBA(0.86, 0.84, 0.8, 1);
            double textX = scaled(36);
            ShowFitted(ctx, row.Name, textX, mid, rightEdge - textX);
        }

        // A thin bar on the right when the list is longer than the box.
        double content = ContentHeight(layout);
        if (content > height)
        {
            double barHeight = Math.Max(scaled(20), height * height / content);
            double barTop = (height - barHeight) * (scroll / Math.Max(1, content - height));
            ctx.SetSourceRGBA(1, 1, 1, 0.25);
            ctx.Rectangle(width - scaled(3), barTop, scaled(3), barHeight);
            ctx.Fill();
        }
    }

    /// <summary>Writes text centred on a line, cut short with "…" when it is wider than the room it has.</summary>
    static void ShowFitted(Context ctx, string text, double x, double midY, double room)
    {
        string shown = text;
        if (ctx.TextExtents(shown).XAdvance > room)
        {
            while (shown.Length > 1 && ctx.TextExtents(shown + "…").XAdvance > room) shown = shown[..^1];
            shown += "…";
        }
        ctx.MoveTo(x, midY + ctx.FontExtents.Ascent / 2 - ctx.FontExtents.Descent / 2);
        ctx.ShowText(shown);
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
    }
}
