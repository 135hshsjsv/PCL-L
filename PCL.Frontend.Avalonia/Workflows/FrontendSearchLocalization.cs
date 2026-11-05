using System.Text;

namespace PCL.Frontend.Avalonia.Workflows;

/// <summary>
/// Modrinth and CurseForge are indexed in English, so a Chinese keyword such as "优化" returns
/// nothing. This maps the vocabulary players actually type to the English terms the search APIs
/// understand. Unknown words are kept as-is so mixed queries still work.
/// </summary>
internal static class FrontendSearchLocalization
{
    private static readonly Dictionary<string, string> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        // Categories
        ["优化"] = "optimization",
        ["性能"] = "performance",
        ["辅助"] = "utility",
        ["工具"] = "tools",
        ["科技"] = "technology",
        ["工业"] = "industrial",
        ["机械"] = "machinery",
        ["自动化"] = "automation",
        ["能源"] = "energy",
        ["魔法"] = "magic",
        ["冒险"] = "adventure",
        ["探索"] = "exploration",
        ["建筑"] = "building",
        ["装饰"] = "decoration",
        ["家具"] = "furniture",
        ["食物"] = "food",
        ["农业"] = "farming",
        ["种植"] = "crops",
        ["烹饪"] = "cooking",
        ["钓鱼"] = "fishing",
        ["生物"] = "mobs",
        ["怪物"] = "mobs",
        ["战斗"] = "combat",
        ["武器"] = "weapons",
        ["装备"] = "equipment",
        ["盔甲"] = "armor",
        ["附魔"] = "enchanting",
        ["存储"] = "storage",
        ["背包"] = "inventory",
        ["整理"] = "sorting",
        ["物流"] = "logistics",
        ["运输"] = "transport",
        ["红石"] = "redstone",
        ["世界生成"] = "world generation",
        ["地形"] = "terrain",
        ["生物群系"] = "biomes",
        ["结构"] = "structures",
        ["地图"] = "map",
        ["小地图"] = "minimap",
        ["任务"] = "quests",
        ["配方"] = "recipe",
        ["物品"] = "items",
        ["界面"] = "user interface",
        ["字体"] = "font",
        ["音效"] = "sound",
        ["光影"] = "shaders",
        ["材质"] = "resource pack",
        ["着色器"] = "shader",
        ["汉化"] = "translation",
        ["中文"] = "chinese",
        ["兼容"] = "compatibility",
        ["前置"] = "library",
        ["库"] = "library",
        ["联机"] = "multiplayer",
        ["服务器"] = "server",
        ["数据包"] = "datapack",
        ["整合包"] = "modpack",
        ["模组"] = "mod",
        ["插件"] = "plugin",
        ["存档"] = "world",
        ["种子"] = "seed",

        // Popular projects players refer to by their Chinese nickname
        ["钠"] = "sodium",
        ["锂"] = "lithium",
        ["星光"] = "starlight",
        ["铷"] = "rubidium",
        ["镁"] = "magnesium",
        ["嵌入式"] = "embeddium",
        ["机械动力"] = "create",
        ["通用机械"] = "mekanism",
        ["工业时代"] = "industrial craft",
        ["应用能源"] = "applied energistics",
        ["神秘时代"] = "thaumcraft",
        ["植物魔法"] = "botania",
        ["匠魂"] = "tinkers construct",
        ["暮色森林"] = "twilight forest",
        ["农夫乐事"] = "farmers delight",
        ["精妙背包"] = "sophisticated backpacks",
        ["苹果皮"] = "appleskin",
        ["旅行地图"] = "journeymap",
        ["物品管理器"] = "jei",
        ["合成表"] = "jei",
        ["光影包"] = "shaderpack",
        ["高清修复"] = "optifine",
        ["铱"] = "iridium"
    };

    /// <summary>Chinese nickname → the exact English project name it refers to.</summary>
    private static readonly Dictionary<string, string> CanonicalProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["钠"] = "Sodium",
        ["锂"] = "Lithium",
        ["星光"] = "Starlight",
        ["铷"] = "Rubidium",
        ["镁"] = "Magnesium",
        ["嵌入式"] = "Embeddium",
        ["铱"] = "Iridium",
        ["机械动力"] = "Create",
        ["通用机械"] = "Mekanism",
        ["工业时代"] = "Industrial Craft",
        ["应用能源"] = "Applied Energistics 2",
        ["神秘时代"] = "Thaumcraft",
        ["植物魔法"] = "Botania",
        ["匠魂"] = "Tinkers' Construct",
        ["暮色森林"] = "Twilight Forest",
        ["农夫乐事"] = "Farmer's Delight",
        ["精妙背包"] = "Sophisticated Backpacks",
        ["苹果皮"] = "AppleSkin",
        ["旅行地图"] = "JourneyMap",
        ["物品管理器"] = "Just Enough Items",
        ["高清修复"] = "OptiFine",
        ["小地图"] = "Xaero's Minimap"
    };

    /// <summary>
    /// True when the result is the project the query is actually asking for, e.g. "钠" → "Sodium".
    /// Used to float the real mod above localisation packs and detector mods that merely mention its name.
    /// </summary>
    public static bool MatchesCanonicalProject(string? title, string? query)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (!CanonicalProjects.TryGetValue(query.Trim(), out var canonical))
        {
            return false;
        }

        var normalizedTitle = title.Trim();
        return string.Equals(normalizedTitle, canonical, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the English search term for the query, or the query itself when nothing is known.</summary>
    public static string Translate(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        var trimmed = query.Trim();
        if (Vocabulary.TryGetValue(trimmed, out var whole))
        {
            return whole;
        }

        var tokens = trimmed.Split([' ', '　', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var translated = new List<string>(tokens.Length);
        var changed = false;

        foreach (var token in tokens)
        {
            if (Vocabulary.TryGetValue(token, out var mapped))
            {
                translated.Add(mapped);
                changed = true;
            }
            else
            {
                translated.Add(token);
            }
        }

        return changed ? string.Join(' ', translated) : trimmed;
    }

    /// <summary>Search terms worth trying for a query: the original and its English translation.</summary>
    public static IReadOnlyList<string> Expand(string query)
    {
        var translated = Translate(query);
        if (string.IsNullOrWhiteSpace(translated) ||
            string.Equals(translated, query?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return [query ?? string.Empty];
        }

        return [query!.Trim(), translated];
    }

    /// <summary>True when the text contains CJK characters, i.e. the query needs translation.</summary>
    public static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF)
            {
                return true;
            }
        }

        return false;
    }
}
