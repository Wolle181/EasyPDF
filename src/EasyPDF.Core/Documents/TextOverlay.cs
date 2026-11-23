namespace EasyPDF.Core.Documents;

/// <summary>
/// Text placed on a page (not saved yet). Like <see cref="StampOverlay"/> the rectangle lives in the page's own space
/// (source page size after its own /Rotate, before the extra rotation, origin top-left, in points) and the text turns with the page.
/// </summary>
/// <param name="Text">The text; lines are separated by "\n".</param>
/// <param name="Font">One of the standard fonts (see <c>TextStyle.Fonts</c>) or the family of an installed font
/// (<c>FontLibrary</c>), which is embedded when saving; where that font is missing the most similar standard font is used.</param>
/// <param name="FontSize">Size in points.</param>
/// <param name="Color">Pen colour as #RRGGBB.</param>
/// <param name="Rect">The area the text covers (after <paramref name="Rotation"/>), so it is also the box to select or move.</param>
/// <param name="Rotation">Clockwise turn of the text inside page space (0, 90, 180, 270).</param>
/// <param name="Bold">Bold face of the font.</param>
/// <param name="Italic">Italic face of the font.</param>
public sealed record TextOverlay(int Id, string Text, string Font, double FontSize, string Color, RectPt Rect, int Rotation = 0,
    bool Bold = false, bool Italic = false);
