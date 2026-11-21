using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using CreateAColony.Network;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Gui;

/// <summary>Where a chapter's map is scrolled to, kept by the window so it stays put when the window redraws.</summary>
public class QuestMapView
{
    public string Chapter = "";
    /// <summary>How far the map is pushed from its starting place, in unscaled pixels.</summary>
    public double PanX, PanY;
    /// <summary>How far the map is zoomed in: 1 is full size, 0.4 is far out.</summary>
    public double Zoom = 1;
}

/// <summary>
/// A chapter's map, FTB Quests style: each quest a big icon in a solid frame (round, square, star or hexagon),
/// joined by thick lines from the quests it needs, coloured by where it stands (grey with a padlock locked,
/// white open, blue counting, gold ready to claim, green done). The chapter's name stands across the top.
/// Dragging moves the map, the mouse wheel zooms, clicking an icon picks the quest (clicking empty map lets it go).
/// The shapes and lines are drawn into a texture; the item icons are drawn on top each frame.
/// </summary>
public class GuiElementQuestMap : GuiElement
{
    /// <summary>Grid cell size and the icon's circle, unscaled.</summary>
    public const double CellSize = 96, NodeRadius = 31, IconSize = 38;
    public const double MinZoom = 0.4, MaxZoom = 1.6;

    readonly List<QuestDef> quests;
    readonly Dictionary<string, QuestStateView> states;
    readonly Dictionary<string, ItemStack?> icons;
    readonly QuestMapView view;
    readonly Action<string?> onSelect;
    readonly string title;
    string? selected;
    QuestDef? hovered;
    LoadedTexture texture;
    /// <summary>Padlocks, ticks, reward badges and the hover name: drawn over the item icons, so an icon never hides them.</summary>
    LoadedTexture overlay;
    bool dirty = true;
    bool dragging, moved;
    int prevMouseX, prevMouseY, downX, downY;

    public GuiElementQuestMap(ICoreClientAPI capi, ElementBounds bounds, List<QuestDef> quests, Dictionary<string, QuestStateView> states,
        Dictionary<string, ItemStack?> icons, QuestMapView view, string? selected, Action<string?> onSelect, string title = "") : base(capi, bounds)
    {
        this.title = title;
        this.quests = quests;
        this.states = states;
        this.icons = icons;
        this.view = view;
        this.selected = selected;
        this.onSelect = onSelect;
        texture = new LoadedTexture(capi);
        overlay = new LoadedTexture(capi);
    }

    public QuestStatus StatusOf(QuestDef q) => states.TryGetValue(q.Code, out var s) ? (QuestStatus)s.Status : QuestStatus.Locked;

    /// <summary>Secret quests stay off the map until the quests they need are done.</summary>
    bool Shown(QuestDef q) => !q.Secret || StatusOf(q) != QuestStatus.Locked;

    /// <summary>A quest's centre inside the element, in screen pixels.</summary>
    (double X, double Y) Centre(QuestDef q)
    {
        double k = Scale;
        return (Bounds.InnerWidth / 2 + (q.X * CellSize + view.PanX) * k, Bounds.InnerHeight / 2 + (q.Y * CellSize + view.PanY) * k);
    }

    /// <summary>Screen pixels per map unit: the GUI scale times the zoom.</summary>
    double Scale => scaled(1) * Math.Clamp(view.Zoom, MinZoom, MaxZoom);

    /// <summary>Marks another quest as the chosen one (or none) without rebuilding the window.</summary>
    public void Select(string? code)
    {
        if (selected == code) return;
        selected = code;
        dirty = true;
    }

    /// <summary>Starts a new chapter's map with its quests in the middle of the box.</summary>
    public void CentreOnQuests(string chapter)
    {
        if (view.Chapter == chapter) return;
        view.Chapter = chapter;
        view.PanX = view.PanY = 0;
        view.Zoom = 1;
        var shown = quests.Where(Shown).ToList();
        if (shown.Count == 0) return;
        view.PanX = -(shown.Min(q => q.X) + shown.Max(q => q.X)) / 2.0 * CellSize;
        view.PanY = -(shown.Min(q => q.Y) + shown.Max(q => q.Y)) / 2.0 * CellSize;
        // Zoom out until the whole chapter fits, but never so far that the icons get tiny.
        double spanX = (shown.Max(q => q.X) - shown.Min(q => q.X) + 1.5) * CellSize;
        double spanY = (shown.Max(q => q.Y) - shown.Min(q => q.Y) + 2.0) * CellSize;
        if (Bounds.fixedWidth > 0 && Bounds.fixedHeight > 0)
        {
            view.Zoom = Math.Clamp(Math.Min(Bounds.fixedWidth / spanX, Bounds.fixedHeight / spanY), 0.6, 1);
        }
    }

