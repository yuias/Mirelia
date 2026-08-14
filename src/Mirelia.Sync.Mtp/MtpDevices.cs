using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Devices.PortableDevices;
using Windows.Win32.Foundation;

namespace Mirelia.Sync.Mtp;

/// <summary>One portable device Windows can currently see.</summary>
/// <param name="DeviceId">
/// Plug-and-Play device id. Stable across reconnects for the same physical
/// device, which is why a target stores this rather than a friendly name.
/// </param>
/// <param name="Label">Friendly name, for display.</param>
public readonly record struct MtpDeviceInfo(string DeviceId, string Label);

/// <summary>Finds portable devices and opens sessions on them.</summary>
/// <remarks>
/// Windows only, and the caller is expected to have checked. Everything here
/// runs through the WPD COM API, which has no counterpart on other platforms.
/// </remarks>
[SupportedOSPlatform("windows5.0")]
public static unsafe class MtpDevices
{
    /// <summary>
    /// Every connected device, in the order Windows reports them. Empty when
    /// nothing is plugged in — which is the normal case, not an error.
    /// </summary>
    public static IReadOnlyList<MtpDeviceInfo> List()
    {
        var manager = (IPortableDeviceManager)new PortableDeviceManager();
        try
        {
            uint count = 0;
            manager.GetDevices(null, ref count);
            if (count == 0)
            {
                return [];
            }

            var ids = new PWSTR[count];
            fixed (PWSTR* buffer = ids)
            {
                manager.GetDevices(buffer, ref count);
            }

            var devices = new List<MtpDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                string deviceId = ids[i].ToString();
                Marshal.FreeCoTaskMem((nint)ids[i].Value);
                devices.Add(new MtpDeviceInfo(deviceId, FriendlyName(manager, deviceId)));
            }

            return devices;
        }
        catch (COMException)
        {
            // No WPD service, or it failed to answer: no devices is the honest
            // answer, and the target list already renders an absent device.
            return [];
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>
    /// Opens a session on a device. The returned store, and every sink built on
    /// it, belongs to the calling thread.
    /// </summary>
    /// <param name="deviceId">Plug-and-Play device id from <see cref="List"/>.</param>
    /// <param name="label">Cached friendly name, used when the device is absent.</param>
    /// <exception cref="MtpDeviceLostException">The device is not connected.</exception>
    public static IMtpObjectStore Open(string deviceId, string label)
        => new WpdObjectStore(deviceId, label);

    /// <summary>
    /// Reads a device's friendly name, which WPD returns into a caller-supplied
    /// buffer rather than allocating one.
    /// </summary>
    private static string FriendlyName(IPortableDeviceManager manager, string deviceId)
    {
        try
        {
            fixed (char* id = deviceId)
            {
                uint length = 0;
                manager.GetDeviceFriendlyName(id, default, ref length);
                if (length == 0)
                {
                    return deviceId;
                }

                var name = new char[length];
                fixed (char* buffer = name)
                {
                    manager.GetDeviceFriendlyName(id, buffer, ref length);
                }

                return new string(name).TrimEnd('\0');
            }
        }
        catch (COMException)
        {
            // Some drivers report no name at all; the raw id still identifies it.
            return deviceId;
        }
    }
}
