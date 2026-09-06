using System.Reflection;
using System.Text;

namespace AramOverlay.Core;

/// <summary>The widget page and match templates, carried inside the assembly.</summary>
public static class Assets
{
    private static readonly Assembly Self = typeof(Assets).Assembly;

    public static readonly string[] RerollTemplateNames =
        { "tpl_reroll_1.png", "tpl_reroll_2.png", "tpl_reroll_3.png" };

    public const string HideTemplateName = "tpl_hide.png";

    public static byte[] Bytes(string name)
    {
        using var stream = Self.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"embedded resource missing: {name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    public static async Task<GrayImage> TemplateAsync(string name) =>
        Cv.ToGray(await Imaging.DecodeAsync(Bytes(name)));

    public static async Task<TemplateGate> GateAsync()
    {
        var templates = new List<GrayImage>();
        foreach (string name in RerollTemplateNames)
            templates.Add(await TemplateAsync(name));
        return new TemplateGate(templates);
    }

    public static async Task<HideButton> HideButtonAsync() =>
        new(await TemplateAsync(HideTemplateName));
}
