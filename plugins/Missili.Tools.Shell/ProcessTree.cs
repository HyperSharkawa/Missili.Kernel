using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Missili.Tools.Shell;

/// <summary>停止普通受管子进程并等待已捕获的进程退出，不作为主动脱管命令的沙盒。</summary>
internal static class ProcessTree
{
    /// <summary>在杀死父进程前保留后代句柄，避免只等待父进程就释放工具资格。</summary>
    public static async Task StopAsync(Process root)
    {
        List<Process> children = [];
        Exception? failure = null;
        try
        {
            try { children = CaptureChildren(root.Id); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            { failure = exception; }
            try { if (!root.HasExited) root.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { failure ??= exception; }
            foreach (Process child in children)
            {
                try { if (!child.HasExited) child.Kill(); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { failure ??= exception; }
            }
            await root.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (Process child in children) await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (failure is not null) throw new IOException("部分进程树操作失败；已经等待可跟踪的工作退出。", failure);
        }
        finally { foreach (Process child in children) child.Dispose(); }
    }

    /// <summary>只取得当前父子关系中的后代；无关进程不会被持有或终止。</summary>
    private static List<Process> CaptureChildren(int parent)
    {
        Dictionary<int, List<int>> hierarchy = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        List<Process> processes = [];
        Queue<int> pending = new();
        HashSet<int> visited = [parent];
        pending.Enqueue(parent);
        try
        {
            while (pending.TryDequeue(out int id))
            {
                if (!hierarchy.TryGetValue(id, out List<int>? children)) continue;
                foreach (int childId in children)
                {
                    if (!visited.Add(childId)) continue;
                    pending.Enqueue(childId);
                    try
                    {
                        Process child = Process.GetProcessById(childId);
                        try { _ = child.SafeHandle; processes.Add(child); }
                        catch { child.Dispose(); throw; }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            return processes;
        }
        catch { foreach (Process process in processes) process.Dispose(); throw; }
    }

    /// <summary>通过 Toolhelp 读取进程关系，不读取命令行或环境变量。
    /// 依据：https://learn.microsoft.com/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes 。</summary>
    private static Dictionary<int, List<int>> WindowsParents()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        ProcessEntry entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry>(), FileName = "" };
        Dictionary<int, List<int>> result = [];
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { Add(result, checked((int)entry.ParentId), checked((int)entry.ProcessId)); }
        while (Process32Next(snapshot, ref entry));
        int error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error);
        return result;
    }

    /// <summary>Linux 只读取 procfs 的 PPid；进程已退出或无权读取的条目不能成为错误的目标。</summary>
    private static Dictionary<int, List<int>> LinuxParents()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("local shell 进程树管理目前支持 Windows 和 Linux。");
        Dictionary<int, List<int>> result = [];
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int id)) continue;
            try
            {
                string? line = File.ReadLines(Path.Combine(directory, "status")).FirstOrDefault(value => value.StartsWith("PPid:", StringComparison.Ordinal));
                if (line is not null && int.TryParse(line.AsSpan(5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parent)) Add(result, parent, id);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>构造有限快照中的父子索引，后续只遍历当前工具根进程的子树。</summary>
    private static void Add(Dictionary<int, List<int>> hierarchy, int parent, int child)
    {
        if (!hierarchy.TryGetValue(parent, out List<int>? children)) hierarchy[parent] = children = [];
        children.Add(child);
    }

    /// <summary>Windows 原生进程条目布局，只用于本次快照。
    /// 依据：https://learn.microsoft.com/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w 。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr HeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
    }

    /// <summary>创建进程快照，安全句柄负责关闭内核资源。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    /// <summary>读取快照首条记录，避免用 shell 命令拼接进程管理操作。</summary>
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    /// <summary>依次读取剩余进程关系，末尾使用系统约定的错误码表示结束。</summary>
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
