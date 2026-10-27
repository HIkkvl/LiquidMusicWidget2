using System.Media;

namespace LiquidMusic;

internal enum VoiceCue { Listening, Accepted }

internal static class VoiceSounds
{
    private static readonly byte[] listening = MakeWave([(660, 110)]);
    private static readonly byte[] accepted = MakeWave([(880, 75), (1100, 100)]);

    public static async Task PlayAsync(VoiceCue cue)
    {
        // No window activation. Finish the ready tone before opening the microphone.
        await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(cue == VoiceCue.Listening ? listening : accepted, false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception error) { AppPaths.Log("Voice cue: " + error.Message); }
        });
    }

    private static byte[] MakeWave((int Frequency, int Milliseconds)[] notes)
    {
        const int rate = 44100, gap = rate * 30 / 1000;
        using var pcm = new MemoryStream();
        using (var samples = new BinaryWriter(pcm, System.Text.Encoding.UTF8, true))
        {
            for (var noteIndex = 0; noteIndex < notes.Length; noteIndex++)
            {
                var (frequency, milliseconds) = notes[noteIndex];
                var count = rate * milliseconds / 1000;
                for (var i = 0; i < count; i++)
                {
                    var envelope = Math.Min(1.0, Math.Min(i, count - 1 - i) / (rate * .008));
                    samples.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * short.MaxValue * .18));
                }
                if (noteIndex + 1 < notes.Length)
                    for (var i = 0; i < gap; i++) samples.Write((short)0);
            }
        }
        using var wave = new MemoryStream();
        using var writer = new BinaryWriter(wave);
        writer.Write("RIFF"u8); writer.Write((int)pcm.Length + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write((int)pcm.Length); writer.Write(pcm.ToArray());
        return wave.ToArray();
    }
}
