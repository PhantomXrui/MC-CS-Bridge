using System.Runtime.InteropServices;

namespace MinecraftBridge;

/// <summary>插件侧语音播放：直接调用 winmm 播 WAV（与模组侧的 Voice.java 共用同一套素材）。</summary>
public sealed partial class MinecraftBridgePlugin
{
    const uint SND_ASYNC = 0x0001, SND_FILENAME = 0x20000, SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    void PlayVoice(string file)
    {
        try
        {
            var path = Path.Combine(Root, "docs", "voice-lines", "audio", file);
            if (!File.Exists(path)) { Log("voice missing: " + path); return; }
            Log("voice " + file);
            PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception e) { Log("voice failed: " + e.Message); }
    }
}
