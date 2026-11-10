namespace Mirelia.AudioBridge;

/// <summary>Thrown when a native audio-core call fails.</summary>
public sealed class AudioEngineException : Exception
{
    /// <summary>Creates an exception for a failed native call.</summary>
    /// <param name="result">The native result code.</param>
    /// <param name="detail">The backend's last-error text, if any.</param>
    public AudioEngineException(MireliaResult result, string detail)
        : base(string.IsNullOrEmpty(detail) ? $"Audio core call failed: {result}." : $"Audio core call failed: {result}. {detail}")
    {
        Result = result;
    }

    /// <summary>Creates an exception that wraps a lower-level failure.</summary>
    /// <param name="result">The native result code the failure maps to.</param>
    /// <param name="message">The message to surface.</param>
    /// <param name="inner">The underlying exception.</param>
    public AudioEngineException(MireliaResult result, string message, Exception inner)
        : base(message, inner)
    {
        Result = result;
    }

    /// <summary>The native result code of the failed call.</summary>
    public MireliaResult Result { get; }
}
