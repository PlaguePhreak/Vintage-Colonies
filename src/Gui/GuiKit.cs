using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// Shared look for the mod's windows: the same fonts and colours everywhere, and text blocks sized to
/// their wrapped text so nothing is cut off or leaves a gap whatever the language or GUI scale.
/// </summary>
public static class GuiKit
{
    public static readonly double[] GoodColor = { 0.55, 0.85, 0.45, 1.0 };
    public static readonly double[] WarnColor = { 1.0, 0.65, 0.3, 1.0 };
    public static readonly double[] MutedColor = { 1.0, 1.0, 1.0, 0.6 };

    /// <summary>Section headings.</summary>
    public static CairoFont Heading() => CairoFont.WhiteSmallishText();
    /// <summary>Names, values and the main line of a row.</summary>
    public static CairoFont Body() => CairoFont.WhiteSmallText();
    /// <summary>Explanations and secondary lines.</summary>
    public static CairoFont Detail() => CairoFont.WhiteDetailText();
    /// <summary>Hints, greyed a little so they read as background.</summary>
    public static CairoFont Muted() => CairoFont.WhiteDetailText().WithColor(MutedColor);

    /// <summary>
    /// A building variant's name: its lang entry (createacolony:variant-small) if there is one, else the
    /// folder name with a capital, so a new variant folder needs no lang entry. "" is the Standard one.
    /// </summary>
    public static string VariantName(string variant)
    {
        string key = "createacolony:variant-" + (variant == "" ? "default" : variant);
        if (Lang.HasTranslation(key, false, false)) return Lang.Get(key);
        return variant.Length == 0 ? "" : char.ToUpperInvariant(variant[0]) + variant.Substring(1);
    }

    /// <summary>The unscaled height plain text takes when wrapped to this width.</summary>
    public static double TextHeight(ICoreClientAPI capi, CairoFont font, string text, double width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double scale = Math.Max(0.01, RuntimeEnv.GUIScale);
        return Math.Ceiling(capi.Gui.Text.GetMultilineTextHeight(font, text, width * scale) / scale) + 2;
    }

    /// <summary>The unscaled height rich text (VTML) takes when wrapped to this width.</summary>
    public static double RichHeight(ICoreClientAPI capi, CairoFont font, string vtml, double width)
    {
        if (string.IsNullOrEmpty(vtml)) return 0;
        GuiElementRichtext? elem = null;
        try
        {
            elem = new GuiElementRichtext(capi, VtmlUtil.Richtextify(capi, vtml, font), ElementBounds.Fixed(0, 0, width, 10).WithParent(ElementBounds.Empty));
            elem.CalcHeightAndPositions();
            return Math.Ceiling(elem.Bounds.fixedHeight) + 2;
        }
        catch (Exception e)
        {
            // Measuring is only for layout; guess from the line breaks rather than break the window.
            capi.Logger.VerboseDebug("[createacolony] Could not measure rich text: {0}", e.Message);
            int lines = 1 + vtml.Split("<br>").Length + vtml.Length / 70;
            return lines * 18;
        }
        finally
        {
            elem?.Dispose();
        }
    }

    /// <summary>Adds wrapped text at y and returns the y below it, plus a gap.</summary>
    public static double Text(this GuiComposer composer, string text, CairoFont font, double x, double y, double width, double gap = 6)
    {
        double height = TextHeight(composer.Api, font, text, width);
        if (height <= 0) return y;
        composer.AddStaticText(text, font, ElementBounds.Fixed(x, y, width, height));
        return y + height + gap;
    }

    /// <summary>Adds wrapped rich text (VTML) at y and returns the y below it, plus a gap.</summary>
    public static double Rich(this GuiComposer composer, string vtml, CairoFont font, double x, double y, double width, double gap = 6)
    {
        double height = RichHeight(composer.Api, font, vtml, width);
        if (height <= 0) return y;
        composer.AddRichtext(vtml, font, ElementBounds.Fixed(x, y, width, height));
        return y + height + gap;
    }

    /// <summary>A section heading with a thin line under it and a little room above. Returns the y below it.</summary>
    public static double Section(this GuiComposer composer, string title, double y, double width)
    {
        composer.AddStaticText(title, Heading(), ElementBounds.Fixed(0, y + 4, width, 24));
        composer.AddInset(ElementBounds.Fixed(0, y + 30, width, 2), 1, 0.5f);
        return y + 40;
    }

    /// <summary>
    /// Makes a tabbed window at least as tall as its tab list down the left side, so the last tabs never
    /// hang below the window. Call it between BeginChildElements and EndChildElements.
    /// </summary>
    public static void FitTabs(this GuiComposer composer, int tabCount)
    {
        if (tabCount > 1) composer.AddStaticText("", Detail(), ElementBounds.Fixed(0, 35 + tabCount * 30, 1, 1));
    }

    /// <summary>One label / value line, the label on the left and the value right-aligned. Returns the y below it.</summary>
    public static double Row(this GuiComposer composer, string label, string value, double x, double y, double width, double[]? valueColor = null)
    {
        var valueFont = Body().WithOrientation(EnumTextOrientation.Right);
        if (valueColor != null) valueFont = valueFont.WithColor(valueColor);
        composer
            .AddStaticText(label, Detail(), ElementBounds.Fixed(x, y + 2, width * 0.6, 22))
            .AddStaticText(value, valueFont, ElementBounds.Fixed(x + width * 0.4, y, width * 0.6, 22));
        return y + 24;
    }

    /// <summary>Previous / page n of m / next, centred. Does nothing for a single page.</summary>
    public static void Pager(this GuiComposer composer, int page, int pages, double y, double width, Action<int> go)
    {
        if (pages <= 1) return;
        double mid = width / 2;
        composer
            .AddSmallButton(Lang.Get("createacolony:prev"), () => { if (page > 0) go(page - 1); return true; }, ElementBounds.Fixed(mid - 160, y, 90, 28))
            .AddStaticText(Lang.Get("createacolony:page", page + 1, pages), Detail().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(mid - 60, y + 6, 120, 22))
            .AddSmallButton(Lang.Get("createacolony:next"), () => { if (page < pages - 1) go(page + 1); return true; }, ElementBounds.Fixed(mid + 70, y, 90, 28));
    }

    /// <summary>
    /// Clips what is drawn next to a box (in screen pixels) that also lies inside an element. Call it between
    /// PushScissor(element) and PopScissor. False when nothing of the box is on show, so the drawing can be skipped.
    /// </summary>
    public static bool ClipTo(ICoreClientAPI capi, ElementBounds within, double x, double y, double width, double height)
    {
        double left = Math.Max(x, within.renderX), top = Math.Max(y, within.renderY);
        double right = Math.Min(x + width, within.renderX + within.InnerWidth), bottom = Math.Min(y + height, within.renderY + within.InnerHeight);
        if (right - left < 1 || bottom - top < 1) return false;
        capi.Render.GlScissor((int)left, (int)(capi.Render.FrameHeight - bottom), (int)(right - left), (int)(bottom - top));
        return true;
    }
}
