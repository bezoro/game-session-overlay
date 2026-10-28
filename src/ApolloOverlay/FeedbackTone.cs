using System.IO;

namespace ApolloOverlay;

/// <summary>Short, quiet earcons with zero-ended envelopes, generated without external assets.</summary>
internal static class FeedbackTone
{
    internal const int SampleRate = 24000;

    internal static byte[] Wave(FeedbackCue cue)
    {
        var notes = cue switch
        {
            FeedbackCue.Navigation => new[] { (0, 28, 720d, 1050d) },
            FeedbackCue.Confirmation => [(0, 34, 660d, 1250d), (28, 48, 990d, 1050d)],
            FeedbackCue.Completion => [(0, 32, 660d, 1100d), (28, 32, 880d, 1000d), (56, 48, 1100d, 950d)],
            FeedbackCue.Cancellation => [(0, 32, 660d, 1050d), (26, 42, 440d, 950d)],
            FeedbackCue.Unavailable => [(0, 35, 260d, 1100d), (55, 40, 220d, 950d)],
            _ => [(0, 28, 720d, 1050d), (100, 34, 660d, 1250d), (128, 48, 990d, 1050d),
                (240, 32, 660d, 1050d), (266, 42, 440d, 950d)]
        };
        var samples = notes.Max(n => n.Item1 + n.Item2) * SampleRate / 1000;
        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2);
        writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            double value = 0;
            foreach (var (start, duration, frequency, amplitude) in notes)
            {
                var offset = i - start * SampleRate / 1000;
                var length = duration * SampleRate / 1000;
                if (offset < 0 || offset >= length) continue;
                var envelope = Math.Pow(Math.Sin(Math.PI * offset / (length - 1)), 2);
                value += amplitude * envelope * Math.Sin(2 * Math.PI * frequency * offset / SampleRate);
            }
            writer.Write((short)Math.Round(value));
        }
        return stream.ToArray();
    }
}
