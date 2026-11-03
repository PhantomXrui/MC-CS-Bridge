package dev.rehan.passthrough;

import javax.sound.sampled.AudioInputStream;
import javax.sound.sampled.AudioSystem;
import javax.sound.sampled.Clip;
import javax.sound.sampled.LineEvent;
import java.io.File;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 播放比特语音。素材放在 docs/voice-lines/audio 下（WAV 即可，Java Sound 直接播放，无需转 OGG）。
 * 目前用 Windows 中文 TTS 生成的是占位音，换成原版录音后同名覆盖即可。
 */
public final class Voice {
    private static final File DIR = new File(System.getProperty("passthrough.voiceDir", new File(System.getenv().getOrDefault("MCCS_ROOT", "."), "docs/voice-lines/audio").getPath()));
    private static final Map<String, Clip> ACTIVE = new ConcurrentHashMap<>();

    public static final String DEPLOY = "比特_T仔出动.wav";
    public static final String CAUGHT = "比特_T仔抓到他们了.wav";
    public static final String PRAISE = "比特_干得漂亮T仔.wav";
    public static final String ACE = "比特_T仔永远是我的王牌.wav";

    private Voice() {}

    public static void play(final String name) {
        try {
            File file = new File(DIR, name);
            if (!file.isFile()) {
                Passthrough.LOG.warn("Voice file missing: {}", file);
                return;
            }
            Clip previous = ACTIVE.remove(name);
            if (previous != null) {
                previous.stop();
                previous.close();
            }
            try (AudioInputStream stream = AudioSystem.getAudioInputStream(file)) {
                final Clip clip = AudioSystem.getClip();
                clip.open(stream);
                clip.start();
                ACTIVE.put(name, clip);
                clip.addLineListener(event -> {
                    if (event.getType() == LineEvent.Type.STOP) {
                        ACTIVE.remove(name);
                        clip.close();
                    }
                });
            }
        } catch (Exception e) {
            Passthrough.LOG.warn("Voice playback failed: {}", e.toString());
        }
    }
}
