using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Devices.PortableDevices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Mirelia.Sync.Mtp;

/// <summary>
/// <see cref="IMtpObjectStore"/> over Windows Portable Devices. Everything COM
/// stops here: the sink above sees relative paths, and this sees object ids.
/// </summary>
/// <remarks>
/// <para>
/// The device session is not thread-safe. Every call must happen on the thread
/// that constructed the store, and that thread must be in the multi-threaded
/// apartment — which is what a .NET thread-pool thread is, so a sync running
/// on its own task costs nothing.
/// </para>
/// <para>
/// Only partly verified against a physical device — listing, transfer, delete
/// and free space have been run by hand; cancellation and disconnection have
/// not. The path translation and cache above are covered by tests; the rest of
/// this layer stays on the manual checklist in <c>docs/sync-protocol.md</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows5.0")]
internal sealed unsafe class WpdObjectStore : IMtpObjectStore
{
    /// <summary>Fallback when the driver states no preference.</summary>
    private const int DefaultChunkBytes = 256 * 1024;

    /// <summary>Object ids fetched per <c>Next</c> call.</summary>
    private const int EnumBatch = 64;

    /// <summary>
    /// Failures that mean the session itself is dead rather than one object
    /// being unwritable. Anything else is reported per file so the run goes on.
    /// </summary>
    private static readonly HashSet<int> DeviceLostResults =
    [
        unchecked((int)0x8007048FU), // ERROR_DEVICE_NOT_CONNECTED
        unchecked((int)0x8007045DU), // ERROR_IO_DEVICE
        unchecked((int)0x800701B1U), // ERROR_DEVICE_NOT_AVAILABLE
        unchecked((int)0x80070006U), // ERROR_INVALID_HANDLE
        unchecked((int)0x800706BAU), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x80010108U), // RPC_E_DISCONNECTED
    ];

    private readonly IPortableDevice _device;
    private readonly IPortableDeviceContent _content;
    private readonly IPortableDeviceProperties _properties;
    private readonly IPortableDeviceKeyCollection _objectKeys;
    private readonly IPortableDeviceKeyCollection _storageKeys;
    private bool _closed;

    /// <summary>Opens a session on the device named by its Plug-and-Play id.</summary>
    internal WpdObjectStore(string deviceId, string deviceLabel)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "A portable device session must be opened on an MTA thread.");
        }

        DeviceLabel = deviceLabel;
        _device = (IPortableDevice)new PortableDeviceFTM();

        var clientInfo = (IPortableDeviceValues)new PortableDeviceValues();
        SetString(clientInfo, PInvoke.WPD_CLIENT_NAME, "Mirelia");
        SetUnsignedInteger(clientInfo, PInvoke.WPD_CLIENT_MAJOR_VERSION, 1);
        SetUnsignedInteger(clientInfo, PInvoke.WPD_CLIENT_MINOR_VERSION, 0);
        SetUnsignedInteger(clientInfo, PInvoke.WPD_CLIENT_REVISION, 0);

        try
        {
            fixed (char* id = deviceId)
            {
                _device.Open(id, clientInfo);
            }

            _device.Content(out _content);
            _content.Properties(out _properties);
        }
        catch (COMException ex)
        {
            throw new MtpDeviceLostException($"Could not open '{deviceLabel}'.", ex);
        }

        _objectKeys = KeyCollection(
            PInvoke.WPD_OBJECT_ORIGINAL_FILE_NAME,
            PInvoke.WPD_OBJECT_NAME,
            PInvoke.WPD_OBJECT_CONTENT_TYPE,
            PInvoke.WPD_OBJECT_SIZE);
        _storageKeys = KeyCollection(PInvoke.WPD_STORAGE_FREE_SPACE_IN_BYTES);
    }

    /// <inheritdoc />
    public string DeviceLabel { get; }

    /// <inheritdoc />
    public string DeviceObjectId => PInvoke.WPD_DEVICE_OBJECT_ID;

    /// <inheritdoc />
    public IReadOnlyList<MtpObject> GetChildren(string parentObjectId)
    {
        try
        {
            var children = new List<MtpObject>();
            foreach (string id in ChildIds(parentObjectId))
            {
                children.Add(Describe(id));
            }

            return children;
        }
        catch (COMException ex)
        {
            throw Translate(ex, parentObjectId);
        }
    }

    /// <inheritdoc />
    public string CreateFolder(string parentObjectId, string name)
    {
        try
        {
            var values = (IPortableDeviceValues)new PortableDeviceValues();
            SetString(values, PInvoke.WPD_OBJECT_PARENT_ID, parentObjectId);
            SetString(values, PInvoke.WPD_OBJECT_NAME, name);
            SetString(values, PInvoke.WPD_OBJECT_ORIGINAL_FILE_NAME, name);
            SetGuid(values, PInvoke.WPD_OBJECT_CONTENT_TYPE, PInvoke.WPD_CONTENT_TYPE_FOLDER);

            PWSTR created = default;
            _content.CreateObjectWithPropertiesOnly(values, &created);
            return Consume(created);
        }
        catch (COMException ex)
        {
            throw Translate(ex, $"{parentObjectId}/{name}");
        }
    }

    /// <inheritdoc />
    public string CreateFile(
        string parentObjectId, string name, Stream source, long sizeBytes, CancellationToken ct)
    {
        try
        {
            return Transfer(parentObjectId, name, source, sizeBytes, ct);
        }
        catch (COMException ex)
        {
            throw Translate(ex, $"{parentObjectId}/{name}");
        }
    }

    /// <inheritdoc />
    public void Delete(string objectId)
    {
        PROPVARIANT value = default;
        value.Anonymous.Anonymous.vt = VARENUM.VT_LPWSTR;
        value.Anonymous.Anonymous.Anonymous.pwszVal = new PWSTR(Marshal.StringToCoTaskMemUni(objectId));

        try
        {
            var ids = (IPortableDevicePropVariantCollection)new PortableDevicePropVariantCollection();
            ids.Add(in value);

            // WPD reports per-object outcomes in the results collection rather
            // than in the HRESULT, and this asks for exactly one object; a
            // delete that silently did nothing shows up as the file still being
            // listed on the next run.
            IPortableDevicePropVariantCollection results = null!;
            _content.Delete(
                (uint)DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_WITH_RECURSION, ids, ref results);
        }
        catch (COMException ex)
        {
            throw Translate(ex, objectId);
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
        }
    }

    /// <inheritdoc />
    public long? GetFreeSpaceBytes(string storageObjectId)
    {
        try
        {
            IPortableDeviceValues values;
            fixed (char* id = storageObjectId)
            {
                _properties.GetValues(id, _storageKeys, out values);
            }

            ulong? free = TryGetUnsignedLargeInteger(values, PInvoke.WPD_STORAGE_FREE_SPACE_IN_BYTES);
            return free is { } bytes ? (long)bytes : null;
        }
        catch (COMException ex) when (!IsDeviceLost(ex))
        {
            // Not every driver exposes the property; the preflight summary
            // already handles a destination that cannot report its space.
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _device.Close();
        }
        catch (COMException)
        {
            // A device unplugged mid-run cannot be closed politely.
        }

        Marshal.ReleaseComObject(_objectKeys);
        Marshal.ReleaseComObject(_storageKeys);
        Marshal.ReleaseComObject(_properties);
        Marshal.ReleaseComObject(_content);
        Marshal.ReleaseComObject(_device);
    }

    private string Transfer(
        string parentObjectId, string name, Stream source, long sizeBytes, CancellationToken ct)
    {
        var values = (IPortableDeviceValues)new PortableDeviceValues();
        SetString(values, PInvoke.WPD_OBJECT_PARENT_ID, parentObjectId);
        SetString(values, PInvoke.WPD_OBJECT_NAME, name);
        SetString(values, PInvoke.WPD_OBJECT_ORIGINAL_FILE_NAME, name);
        SetUnsignedLargeInteger(values, PInvoke.WPD_OBJECT_SIZE, (ulong)sizeBytes);
        SetGuid(values, PInvoke.WPD_OBJECT_CONTENT_TYPE, PInvoke.WPD_CONTENT_TYPE_GENERIC_FILE);
        SetGuid(values, PInvoke.WPD_OBJECT_FORMAT, PInvoke.WPD_OBJECT_FORMAT_UNSPECIFIED);

        // The object does not exist until Commit, so a run that dies here
        // leaves nothing for the next one to mistake for a complete file.
        uint optimal = 0;
        _content.CreateObjectWithPropertiesAndData(values, out IStream raw, ref optimal, null);
        var stream = (IPortableDeviceDataStream)raw;

        try
        {
            // The driver's own preference; ignoring it costs real throughput.
            var buffer = new byte[optimal > 0 ? optimal : DefaultChunkBytes];
            for (int read = source.Read(buffer); read > 0; read = source.Read(buffer))
            {
                ct.ThrowIfCancellationRequested();
                fixed (byte* chunk = buffer)
                {
                    uint written = 0;
                    stream.Write(chunk, (uint)read, &written).ThrowOnFailure();
                    if (written != read)
                    {
                        throw new IOException(
                            $"'{name}' on {DeviceLabel}: the device took {written} of {read} bytes.");
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            stream.Commit(STGC.STGC_DEFAULT);

            PWSTR created = default;
            stream.GetObjectID(&created);
            return Consume(created);
        }
        catch
        {
            try
            {
                stream.Revert();
            }
            catch (COMException)
            {
                // Best effort: the transfer already failed.
            }

            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    private List<string> ChildIds(string parentObjectId)
    {
        IEnumPortableDeviceObjectIDs enumerator;
        fixed (char* parent = parentObjectId)
        {
            _content.EnumObjects(0, parent, null, out enumerator);
        }

        try
        {
            var ids = new List<string>();
            var batch = new PWSTR[EnumBatch];
            while (true)
            {
                uint fetched = 0;
                HRESULT hr = enumerator.Next(batch, ref fetched);
                hr.ThrowOnFailure();
                for (uint i = 0; i < fetched; i++)
                {
                    ids.Add(Consume(batch[i]));
                }

                // S_FALSE means the enumeration ran out inside this batch.
                if (hr.Value != 0 || fetched == 0)
                {
                    return ids;
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private MtpObject Describe(string objectId)
    {
        IPortableDeviceValues values;
        fixed (char* id = objectId)
        {
            _properties.GetValues(id, _objectKeys, out values);
        }

        // The original file name is the one the sanitizer produced; the object
        // name is a display label some drivers set to something else entirely.
        string name = TryGetString(values, PInvoke.WPD_OBJECT_ORIGINAL_FILE_NAME)
            ?? TryGetString(values, PInvoke.WPD_OBJECT_NAME)
            ?? objectId;

        Guid contentType = TryGetGuid(values, PInvoke.WPD_OBJECT_CONTENT_TYPE) ?? Guid.Empty;

        // Storages are functional objects rather than folders, and the sink
        // reaches its music root through one.
        bool isFolder = contentType == PInvoke.WPD_CONTENT_TYPE_FOLDER
            || contentType == PInvoke.WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT;

        long size = isFolder
            ? 0
            : (long)(TryGetUnsignedLargeInteger(values, PInvoke.WPD_OBJECT_SIZE) ?? 0);

        return new MtpObject(objectId, name, isFolder, size);
    }

    private static IPortableDeviceKeyCollection KeyCollection(params PROPERTYKEY[] keys)
    {
        var collection = (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
        foreach (PROPERTYKEY key in keys)
        {
            PROPERTYKEY copy = key;
            collection.Add(&copy);
        }

        return collection;
    }

    private static void SetString(IPortableDeviceValues values, PROPERTYKEY key, string value)
    {
        PROPERTYKEY copy = key;
        fixed (char* text = value)
        {
            values.SetStringValue(&copy, text);
        }
    }

    private static void SetGuid(IPortableDeviceValues values, PROPERTYKEY key, Guid value)
    {
        PROPERTYKEY copy = key;
        values.SetGuidValue(&copy, &value);
    }

    private static void SetUnsignedInteger(IPortableDeviceValues values, PROPERTYKEY key, uint value)
    {
        PROPERTYKEY copy = key;
        values.SetUnsignedIntegerValue(&copy, value);
    }

    private static void SetUnsignedLargeInteger(
        IPortableDeviceValues values, PROPERTYKEY key, ulong value)
    {
        PROPERTYKEY copy = key;
        values.SetUnsignedLargeIntegerValue(&copy, value);
    }

    private static string? TryGetString(IPortableDeviceValues values, PROPERTYKEY key)
    {
        PROPERTYKEY copy = key;
        PWSTR text = default;
        try
        {
            values.GetStringValue(&copy, &text);
        }
        catch (COMException ex) when (!IsDeviceLost(ex))
        {
            return null;
        }

        string result = Consume(text);
        return result.Length == 0 ? null : result;
    }

    private static Guid? TryGetGuid(IPortableDeviceValues values, PROPERTYKEY key)
    {
        PROPERTYKEY copy = key;
        Guid value;
        try
        {
            values.GetGuidValue(&copy, &value);
        }
        catch (COMException ex) when (!IsDeviceLost(ex))
        {
            return null;
        }

        return value;
    }

    private static ulong? TryGetUnsignedLargeInteger(IPortableDeviceValues values, PROPERTYKEY key)
    {
        PROPERTYKEY copy = key;
        try
        {
            values.GetUnsignedLargeIntegerValue(&copy, out ulong value);
            return value;
        }
        catch (COMException ex) when (!IsDeviceLost(ex))
        {
            return null;
        }
    }

    /// <summary>Reads a WPD-allocated string and releases it.</summary>
    private static string Consume(PWSTR text)
    {
        if (text.Value is null)
        {
            return string.Empty;
        }

        try
        {
            return text.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)text.Value);
        }
    }

    private static bool IsDeviceLost(COMException ex) => DeviceLostResults.Contains(ex.HResult);

    /// <summary>
    /// Turns a WPD failure into something sync already understands. Per-object
    /// failures become <see cref="IOException"/>, which the runner reports and
    /// steps over; a dead session is not survivable and keeps its own type.
    /// </summary>
    private Exception Translate(COMException ex, string what) => IsDeviceLost(ex)
        ? new MtpDeviceLostException($"{DeviceLabel} is no longer reachable.", ex)
        : new IOException(
            $"'{what}' on {DeviceLabel} failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}", ex);
}
