namespace TalkLens.Transcription;

/// <summary>100 ms of 16 kHz mono PCM16 stamped with the talk clock.</summary>
public sealed record AudioFrame(byte[] Pcm, long TalkMs)
{
    public const int SampleRate = 16000;
    public const int Bytes = SampleRate / 10 * 2;
    public const double VoiceRms = 0.01;

    public double Rms()
    {
        double sum = 0;
        for (var i = 0; i + 1 < Pcm.Length; i += 2)
        {
            double s = BitConverter.ToInt16(Pcm, i) / 32768.0;
            sum += s * s;
        }
        return Math.Sqrt(sum / (Pcm.Length / 2));
    }

    public bool HasVoice => Rms() > VoiceRms;
}

/// <param name="Key">Stable per caption, so partials replace themselves on screen.</param>
public sealed record TranscriptEvent(string Key, string Text, bool Final, long StartMs, long EndMs);

public interface ITranscriber : IAsyncDisposable
{
    string Name { get; }
    /// <summary>Completes once the engine is ready to take audio; throws if it cannot.</summary>
    Task StartAsync(CancellationToken ct);
    void Feed(AudioFrame frame);
    event Action<TranscriptEvent>? Transcript;
    event Action<Exception>? Failed;
    /// <summary>Audio went to a provider that now owes an answer. Local engines never raise this.</summary>
    event Action? AnswerDue { add { } remove { } }
    /// <summary>The provider answered, even if it heard no words, as with a cough or a door.</summary>
    event Action? Answered { add { } remove { } }
}
