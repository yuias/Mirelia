namespace Mirelia.Sync.Mtp;

/// <summary>
/// The device went away mid-run: unplugged, powered off, or its session
/// otherwise dead.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="IOException"/>. Sync treats those as one bad
/// file and carries on, which is right for a file the device refused and wrong
/// here — every remaining file would fail the same way.
/// </remarks>
public sealed class MtpDeviceLostException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public MtpDeviceLostException()
        : base("The portable device is no longer reachable.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public MtpDeviceLostException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure beneath it.</summary>
    public MtpDeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
