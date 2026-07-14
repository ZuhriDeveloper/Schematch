using MaterialSkin;
using MaterialSkin.Controls;

namespace Schematch.App.UI;

/// <summary>Central MaterialSkin configuration: one manager setup shared by every window.</summary>
internal static class MaterialTheme
{
    // Diff status accents, tuned to read well on the light material surfaces.
    public static readonly Color SourceOnlyColor = Color.FromArgb(46, 125, 50);   // green 800
    public static readonly Color TargetOnlyColor = Color.FromArgb(198, 40, 40);   // red 800
    public static readonly Color DifferentColor = Color.FromArgb(239, 108, 0);    // orange 800
    public static readonly Color EqualColor = Color.FromArgb(117, 117, 117);      // grey 600

    private static bool _initialized;

    /// <summary>Registers a form with the skin manager (configuring the theme on first use) and unregisters it on close.</summary>
    public static void Apply(MaterialForm form)
    {
        var manager = MaterialSkinManager.Instance;
        if (!_initialized)
        {
            _initialized = true;
            // Non-material controls (list views, grids, diff panes, the dark execution log) keep their own colors.
            manager.EnforceBackcolorOnAllComponents = false;
            manager.Theme = MaterialSkinManager.Themes.LIGHT;
            manager.ColorScheme = new ColorScheme(
                Primary.Indigo600, Primary.Indigo700, Primary.Indigo100,
                Accent.LightBlue200, TextShade.WHITE);
        }
        manager.AddFormToManage(form);
        form.FormClosed += (_, _) => manager.RemoveFormToManage(form);
    }
}
