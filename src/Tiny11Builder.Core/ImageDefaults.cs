namespace Tiny11Builder.Core;

public static class ImageDefaults
{
    public static int SelectPosition(IReadOnlyList<WindowsImage> images, int? restoredIndex = null)
    {
        if (images.Count == 0) throw new InvalidDataException("源中不包含任何 Windows 版本。");
        if (restoredIndex is int saved)
            for (var i = 0; i < images.Count; i++)
                if (images[i].Index == saved) return i;
        for (var i = 0; i < images.Count; i++)
            if (images[i].Edition.Equals("Professional", StringComparison.OrdinalIgnoreCase) ||
                images[i].Name.Equals("Windows 11 Pro", StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < images.Count; i++) if (images[i].Index == 1) return i;
        throw new InvalidDataException("此源中既没有 Windows 11 专业版，也没有映像索引 1。");
    }
}
