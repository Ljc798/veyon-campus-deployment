using System.Runtime.InteropServices;

namespace VeyonCampus.App;

internal enum MacTeacherLoginStartState
{
    Unavailable,
    NotRegistered,
    Enabled,
    RequiresApproval,
    NotFound,
    Failed
}

internal sealed record MacTeacherLoginStartSnapshot(MacTeacherLoginStartState State, string Message,
    bool IsPackagedApp);

/// <summary>Controls whether the bundled TeacherConsole app opens for the current user at login.</summary>
internal static class MacTeacherLoginStartService
{
    private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string ServiceManagementPath = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";
    private const string ObjectiveCRuntimePath = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<IntPtr> Foundation = new(() => NativeLibrary.Load(FoundationPath));
    private static readonly Lazy<IntPtr> ServiceManagement = new(() => NativeLibrary.Load(ServiceManagementPath));

    public static bool IsPackagedApp => FindAppBundleRoot() is not null;

    public static MacTeacherLoginStartSnapshot ReadStatus()
    {
        if (!OperatingSystem.IsMacOS() || !OperatingSystem.IsMacOSVersionAtLeast(13))
            return new(MacTeacherLoginStartState.Unavailable,
                "登录启动需要 macOS 13 或更新版本。", IsPackagedApp: false);

        if (FindAppBundleRoot() is null)
            return new(MacTeacherLoginStartState.Unavailable,
                "请从 Veyon Campus 教师端 .app 启动后再设置登录启动。", IsPackagedApp: false);

        try
        {
            using var pool = new AutoreleasePool();
            return Snapshot(ReadNativeStatus(CreateMainAppService()), isPackagedApp: true);
        }
        catch (Exception exception) when (IsNativeInteropFailure(exception))
        {
            return new(MacTeacherLoginStartState.Failed,
                "读取 macOS 登录启动状态失败：" + exception.Message, IsPackagedApp: true);
        }
    }

