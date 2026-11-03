namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    // Deliberately limited to variants of the two established F8 skeletons.
    static readonly HashSet<string> SimpleDanceModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "characters/models/tm_phoenix/tm_phoenix_variantf.vmdl",
        "characters/models/tm_phoenix/tm_phoenix_variantg.vmdl",
        "characters/models/tm_phoenix/tm_phoenix_varianth.vmdl",
        "characters/models/tm_phoenix/tm_phoenix_varianti.vmdl",
        "characters/models/tm_leet/tm_leet_variantg.vmdl",
        "characters/models/tm_leet/tm_leet_varianth.vmdl",
        "characters/models/tm_leet/tm_leet_varianti.vmdl",
    };
    readonly record struct UniqueTAgent(string Name, string Model);
    // Verified against the local VPK and T-only item definitions; interleave factions.
    static readonly UniqueTAgent[] UniqueTAgents =
    [
        new("德拉戈米尔 | 军刀", "characters/models/tm_balkan/tm_balkan_variantf.vmdl"),
        new("精锐捕兽者索尔曼 | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_varianta.vmdl"),
        new("地面叛军 | 精锐分子", "characters/models/tm_leet/tm_leet_variantg.vmdl"),
        new("枪手 | 凤凰战士", "characters/models/tm_phoenix/tm_phoenix_varianth.vmdl"),
        new("残酷的达里尔爵士（迈阿密）| 专业人士", "characters/models/tm_professional/tm_professional_varf.vmdl"),
        new("马克西姆斯 | 军刀", "characters/models/tm_balkan/tm_balkan_varianti.vmdl"),
        new("遗忘者克拉斯沃特 | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantb.vmdl"),
        new("奥西瑞斯 | 精锐分子", "characters/models/tm_leet/tm_leet_varianth.vmdl"),
        new("执行者 | 凤凰战士", "characters/models/tm_phoenix/tm_phoenix_variantf.vmdl"),
        new("残酷的达里尔爵士（沉默）| 专业人士", "characters/models/tm_professional/tm_professional_varf1.vmdl"),
        new("准备就绪的列赞 | 军刀", "characters/models/tm_balkan/tm_balkan_variantg.vmdl"),
        new("亚诺（野草） | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantc.vmdl"),
        new("沙哈马特教授 | 精锐分子", "characters/models/tm_leet/tm_leet_varianti.vmdl"),
        new("弹弓 | 凤凰战士", "characters/models/tm_phoenix/tm_phoenix_variantg.vmdl"),
        new("残酷的达里尔爵士（头盖骨）| 专业人士", "characters/models/tm_professional/tm_professional_varf2.vmdl"),
        new("黑狼 | 军刀", "characters/models/tm_balkan/tm_balkan_variantj.vmdl"),
        new("上校曼戈斯·达比西 | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantd.vmdl"),
        new("精英穆哈里克先生 | 精锐分子", "characters/models/tm_leet/tm_leet_variantf.vmdl"),
        new("街头士兵 | 凤凰战士", "characters/models/tm_phoenix/tm_phoenix_varianti.vmdl"),
        new("残酷的达里尔爵士（皇家）| 专业人士", "characters/models/tm_professional/tm_professional_varf3.vmdl"),
        new("“医生”罗曼诺夫 | 军刀", "characters/models/tm_balkan/tm_balkan_varianth.vmdl"),
        new("薇帕姐（革新派） | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variante.vmdl"),
        new("丛林反抗者 | 精锐分子", "characters/models/tm_leet/tm_leet_variantj.vmdl"),
        new("残酷的达里尔爵士（聒噪）| 专业人士", "characters/models/tm_professional/tm_professional_varf4.vmdl"),
        new("红衫列赞 | 军刀", "characters/models/tm_balkan/tm_balkan_variantk.vmdl"),
        new("捕兽者（挑衅者） | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantf.vmdl"),
        new("飞贼波兹曼 | 专业人士", "characters/models/tm_professional/tm_professional_varg.vmdl"),
        new("德拉戈米尔 | 军刀勇士", "characters/models/tm_balkan/tm_balkan_variantl.vmdl"),
        new("克拉斯沃特（三分熟） | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantb2.vmdl"),
        new("小凯夫 | 专业人士", "characters/models/tm_professional/tm_professional_varh.vmdl"),
        new("捕兽者 | 游击队", "characters/models/tm_jungle_raider/tm_jungle_raider_variantf2.vmdl"),
        new("老K | 专业人士", "characters/models/tm_professional/tm_professional_vari.vmdl"),
        new("出逃的萨莉 | 专业人士", "characters/models/tm_professional/tm_professional_varj.vmdl"),
        new("残酷的达里尔（穷鬼）| 专业人士", "characters/models/tm_professional/tm_professional_varf5.vmdl"),
    ];
    static readonly UniqueTAgent[] SimpleDanceAgents = UniqueTAgents.Where(a => SimpleDanceModels.Contains(a.Model)).ToArray();
}
