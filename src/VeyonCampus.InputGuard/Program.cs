using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VeyonCampus.InputGuard;

internal static class Program
{
    private static readonly TimeSpan MaximumLease = TimeSpan.FromSeconds(20);
    private const string ConfirmationPhrase = "开始20秒锁定测试";

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("此原型仅支持 Windows 10/11。");
            return 2;
        }

        if (args.SequenceEqual(["--guard-server"], StringComparer.Ordinal))
        {
            try
            {
                using var shutdown = new CancellationTokenSource();
                new StudentInputGuardServer().RunAsync(shutdown.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                              System.Security.SecurityException or ArgumentException)
            {
                GuardDiagnostics.Write("ServerExited", exception);
                return 1;
            }
        }

        if (!args.SequenceEqual(["--run-prototype-once"], StringComparer.Ordinal))
        {
            Console.WriteLine("Student InputGuard 本机可行性原型");
            Console.WriteLine("仅用于独立测试；不连接教师端，不会自动启动或持续锁定。");
            Console.WriteLine("运行参数：VeyonCampus.StudentInputGuard.exe --run-prototype-once");
            return 0;
        }

        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId <= 0)
        {
            Console.Error.WriteLine("拒绝运行：必须从已登录学生的交互桌面启动，不能从 Session 0 或后台任务启动。");
            return 3;
        }

        Console.WriteLine("即将进行最多 20 秒的键盘和鼠标输入阻止测试。");
        Console.WriteLine("请先保存工作，并确认视频与耳机音频正在播放。Ctrl+Alt+Delete 可让 Windows 提前恢复输入。");
        Console.WriteLine("本程序不修改音量、视频、设备、驱动或系统策略；到期由原锁定线程尝试解除。");
        Console.Write($"输入“{ConfirmationPhrase}”后按 Enter 才会开始；其他输入退出：");
        if (!string.Equals(Console.ReadLine(), ConfirmationPhrase, StringComparison.Ordinal))
        {
            Console.WriteLine("已取消，没有阻止输入。");
            return 0;
        }

        var lockThread = new Thread(RunBoundedLease)
        {
            IsBackground = false,
            Name = "InputGuard prototype lease",
            Priority = ThreadPriority.Normal
        };
        lockThread.SetApartmentState(ApartmentState.STA);
        lockThread.Start();
        lockThread.Join();
        return 0;
    }

    private static void RunBoundedLease()
    {
        if (!NativeMethods.BlockInput(true))
        {
            Console.Error.WriteLine($"Windows 未接受输入阻止请求（错误码 {Marshal.GetLastWin32Error()}）。");
            return;
        }

        Console.WriteLine("输入阻止请求已被 Windows 接受；最长 20 秒后由此线程解除。");
        var start = Stopwatch.GetTimestamp();
        var leaseTicks = (long)(MaximumLease.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (Stopwatch.GetTimestamp() - start < leaseTicks)
            {
                var elapsedTicks = Stopwatch.GetTimestamp() - start;
                var remainingTicks = leaseTicks - elapsedTicks;
                var sleepMilliseconds = (int)Math.Clamp(
                    remainingTicks * 1000 / Stopwatch.Frequency, 1, 100);
                Thread.Sleep(sleepMilliseconds);
            }
        }
        finally
        {
            if (NativeMethods.BlockInput(false))
            {
                Console.WriteLine("20 秒租约结束；原锁定线程已解除输入阻止。");
            }
            else
            {
                Console.WriteLine("20 秒租约结束；解除调用未报告成功（Windows 可能已通过安全桌面提前恢复）。请人工确认键盘和鼠标可用。");
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool block);
    }
}
