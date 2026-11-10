using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using MinecraftCS.Graphics;

namespace MinecraftCS;

// Менеджер шейдерпаков в стиле Minecraft
// Поддерживает добавление шейдеров в папку shaderpacks/ и переключение через UI
internal static unsafe partial class Program
{
    static readonly string ShaderpacksDir = ResolveShaderpacksDirectory(AppContext.BaseDirectory);
    static readonly List<string> shaderPacks = new();
    static readonly Dictionary<string, string> shaderPackPaths = new(StringComparer.OrdinalIgnoreCase);
    static int shaderPackOffset;
    const int VisibleShaderPacks = 7;
    static string currentShaderPack = "(internal) Vanilla";
    static bool shaderMenuOpen;
    static Texture2D dirtUiTex;
    static bool dirtUiLoaded;
    static int locGameTime;

    // Инициализация менеджера шейдеров
    static void InitShaderManager()
    {
        try
        {
            Directory.CreateDirectory(ShaderpacksDir);

            // Загрузка текстуры земли для фона меню в стиле Minecraft
            string dirtPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Faithful", "dirt.png");
            if (!File.Exists(dirtPath))
                dirtPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "BareBones", "dirt.png");

            if (File.Exists(dirtPath))
            {
                dirtUiTex = Raylib.LoadTexture(dirtPath);
                Raylib.SetTextureFilter(dirtUiTex, TextureFilter.Point);
                dirtUiLoaded = dirtUiTex.Id != 0;
            }

            ScanShaderPacks();

            // Если доступен Realis — пробуем применить его, иначе оставляем встроенный
            if (shaderPacks.Contains("Realis v1.0"))
                ApplyShaderPack("Realis v1.0");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShaderManager] Init error: {ex.Message}");
        }
    }

    // Сканирование папки shaderpacks/ на наличие папок и .zip архивов
    static void ScanShaderPacks()
    {
        shaderPacks.Clear();
        shaderPackPaths.Clear();
        shaderPacks.Add("(internal) Vanilla");

        if (!Directory.Exists(ShaderpacksDir)) return;

        var candidates = Directory.GetDirectories(ShaderpacksDir)
            .Where(dir => File.Exists(Path.Combine(dir, "fragment.glsl")) || Directory.Exists(Path.Combine(dir, "shaders")))
            .Concat(Directory.GetFiles(ShaderpacksDir, "*.zip"));
        foreach (var path in candidates)
        {
            string name = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            if (name.Equals("(internal) Vanilla", StringComparison.OrdinalIgnoreCase)) continue;
            if (!shaderPackPaths.TryAdd(name, path)) continue;
            shaderPacks.Add(name);
        }
        shaderPacks.Sort(1, shaderPacks.Count - 1, StringComparer.OrdinalIgnoreCase);
        shaderPackOffset = Math.Clamp(shaderPackOffset, 0, Math.Max(0, shaderPacks.Count - VisibleShaderPacks));
    }

    // Применение выбранного шейдерпака
    static bool ApplyShaderPack(string packName)
    {
        Shader candidateWorld = default;
        IScreenRenderChain candidatePost = null;
        try
        {
            ShaderPackSource source = packName == "(internal) Vanilla"
                ? new ShaderPackSource(VertexShaderSrc, FragmentShaderSrc, false)
                : ReadShaderPack(shaderPackPaths[packName]);

            candidateWorld = Raylib.LoadShaderFromMemory(source.Vertex.Replace("@TILE_COUNT@", TileCount.ToString()),
                PrepareFragmentShader(source.Fragment));
            if (!IsWorldShaderLoaded(candidateWorld))
                throw new InvalidDataException($"Terrain shader failed: {packName}");
            if (source.Post is { Length: > 0 } stages)
                candidatePost = renderBackend.CreateScreenChain(stages, Raylib.GetScreenWidth(), Raylib.GetScreenHeight());

            if (shaderOk) Raylib.UnloadShader(worldShader);
            activeScreenChain?.Dispose();
            activeScreenChain = candidatePost;
            candidatePost = null;
            worldShader = candidateWorld;
            candidateWorld = default;
            shaderOk = true;

            BindWorldShader();
            material.Maps[(int)MaterialMapIndex.Normal].Texture = source.Java
                ? new Texture2D { Id = Rlgl.GetTextureIdDefault(), Width = 1, Height = 1, Mipmaps = 1 }
                : default;

            currentShaderPack = packName;
            ShowStatus($"Shader active: {packName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShaderManager] Shader compile failed: {ex.Message}");
            ShowStatus(ex is InvalidDataException ? ex.Message : $"Could not load shader: {packName}");
            return false;
        }
        finally
        {
            if (IsWorldShaderLoaded(candidateWorld)) Raylib.UnloadShader(candidateWorld);
            candidatePost?.Dispose();
        }
    }

    // Обработка ввода в меню шейдеров
    static void UpdateShaderMenu()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            shaderMenuOpen = false;
            return;
        }

        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Vector2 mouse = Raylib.GetMousePosition();

        int panelW = 460, panelH = 340;
        int panelX = (w - panelW) / 2;
        int panelY = (h - panelH) / 2 - 20;

        float scroll = Raylib.GetMouseWheelMove();
        if (scroll != 0) shaderPackOffset = Math.Clamp(shaderPackOffset - Math.Sign(scroll), 0, Math.Max(0, shaderPacks.Count - VisibleShaderPacks));
        if (Raylib.IsKeyPressed(KeyboardKey.R)) ScanShaderPacks();
        // Клик по элементам списка шейдеров
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            int itemY = panelY + 12;
            for (int i = shaderPackOffset; i < Math.Min(shaderPacks.Count, shaderPackOffset + VisibleShaderPacks); i++)
            {
                Rectangle itemRect = new(panelX + 10, itemY, panelW - 20, 36);
                if (Raylib.CheckCollisionPointRec(mouse, itemRect))
                {
                    ApplyShaderPack(shaderPacks[i]);
                    break;
                }
                itemY += 42;
            }

            // Кнопка: Open shader pack folder
            Rectangle btnFolderRect = new(panelX, panelY + panelH + 16, 240, 38);
            if (Raylib.CheckCollisionPointRec(mouse, btnFolderRect))
            {
                try
                {
                    Directory.CreateDirectory(ShaderpacksDir);
                    Process.Start(new ProcessStartInfo { FileName = ShaderpacksDir, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ShaderManager] Could not open folder: {ex.Message}");
                }
            }

            // Кнопка: Done
            Rectangle btnDoneRect = new(panelX + 260, panelY + panelH + 16, 200, 38);
            if (Raylib.CheckCollisionPointRec(mouse, btnDoneRect))
            {
                shaderMenuOpen = false;
            }
        }
    }

    // Отрисовка UI меню выбора шейдеров в стиле Minecraft
    static void DrawShaderMenu()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Vector2 mouse = Raylib.GetMousePosition();

        // 1. Заливка экрана тайлами текстуры земли (dirt) с затемнением, как в Minecraft
        if (dirtUiLoaded)
        {
            int tileSize = 64;
            for (int y = 0; y < h; y += tileSize)
            for (int x = 0; x < w; x += tileSize)
            {
                Raylib.DrawTexturePro(dirtUiTex,
                    new Rectangle(0, 0, dirtUiTex.Width, dirtUiTex.Height),
                    new Rectangle(x, y, tileSize, tileSize),
                    Vector2.Zero, 0f, Col(64, 64, 64, 255));
            }
        }
        else
        {
            Raylib.DrawRectangle(0, 0, w, h, Col(30, 24, 20, 255));
        }

        // Легкое виньетирование / градиент
        Raylib.DrawRectangleGradientV(0, 0, w, 80, Col(0, 0, 0, 180), Col(0, 0, 0, 0));
        Raylib.DrawRectangleGradientV(0, h - 80, w, 80, Col(0, 0, 0, 0), Col(0, 0, 0, 180));

        // 2. Заголовок с классической тенью текста Minecraft
        void DrawTextWithShadow(string text, int x, int y, int size, Color color)
        {
            Raylib.DrawText(text, x + 2, y + 2, size, Col(40, 40, 40, 230));
            Raylib.DrawText(text, x, y, size, color);
        }

        void CenterWithShadow(string text, int y, int size, Color color)
        {
            int textW = Raylib.MeasureText(text, size);
            DrawTextWithShadow(text, (w - textW) / 2, y, size, color);
        }

        CenterWithShadow("Shaders", 28, 28, Col(255, 255, 255));
        CenterWithShadow("Add a folder or ZIP, then press R to refresh", 62, 16, Col(180, 180, 180));

        // 3. Центральная панель списка шейдеров (Minecraft Inset Frame)
        int panelW = 460, panelH = 340;
        int panelX = (w - panelW) / 2;
        int panelY = (h - panelH) / 2 - 20;

        DrawMinecraftInsetPanel(new Rectangle(panelX, panelY, panelW, panelH));

        // 4. Отрисовка элементов списка шейдерпаков
        int itemY = panelY + 12;
        for (int i = shaderPackOffset; i < Math.Min(shaderPacks.Count, shaderPackOffset + VisibleShaderPacks); i++)
        {
            string pack = shaderPacks[i];
            bool isSelected = pack == currentShaderPack;
            Rectangle itemRect = new(panelX + 10, itemY, panelW - 20, 36);
            bool isHovered = Raylib.CheckCollisionPointRec(mouse, itemRect);

            DrawMinecraftListItem(itemRect, pack, isSelected, isHovered);
            itemY += 42;
        }

        if (Raylib.GetTime() < statusUntil) CenterWithShadow(status, panelY + panelH - 23, 15, Col(255, 230, 153));

        // 5. Кнопки в стиле Minecraft внизу экрана
        Rectangle btnFolderRect = new(panelX, panelY + panelH + 16, 240, 38);
        bool hoverFolder = Raylib.CheckCollisionPointRec(mouse, btnFolderRect);
        DrawMinecraftButton(btnFolderRect, "Shaders Folder", hoverFolder);

        Rectangle btnDoneRect = new(panelX + 260, panelY + panelH + 16, 200, 38);
        bool hoverDone = Raylib.CheckCollisionPointRec(mouse, btnDoneRect);
        DrawMinecraftButton(btnDoneRect, "Done", hoverDone);
    }

    // Отрисовка кнопки в стиле Minecraft (3D bevel, темный контур, тень текста)
    static void DrawMinecraftButton(Rectangle rect, string text, bool hovered)
    {
        int x = (int)rect.X, y = (int)rect.Y, bw = (int)rect.Width, bh = (int)rect.Height;

        // Внешний черный контур
        Raylib.DrawRectangle(x, y, bw, bh, Col(0, 0, 0, 255));

        // Цвет заливки кнопки (стандартный серый / подсвеченный при наведении)
        Color fillColor = hovered ? Col(135, 146, 175) : Col(110, 110, 110);
        Color lightBevel = hovered ? Col(185, 195, 220) : Col(155, 155, 155);
        Color darkBevel = hovered ? Col(85, 95, 120) : Col(65, 65, 65);

        // Внутренняя заливка
        Raylib.DrawRectangle(x + 2, y + 2, bw - 4, bh - 4, fillColor);

        // Верхний и левый светлый блик (bevel)
        Raylib.DrawRectangle(x + 2, y + 2, bw - 4, 2, lightBevel);
        Raylib.DrawRectangle(x + 2, y + 2, 2, bh - 4, lightBevel);

        // Нижний и правый теневой скос (bevel)
        Raylib.DrawRectangle(x + 2, y + bh - 4, bw - 4, 2, darkBevel);
        Raylib.DrawRectangle(x + bw - 4, y + 2, 2, bh - 4, darkBevel);

        // Текст кнопки (при наведении текст становится классически желтым, как в Minecraft)
        int fontSize = 18;
        int textW = Raylib.MeasureText(text, fontSize);
        int tx = x + (bw - textW) / 2;
        int ty = y + (bh - fontSize) / 2;

        Color textColor = hovered ? Col(255, 255, 160) : Col(225, 225, 225);
        Raylib.DrawText(text, tx + 2, ty + 2, fontSize, Col(45, 45, 45));
        Raylib.DrawText(text, tx, ty, fontSize, textColor);
    }

    // Отрисовка углубленной рамки для списка (Minecraft Inset Frame)
    static void DrawMinecraftInsetPanel(Rectangle rect)
    {
        int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;

        // Внешний контур
        Raylib.DrawRectangle(x - 2, y - 2, w + 4, h + 4, Col(0, 0, 0, 200));

        // Темный фон панели
        Raylib.DrawRectangle(x, y, w, h, Col(16, 16, 18, 235));

        // Теневая рамка внутри (сверху и слева тень, снизу и справа светлый кант)
        Raylib.DrawRectangle(x, y, w, 2, Col(10, 10, 10, 255));
        Raylib.DrawRectangle(x, y, 2, h, Col(10, 10, 10, 255));
        Raylib.DrawRectangle(x, y + h - 2, w, 2, Col(50, 50, 55, 255));
        Raylib.DrawRectangle(x + w - 2, y, 2, h, Col(50, 50, 55, 255));
    }

    // Отрисовка элемента списка шейдерпаков
    static void DrawMinecraftListItem(Rectangle rect, string name, bool isSelected, bool isHovered)
    {
        int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;

        if (isSelected)
        {
            // Выделенный шейдер: белая обводка и подсветка
            Raylib.DrawRectangle(x, y, w, h, Col(0, 0, 0, 200));
            Raylib.DrawRectangleLines(x, y, w, h, Col(255, 255, 255));
        }
        else if (isHovered)
        {
            // Подсветка при наведении
            Raylib.DrawRectangle(x, y, w, h, Col(50, 55, 65, 180));
            Raylib.DrawRectangleLines(x, y, w, h, Col(130, 135, 145));
        }

        string label = isSelected ? $"> {name} [ACTIVE]" : $"  {name}";
        Color textColor = isSelected ? Col(255, 255, 140) : (isHovered ? Col(255, 255, 255) : Col(200, 200, 200));

        Raylib.DrawText(label, x + 12, y + 9, 18, Col(30, 30, 30));
        Raylib.DrawText(label, x + 10, y + 7, 18, textColor);
    }
}