    QuestDef? QuestAt(double x, double y)
    {
        double r = NodeRadius * Scale;
        foreach (var q in quests)
        {
            if (!Shown(q)) continue;
            var (cx, cy) = Centre(q);
            if (Math.Abs(x - cx) <= r && Math.Abs(y - cy) <= r) return q;
        }
        return null;
    }

    // ---- Mouse ----------------------------------------------------------------------------

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (args.Button != EnumMouseButton.Left) return;
        dragging = true;
        moved = false;
        prevMouseX = downX = args.X;
        prevMouseY = downY = args.Y;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (!dragging) return;
        dragging = false;
        if (moved || !Bounds.PointInside(args.X, args.Y)) return;
        var under = QuestAt(args.X - Bounds.absX, args.Y - Bounds.absY);
        selected = under?.Code;
        dirty = true;
        onSelect(selected);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging && !api.Input.MouseButton.Left) dragging = false;
        if (dragging)
        {
            if (Math.Abs(args.X - downX) > 4 || Math.Abs(args.Y - downY) > 4) moved = true;
            view.PanX += (args.X - prevMouseX) / Scale;
            view.PanY += (args.Y - prevMouseY) / Scale;
            prevMouseX = args.X;
            prevMouseY = args.Y;
            dirty = true;
        }
        var under = Bounds.PointInside(args.X, args.Y) ? QuestAt(args.X - Bounds.absX, args.Y - Bounds.absY) : null;
        if (under != hovered)
        {
            hovered = under;
            dirty = true;
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;
        // Zoom about the pointer, so what is under it stays under it.
        double mx = api.Input.MouseX - Bounds.absX - Bounds.InnerWidth / 2, my = api.Input.MouseY - Bounds.absY - Bounds.InnerHeight / 2;
        double before = Scale;
        double wx = mx / before - view.PanX, wy = my / before - view.PanY;
        view.Zoom = Math.Clamp(view.Zoom * (args.delta > 0 ? 1.15 : 1 / 1.15), MinZoom, MaxZoom);
        double after = Scale;
        view.PanX = mx / after - wx;
        view.PanY = my / after - wy;
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

        // The item icons. Each is clipped to its own frame, so a long item (a spear) cannot spill over the map.
        api.Render.PushScissor(Bounds);
        float size = (float)(IconSize * Scale);
        double margin = NodeRadius * Scale;
        double half = NodeRadius * Scale * 0.74;
        foreach (var q in quests)
        {
            if (!Shown(q) || !icons.TryGetValue(q.Code, out var stack) || stack == null) continue;
            var (cx, cy) = Centre(q);
            if (cx < -margin || cy < -margin || cx > Bounds.InnerWidth + margin || cy > Bounds.InnerHeight + margin) continue;
            if (!GuiKit.ClipTo(api, Bounds, Bounds.renderX + cx - half, Bounds.renderY + cy - half, half * 2, half * 2)) continue;
            int color = StatusOf(q) == QuestStatus.Locked ? ColorUtil.ColorFromRgba(110, 110, 110, 255) : ColorUtil.WhiteArgb;
            api.Render.RenderItemstackToGui(new DummySlot(stack), Bounds.renderX + cx, Bounds.renderY + cy, 100, size, color, true, false, false);
        }
        api.Render.PopScissor();

        if (overlay.TextureId != 0) api.Render.Render2DTexturePremultipliedAlpha(overlay.TextureId, Bounds.renderX, Bounds.renderY, Bounds.InnerWidth, Bounds.InnerHeight, 400);
    }

    void Redraw()
    {
        dirty = false;
        int width = Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth)), height = Math.Max(1, (int)Math.Ceiling(Bounds.InnerHeight));
        using var surface = new ImageSurface(Format.Argb32, width, height);
        using (var ctx = new Context(surface)) Draw(ctx, width, height);
        api.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
        using var top = new ImageSurface(Format.Argb32, width, height);
        using (var ctx = new Context(top)) DrawMarks(ctx, width, height);
        api.Gui.LoadOrUpdateCairoTexture(top, false, ref overlay);
    }

    static double[] StatusColor(QuestStatus status) => status switch
    {
        QuestStatus.Locked => new[] { 0.45, 0.45, 0.47 },
        QuestStatus.Available => new[] { 0.92, 0.92, 0.9 },
        QuestStatus.Active => new[] { 0.5, 0.72, 1.0 },
        QuestStatus.Complete => new[] { 1.0, 0.82, 0.25 },
        _ => new[] { 0.5, 0.85, 0.45 }
    };

    /// <summary>The solid colour inside a quest's frame, tinted by where the quest stands.</summary>
    static double[] FillColor(QuestStatus status) => status switch
    {
        QuestStatus.Locked => new[] { 0.15, 0.15, 0.16 },
        QuestStatus.Available => new[] { 0.27, 0.25, 0.22 },
        QuestStatus.Active => new[] { 0.16, 0.22, 0.32 },
        QuestStatus.Complete => new[] { 0.36, 0.29, 0.10 },
        _ => new[] { 0.15, 0.27, 0.15 }
    };

    void Draw(Context ctx, int width, int height)
    {
        // Parchment-dark background with a faint grid, so dragging shows movement.
        ctx.SetSourceRGBA(0.13, 0.12, 0.11, 1);
        ctx.Rectangle(0, 0, width, height);
        ctx.Fill();
        double k = Scale;
        double cell = CellSize * k / 2;
        double ox = Bounds.InnerWidth / 2 + view.PanX * k, oy = Bounds.InnerHeight / 2 + view.PanY * k;
        ctx.SetSourceRGBA(1, 1, 1, 0.035);
        ctx.LineWidth = 1;
        for (double x = ((ox % cell) + cell) % cell; x < width; x += cell) { ctx.MoveTo(x, 0); ctx.LineTo(x, height); }
        for (double y = ((oy % cell) + cell) % cell; y < height; y += cell) { ctx.MoveTo(0, y); ctx.LineTo(width, y); }
        ctx.Stroke();

        // The chapter's name across the top, behind everything else.
        if (title.Length > 0)
        {
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
            double fontSize = scaled(40);
            ctx.SetFontSize(fontSize);
            double textWidth = ctx.TextExtents(title).XAdvance;
            if (textWidth > width - scaled(40))
            {
                fontSize *= (width - scaled(40)) / textWidth;
                ctx.SetFontSize(fontSize);
                textWidth = ctx.TextExtents(title).XAdvance;
            }
            double tx = width / 2.0 - textWidth / 2, ty = scaled(14) + ctx.FontExtents.Ascent;
            ctx.SetSourceRGBA(0, 0, 0, 0.55);
            ctx.MoveTo(tx + scaled(2), ty + scaled(2));
            ctx.ShowText(title);
            ctx.SetSourceRGBA(0.93, 0.88, 0.78, 0.5);
            ctx.MoveTo(tx, ty);
            ctx.ShowText(title);
        }

        if (quests.Count == 0)
        {
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(scaled(14));
            string none = Lang.Get("createacolony:quests-chapter-empty");
            ctx.SetSourceRGBA(1, 1, 1, 0.6);
            ctx.MoveTo(width / 2.0 - ctx.TextExtents(none).XAdvance / 2, height / 2.0);
            ctx.ShowText(none);
            return;
        }

        var shown = quests.Where(Shown).ToList();
        double r = NodeRadius * k;

        // Lines from each quest's requirements, green once they are done.
        foreach (var q in shown)
        {
            var (x2, y2) = Centre(q);
            foreach (string need in q.Requires)
            {
                var from = shown.FirstOrDefault(s => s.Code == need);
                if (from == null) continue;
                var (x1, y1) = Centre(from);
                bool done = StatusOf(from) == QuestStatus.Claimed;
                // A dark edge under a thick line, so it reads on any background.
                ctx.LineCap = LineCap.Round;
                ctx.SetSourceRGBA(0, 0, 0, 0.6);
                ctx.LineWidth = Math.Max(2, 7 * k);
                ctx.MoveTo(x1, y1);
                ctx.LineTo(x2, y2);
                ctx.Stroke();
                ctx.SetSourceRGBA(done ? 0.45 : 0.55, done ? 0.8 : 0.5, done ? 0.4 : 0.47, 1);
                ctx.LineWidth = Math.Max(1.5, 4 * k);
                ctx.MoveTo(x1, y1);
                ctx.LineTo(x2, y2);
                ctx.Stroke();
            }
        }

        // The quests themselves.
        foreach (var q in shown)
        {
            var (cx, cy) = Centre(q);
            if (cx < -r * 2 || cy < -r * 2 || cx > width + r * 2 || cy > height + r * 2) continue;
            var status = StatusOf(q);
            var color = StatusColor(status);
            bool isSelected = q.Code == selected, isHovered = q == hovered;

            var fill = FillColor(status);
            // A soft shadow, the solid frame, then its border.
            Shape(ctx, q.Shape, cx + 2 * k, cy + 3 * k, r);
            ctx.SetSourceRGBA(0, 0, 0, 0.45);
            ctx.Fill();
            Shape(ctx, q.Shape, cx, cy, r);
            ctx.SetSourceRGBA(fill[0], fill[1], fill[2], 1);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(color[0], color[1], color[2], 1);
            ctx.LineWidth = Math.Max(1.5, (isSelected ? 5 : isHovered ? 4 : 3) * k);
            ctx.Stroke();
            if (isSelected)
            {
                Shape(ctx, q.Shape, cx, cy, r + 5 * k);
                ctx.SetSourceRGBA(1, 1, 1, 0.8);
                ctx.LineWidth = Math.Max(1, 1.5 * k);
                ctx.Stroke();
            }
            if (status == QuestStatus.Complete)
            {
                // A glow around quests ready to claim.
                Shape(ctx, q.Shape, cx, cy, r + 6 * k);
                ctx.SetSourceRGBA(color[0], color[1], color[2], 0.4);
                ctx.LineWidth = Math.Max(1.5, 4 * k);
                ctx.Stroke();
            }
        }
    }

    /// <summary>The layer over the item icons: a padlock, tick, reward badge or tracking dot on each quest, and the name of the one under the pointer.</summary>
    void DrawMarks(Context ctx, int width, int height)
    {
        double k = Scale, r = NodeRadius * k;
        foreach (var q in quests)
        {
            if (!Shown(q)) continue;
            var (cx, cy) = Centre(q);
            if (cx < -r * 2 || cy < -r * 2 || cx > width + r * 2 || cy > height + r * 2) continue;
            var status = StatusOf(q);
            var color = StatusColor(status);
            if (status == QuestStatus.Locked) Padlock(ctx, cx + r * 0.72, cy + r * 0.7, r * 0.3);
            if (status == QuestStatus.Complete) Badge(ctx, cx + r * 0.74, cy - r * 0.74, r * 0.32);
            if (status == QuestStatus.Claimed)
            {
                // A tick on a dark disc in the corner.
                double tx = cx + r * 0.72, ty = cy + r * 0.7, tr = r * 0.32;
                ctx.NewPath();
                ctx.Arc(tx, ty, tr, 0, 2 * Math.PI);
                ctx.SetSourceRGBA(0.08, 0.14, 0.08, 1);
                ctx.FillPreserve();
                ctx.SetSourceRGBA(color[0], color[1], color[2], 1);
                ctx.LineWidth = Math.Max(1, 1.5 * k);
                ctx.Stroke();
                ctx.LineWidth = Math.Max(1.5, 3 * k);
                ctx.MoveTo(tx - tr * 0.5, ty);
                ctx.LineTo(tx - tr * 0.12, ty + tr * 0.42);
                ctx.LineTo(tx + tr * 0.55, ty - tr * 0.42);
                ctx.Stroke();
            }
            if (states.TryGetValue(q.Code, out var s) && s.Tracked)
            {
                // A white pin dot for tracked quests.
                ctx.NewPath();
                ctx.Arc(cx - r * 0.75, cy - r * 0.75, Math.Max(2, 5 * k), 0, 2 * Math.PI);
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Fill();
            }
        }

        // The name of the quest under the pointer.
        if (hovered != null && Shown(hovered))
        {
            var (cx, cy) = Centre(hovered);
            string text = Lang.Get(QuestLibrary.TitleKey(hovered));
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(scaled(14));
            double boxWidth = ctx.TextExtents(text).XAdvance + scaled(8), boxHeight = ctx.FontExtents.Height + scaled(4);
            double lx = Math.Clamp(cx - boxWidth / 2, scaled(2), Math.Max(scaled(2), width - boxWidth - scaled(2)));
            double ly = cy - r - boxHeight - scaled(4);
            if (ly < scaled(2)) ly = Math.Min(height - boxHeight - scaled(2), cy + r + scaled(4));
            ctx.SetSourceRGBA(0, 0, 0, 0.85);
            ctx.Rectangle(lx, ly, boxWidth, boxHeight);
            ctx.Fill();
            ctx.SetSourceRGBA(1, 1, 1, 0.5);
            ctx.LineWidth = 1;
            ctx.Rectangle(lx + 0.5, ly + 0.5, boxWidth - 1, boxHeight - 1);
            ctx.Stroke();
            ctx.SetSourceRGBA(1, 1, 1, 0.95);
            ctx.MoveTo(lx + scaled(4), ly + scaled(2) + ctx.FontExtents.Ascent);
            ctx.ShowText(text);
        }
    }

    /// <summary>A small padlock: the mark of a quest that is not open yet.</summary>
    static void Padlock(Context ctx, double cx, double cy, double size)
    {
        double w = size * 1.5, h = size * 1.15;
        // The shackle.
        ctx.NewPath();
        ctx.Arc(cx, cy - h * 0.35, size * 0.45, Math.PI, 2 * Math.PI);
        ctx.SetSourceRGBA(0.78, 0.78, 0.8, 1);
        ctx.LineWidth = Math.Max(1.2, size * 0.28);
        ctx.Stroke();
        // The body.
        ctx.NewPath();
        ctx.Rectangle(cx - w / 2, cy - h * 0.35, w, h);
        ctx.SetSourceRGBA(0.78, 0.78, 0.8, 1);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.1, 0.1, 0.1, 1);
        ctx.LineWidth = Math.Max(1, size * 0.12);
        ctx.Stroke();
        ctx.NewPath();
        ctx.Arc(cx, cy + h * 0.2, size * 0.16, 0, 2 * Math.PI);
        ctx.SetSourceRGBA(0.1, 0.1, 0.1, 1);
        ctx.Fill();
    }

    /// <summary>A red disc with a white "!": a reward is waiting here.</summary>
    public static void Badge(Context ctx, double cx, double cy, double radius)
    {
        ctx.NewPath();
        ctx.Arc(cx, cy, radius, 0, 2 * Math.PI);
        ctx.SetSourceRGBA(0.86, 0.18, 0.16, 1);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(1, 1, 1, 0.9);
        ctx.LineWidth = Math.Max(1, radius * 0.16);
        ctx.Stroke();
        ctx.SetSourceRGBA(1, 1, 1, 1);
        ctx.LineWidth = Math.Max(1.2, radius * 0.34);
        ctx.LineCap = LineCap.Round;
        ctx.MoveTo(cx, cy - radius * 0.5);
        ctx.LineTo(cx, cy + radius * 0.1);
        ctx.Stroke();
        ctx.NewPath();
        ctx.Arc(cx, cy + radius * 0.48, Math.Max(0.8, radius * 0.17), 0, 2 * Math.PI);
        ctx.Fill();
    }

    /// <summary>Traces a quest's outline: round, square, a five-pointed star or a hexagon.</summary>
    static void Shape(Context ctx, string shape, double cx, double cy, double r)
    {
        switch (shape)
        {
            case QuestShapes.Square:
                double s = r * 0.9, c = r * 0.25;
                ctx.NewPath();
                ctx.MoveTo(cx - s + c, cy - s);
                ctx.LineTo(cx + s - c, cy - s);
                ctx.Arc(cx + s - c, cy - s + c, c, -Math.PI / 2, 0);
                ctx.LineTo(cx + s, cy + s - c);
                ctx.Arc(cx + s - c, cy + s - c, c, 0, Math.PI / 2);
                ctx.LineTo(cx - s + c, cy + s);
                ctx.Arc(cx - s + c, cy + s - c, c, Math.PI / 2, Math.PI);
                ctx.LineTo(cx - s, cy - s + c);
                ctx.Arc(cx - s + c, cy - s + c, c, Math.PI, 3 * Math.PI / 2);
                ctx.ClosePath();
                break;
            case QuestShapes.Star:
                ctx.NewPath();
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5;
                    double radius = i % 2 == 0 ? r * 1.15 : r * 0.62;
                    double x = cx + Math.Cos(angle) * radius, y = cy + Math.Sin(angle) * radius;
                    if (i == 0) ctx.MoveTo(x, y); else ctx.LineTo(x, y);
                }
                ctx.ClosePath();
                break;
            case QuestShapes.Hexagon:
                ctx.NewPath();
                for (int i = 0; i < 6; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 3;
                    double x = cx + Math.Cos(angle) * r * 1.05, y = cy + Math.Sin(angle) * r * 1.05;
                    if (i == 0) ctx.MoveTo(x, y); else ctx.LineTo(x, y);
                }
                ctx.ClosePath();
                break;
            default:
                ctx.NewPath();
                ctx.Arc(cx, cy, r, 0, 2 * Math.PI);
                break;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
        overlay.Dispose();
    }
}
