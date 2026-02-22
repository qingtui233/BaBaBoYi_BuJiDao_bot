namespace BedwarsBot;

public static class RenderFontHelper
{
    private const string IconsConfigDirectoryName = "pz";
    private const string FontsDirectoryName = "fonts";
    private const string DefaultFontStack = "'Nunito', 'Noto Sans SC', sans-serif";

    public static (string FontFaceCss, string GlobalFontFamily) BuildCustomFontCss()
    {
        try
        {
            var fontsDir = ResolveFontsDirectory();
            if (!Directory.Exists(fontsDir))
            {
                Directory.CreateDirectory(fontsDir);
                return (string.Empty, DefaultFontStack);
            }

            var fontFile = Directory
                .EnumerateFiles(fontsDir)
                .Where(path =>
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    return ext is ".ttf" or ".otf" or ".woff" or ".woff2";
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(fontFile))
            {
                return (string.Empty, DefaultFontStack);
            }

            var fontDataUri = BuildDataUriFromFile(fontFile);
            if (string.IsNullOrWhiteSpace(fontDataUri))
            {
                return (string.Empty, DefaultFontStack);
            }

            var ext = Path.GetExtension(fontFile).ToLowerInvariant();
            var format = ext switch
            {
                ".ttf" => "truetype",
                ".otf" => "opentype",
                ".woff" => "woff",
                ".woff2" => "woff2",
                _ => "truetype"
            };

            const string fontFamily = "BwdCustomFont";
            var css = $"@font-face{{font-family:'{fontFamily}';src:url('{fontDataUri}') format('{format}');font-display:swap;}}";
            return (css, $"'{fontFamily}', 'Noto Sans SC', sans-serif");
        }
        catch
        {
            return (string.Empty, DefaultFontStack);
        }
    }

    private static string ResolveFontsDirectory()
    {
        var pzDir = ResolvePzDirectory();
        return Path.Combine(pzDir, FontsDirectoryName);
    }

    private static string ResolvePzDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, IconsConfigDirectoryName);
            if (Directory.Exists(candidate) || File.Exists(Path.Combine(dir.FullName, "BedwarsBot.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, IconsConfigDirectoryName);
    }

    private static string? BuildDataUriFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0)
            {
                return null;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream"
            };

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }
}
