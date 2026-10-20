using UnityEngine;

namespace NebulaeWorld.MonoBehaviours.Local.Chat;

/// <summary>Chinese glyphs for mod-owned chat UI, including messages received in other languages.</summary>
internal static class ChatLocalization
{
    private static Font chineseFont;

    public static Font Font
    {
        get
        {
            if (chineseFont == null)
            {
                chineseFont = UnityEngine.Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "WenQuanYi Micro Hei" }, 20);
                Object.DontDestroyOnLoad(chineseFont);
            }
            return chineseFont;
        }
    }
}