    public static MacTeacherLoginStartSnapshot SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsMacOS() || !OperatingSystem.IsMacOSVersionAtLeast(13))
            return new(MacTeacherLoginStartState.Unavailable,
                "登录启动需要 macOS 13 或更新版本。", IsPackagedApp: false);

        if (FindAppBundleRoot() is null)
            return new(MacTeacherLoginStartState.Unavailable,
                "请从 Veyon Campus 教师端 .app 启动后再设置登录启动。", IsPackagedApp: false);

        try
        {
            using var pool = new AutoreleasePool();
            var service = CreateMainAppService();
            var currentStatus = ReadNativeStatus(service);
            if (enabled && currentStatus == 1)
                return Snapshot(currentStatus, isPackagedApp: true);
            if (enabled && currentStatus == 2)
            {
                OpenLoginItemsSettings();
                return Snapshot(currentStatus, isPackagedApp: true);
            }
            if (!enabled && currentStatus == 0)
                return Snapshot(currentStatus, isPackagedApp: true);

            var error = IntPtr.Zero;
            var succeeded = SendBooleanWithError(service,
                Selector(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), out error) != 0;
            var updatedStatus = ReadNativeStatus(service);

            if (enabled && updatedStatus == 2)
            {
                OpenLoginItemsSettings();
                return Snapshot(updatedStatus, isPackagedApp: true);
            }
            if (!enabled && updatedStatus == 0)
                return Snapshot(updatedStatus, isPackagedApp: true);
            if (!succeeded)
                return new(MacTeacherLoginStartState.Failed,
                    NativeErrorMessage(error, enabled), IsPackagedApp: true);

            return Snapshot(updatedStatus, isPackagedApp: true);
        }
        catch (Exception exception) when (IsNativeInteropFailure(exception))
        {
            return new(MacTeacherLoginStartState.Failed,
                "修改 macOS 登录启动失败：" + exception.Message, IsPackagedApp: true);
        }
    }

    private static long ReadNativeStatus(IntPtr service) =>
        SendInteger(service, Selector("status"));

    private static IntPtr CreateMainAppService()
    {
        _ = Foundation.Value;
        _ = ServiceManagement.Value;
        var serviceClass = GetClass("SMAppService");
        if (serviceClass == IntPtr.Zero)
            throw new InvalidOperationException("macOS 未提供 SMAppService。");
        var service = SendObject(serviceClass, Selector("mainAppService"));
        return service != IntPtr.Zero
            ? service
            : throw new InvalidOperationException("macOS 没有返回教师端登录项服务。");
    }

    private static void OpenLoginItemsSettings()
    {
        var serviceClass = GetClass("SMAppService");
        if (serviceClass != IntPtr.Zero)
            SendVoid(serviceClass, Selector("openSystemSettingsLoginItems"));
    }

    private static MacTeacherLoginStartSnapshot Snapshot(long nativeStatus, bool isPackagedApp) => nativeStatus switch
    {
        0 => new(MacTeacherLoginStartState.NotRegistered,
            "登录启动已关闭。开启后，TeacherConsole 会在当前 macOS 用户登录时启动；手机服务是否恢复仍由手机控制页的自动恢复设置决定。",
            isPackagedApp),
        1 => new(MacTeacherLoginStartState.Enabled,
            "登录启动已开启：TeacherConsole 会在当前 macOS 用户登录时启动。手机服务是否恢复仍由手机控制页的自动恢复设置决定。",
            isPackagedApp),
        2 => new(MacTeacherLoginStartState.RequiresApproval,
            "请在“系统设置 > 通用 > 登录项”中允许 Veyon Campus 教师端启动。",
            isPackagedApp),
        _ => new(MacTeacherLoginStartState.NotFound,
            "macOS 未找到教师端登录项服务；请重新放置教师端 .app 后刷新。", isPackagedApp)
    };

    private static string NativeErrorMessage(IntPtr error, bool enabled)
    {
        var detail = error == IntPtr.Zero ? "macOS 未提供具体原因。" : ReadNSErrorDescription(error);
        var signingGuidance = detail.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                              detail.Contains("签名", StringComparison.OrdinalIgnoreCase)
            ? " SMAppService 要求应用包已代码签名；请使用签名后的教师端 .app。"
            : "";
        return (enabled ? "macOS 拒绝启用登录启动：" : "macOS 拒绝关闭登录启动：") + detail + signingGuidance;
    }

    private static string ReadNSErrorDescription(IntPtr error)
    {
        var description = SendObject(error, Selector("localizedDescription"));
        if (description == IntPtr.Zero) return "macOS 未提供具体原因。";
        var utf8 = SendPointer(description, Selector("UTF8String"));
        return utf8 == IntPtr.Zero
            ? "macOS 未提供具体原因。"
            : Marshal.PtrToStringUTF8(utf8) ?? "macOS 未提供具体原因。";
    }

    private static string? FindAppBundleRoot()
    {
        DirectoryInfo? directory;
        try { directory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }

        for (var current = directory; current is not null; current = current.Parent)
        {
            if (!current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(current.FullName, "Contents", "Info.plist")) &&
                Directory.Exists(Path.Combine(current.FullName, "Contents", "MacOS")))
                return current.FullName;
        }
        return null;
    }

    private static bool IsNativeInteropFailure(Exception exception) => exception is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
        InvalidOperationException or ExternalException or ArgumentException;

    private static IntPtr GetClass(string name) => ObjCGetClass(name);
    private static IntPtr Selector(string name) => SelRegisterName(name);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjCGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_msgSend")]
    private static extern long SendInteger(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_msgSend")]
    private static extern byte SendBooleanWithError(IntPtr receiver, IntPtr selector, out IntPtr error);

    [DllImport(ObjectiveCRuntimePath, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    private sealed class AutoreleasePool : IDisposable
    {
        private IntPtr _pool;

        public AutoreleasePool()
        {
            _ = Foundation.Value;
            var poolClass = GetClass("NSAutoreleasePool");
            if (poolClass == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 macOS 自动释放池。");
            var allocated = SendObject(poolClass, Selector("alloc"));
            _pool = allocated == IntPtr.Zero ? IntPtr.Zero : SendObject(allocated, Selector("init"));
            if (_pool == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 macOS 自动释放池。");
        }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, IntPtr.Zero);
            if (pool != IntPtr.Zero) SendVoid(pool, Selector("drain"));
        }
    }
}
