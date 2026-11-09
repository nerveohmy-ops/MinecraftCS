using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static readonly string[] PackTiles = {
        "grass_block_top", "grass_block_side", "dirt", "stone", "sand",
        "oak_log", "oak_log_top", "oak_leaves", "oak_planks", "bricks", "cobblestone"
    };

    static void ApplyBlockTextures(ref Image atlasImage)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Faithful");
        for (int tile = 0; tile < PackTiles.Length; tile++)
        {
            string path = Path.Combine(directory, PackTiles[tile] + ".png");
            if (!File.Exists(path)) continue;
            Image source = Raylib.LoadImage(path);
            if (source.Data == null) continue;
            // Keep every pixel of the supplied 32x32 pack without downsampling.
            if (source.Width != TileSize || source.Height != TileSize)
            {
                Raylib.UnloadImage(source);
                continue;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                Color color = Raylib.GetImageColor(source, x, y);
                if (tile is 0 or 7)
                {
                    color.R = (byte)(color.R * (tile == 0 ? 0.56f : 0.48f));
                    color.G = (byte)(color.G * (tile == 0 ? 0.94f : 0.88f));
                    color.B = (byte)(color.B * (tile == 0 ? 0.32f : 0.36f));
                }
                // Leaves remain opaque to match the world's solid-block face culling.
                Color background = tile == 7 ? Col(54, 100, 35) : Raylib.GetImageColor(atlasImage, tile * TileSize + x, y);
                int alpha = color.A;
                color = Col((color.R * alpha + background.R * (255 - alpha)) / 255,
                    (color.G * alpha + background.G * (255 - alpha)) / 255,
                    (color.B * alpha + background.B * (255 - alpha)) / 255);
                Raylib.ImageDrawPixel(ref atlasImage, tile * TileSize + x, y, color);
            }
            Raylib.UnloadImage(source);
        }
        string overlayPath = Path.Combine(directory, "grass_block_side_overlay.png");
        if (!File.Exists(overlayPath) || !File.Exists(Path.Combine(directory, "grass_block_side.png"))) return;
        Image overlay = Raylib.LoadImage(overlayPath);
        if (overlay.Data == null) return;
        if (overlay.Width == TileSize && overlay.Height == TileSize)
        {
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                Color mask = Raylib.GetImageColor(overlay, x, y);
                Color background = Raylib.GetImageColor(atlasImage, TileSize + x, y);
                int alpha = mask.A;
                Color result = Col(((int)(mask.R * 0.56f) * alpha + background.R * (255 - alpha)) / 255,
                    ((int)(mask.G * 0.94f) * alpha + background.G * (255 - alpha)) / 255,
                    ((int)(mask.B * 0.32f) * alpha + background.B * (255 - alpha)) / 255);
                Raylib.ImageDrawPixel(ref atlasImage, TileSize + x, y, result);
            }
        }
        Raylib.UnloadImage(overlay);
    }
}
