using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Net;
using System.Runtime.InteropServices;

namespace ProcessDrive;

#region Output Types

public class ProcessInfo
{
    public string Directory { get; set; } = "";
    public string Name { get; set; } = "";
    public int PID { get; set; }
    public int ParentPID { get; set; }
    public string CommandLine { get; set; } = "";
    public double MemMB { get; set; }
    public string CPU { get; set; } = "";
    public int Threads { get; set; }
    public int Handles { get; set; }
    public string StartTime { get; set; } = "";
}

public class ProcessDetail : ProcessInfo
{
    public double WorkingSetMB { get; set; }
    public double PeakWorkingSetMB { get; set; }
    public double PrivateBytesMB { get; set; }
    public double VirtualSizeMB { get; set; }
    public double PeakVirtualSizeMB { get; set; }
    public double PagedMemoryMB { get; set; }
    public double NonpagedMemoryKB { get; set; }
    public double UserCPU { get; set; }
    public double KernelCPU { get; set; }
    public double TotalCPU { get; set; }
    public int SessionId { get; set; }
    public int BasePriority { get; set; }
    public string PriorityClass { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public string Company { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string RunningTime { get; set; } = "";
    public long IOReadOps { get; set; }
    public long IOWriteOps { get; set; }
    public long IOOtherOps { get; set; }
    public double IOReadBytesMB { get; set; }
    public double IOWriteBytesMB { get; set; }
    public double IOOtherBytesMB { get; set; }
    public long PageFaults { get; set; }
}

public class ModuleInfo
{
    public string Directory { get; set; } = "";
    public string Name { get; set; } = "";
    public double SizeKB { get; set; }
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string Company { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ThreadInfo
{
    public string Directory { get; set; } = "";
    public int TID { get; set; }
    public string State { get; set; } = "";
    public string WaitReason { get; set; } = "";
    public int Priority { get; set; }
    public double CPU { get; set; }
    public string StartTime { get; set; } = "";
    public string StartAddress { get; set; } = "";
}

public class ServiceInfo
{
    public string Directory { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";
    public string StartMode { get; set; } = "";
}

public class NetworkInfo
{
    public string Directory { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string State { get; set; } = "";
}

#endregion

enum PathType { Root, Process, VirtualFolder, VirtualItem }

sealed record PathInfo(PathType Type, int Pid, string? VirtualFolder, string? VirtualItem);

[CmdletProvider("ProcessDrive", ProviderCapabilities.ShouldProcess)]
[OutputType(typeof(ProcessInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ProcessDetail), ProviderCmdlet = ProviderCmdlet.GetItem)]
[OutputType(typeof(ModuleInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ThreadInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ServiceInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(NetworkInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public class ProcessDriveProvider : NavigationCmdletProvider
{
    private const char Sep = '\\';

    private static readonly HashSet<string> VirtualFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Modules", "Threads", "Services", "Network"
    };

    private static readonly Dictionary<string, string> VirtualFolderDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Modules"]  = "Loaded DLLs and modules",
        ["Threads"]  = "Process threads",
        ["Services"] = "Associated Windows services",
        ["Network"]  = "Network connections (TCP/UDP)"
    };

    #region Path Helpers

    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Array.Empty<string>();
        // Strip drive prefix (e.g., "Proc:\")
        if (path.Contains(':'))
            path = path[(path.IndexOf(':') + 1)..];
        return path.Trim(Sep).Split(Sep, StringSplitOptions.RemoveEmptyEntries);
    }

    private static int ParsePid(string segment)
    {
        int i = segment.LastIndexOf('_');
        if (i >= 0 && int.TryParse(segment.AsSpan(i + 1), out int pid))
            return pid;
        return -1;
    }

    private static string FormatSegment(int pid, string name) => $"{name}_{pid}";

    private static bool IsRootPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var trimmed = path.Trim(Sep);
        if (trimmed.Length == 0) return true;
        // Handle "Proc:" or "Proc:\" style root
        if (trimmed.EndsWith(':')) return true;
        return false;
    }

    private static string BuildChildPath(string parentPath, string childSegment)
        => parentPath.TrimEnd(Sep) + Sep + childSegment;

    private string EnsureDrivePrefix(string path)
    {
        if (PSDriveInfo == null) return path;
        var prefix = PSDriveInfo.Name + ":\\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path;
        if (IsRootPath(path))
            return prefix;
        // Strip drive prefix from internal path if present, then re-add
        var inner = path;
        if (inner.Contains(':'))
            inner = inner[(inner.IndexOf(':') + 1)..];
        return prefix + inner.TrimStart(Sep);
    }

    private PathInfo ParsePathInfo(string path)
    {
        if (IsRootPath(path)) return new(PathType.Root, -1, null, null);

        var segments = SplitPath(path);
        var last = segments[^1];

        if (VirtualFolderNames.Contains(last))
        {
            int pid = segments.Length >= 2 ? ParsePid(segments[^2]) : -1;
            return new(PathType.VirtualFolder, pid, last, null);
        }

        int lastPid = ParsePid(last);
        if (lastPid >= 0)
            return new(PathType.Process, lastPid, null, null);

        // Virtual item (inside a virtual folder)
        if (segments.Length >= 2 && VirtualFolderNames.Contains(segments[^2]))
        {
            int pid = segments.Length >= 3 ? ParsePid(segments[^3]) : -1;
            return new(PathType.VirtualItem, pid, segments[^2], last);
        }

        return new(PathType.Root, -1, null, null);
    }

    #endregion

    #region Process Tree

    private sealed record ProcInfo(int Pid, int ParentPid, string Name, string CommandLine,
        long WorkingSetSize, int ThreadCount, int HandleCount, string CreationDate, double CpuSeconds);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private static void InvalidateCache()
    {
        lock (_cacheLock)
        {
            // Skip if cache was just rebuilt (prevents hundreds of WMI queries during recursive wildcard resolution)
            if ((DateTime.UtcNow - _cacheTime).TotalSeconds > 1)
                _cacheTime = DateTime.MinValue;
        }
    }
    private static DateTime _cacheTime;
    private static (Dictionary<int, ProcInfo> map, Dictionary<int, List<int>> children, List<int> roots) _cache;
    private static readonly object _cacheLock = new();

    private (Dictionary<int, ProcInfo> map, Dictionary<int, List<int>> children, List<int> roots) BuildTree()
    {
        lock (_cacheLock)
        {
            if (_cache.map != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                return _cache;

            var map = new Dictionary<int, ProcInfo>();
            var children = new Dictionary<int, List<int>>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, " +
                "WorkingSetSize, ThreadCount, HandleCount, CreationDate, " +
                "KernelModeTime, UserModeTime FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get())
            {
                int pid = Convert.ToInt32(obj["ProcessId"]);
                int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                string name = obj["Name"]?.ToString() ?? "unknown";
                string cmdLine = obj["CommandLine"]?.ToString() ?? "";
                long ws = Convert.ToInt64(obj["WorkingSetSize"] ?? 0);
                int threads = Convert.ToInt32(obj["ThreadCount"] ?? 0);
                int handles = Convert.ToInt32(obj["HandleCount"] ?? 0);
                string creation = obj["CreationDate"]?.ToString() ?? "";
                // KernelModeTime + UserModeTime are in 100-nanosecond units
                long kernel = Convert.ToInt64(obj["KernelModeTime"] ?? 0);
                long user = Convert.ToInt64(obj["UserModeTime"] ?? 0);
                double cpuSeconds = Math.Round((kernel + user) / 10_000_000.0, 1);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                map[pid] = new ProcInfo(pid, ppid, name, cmdLine, ws, threads, handles, creation, cpuSeconds);
            }

            foreach (var proc in map.Values)
            {
                if (proc.Pid == proc.ParentPid) continue;
                if (!children.ContainsKey(proc.ParentPid))
                    children[proc.ParentPid] = new List<int>();
                children[proc.ParentPid].Add(proc.Pid);
            }

            var roots = map.Values
                .Where(p => !map.ContainsKey(p.ParentPid) || p.ParentPid == p.Pid)
                .Select(p => p.Pid)
                .ToList();

            _cache = (map, children, roots);
            _cacheTime = DateTime.UtcNow;
            return _cache;
        }
    }

    #endregion

    #region Object Factories

    private static ProcessInfo CreateProcessInfo(ProcInfo info, string directory) => new()
    {
        Directory = directory,
        Name = info.Name,
        PID = info.Pid,
        ParentPID = info.ParentPid,
        CommandLine = info.CommandLine,
        MemMB = Math.Round(info.WorkingSetSize / 1048576.0, 1),
        CPU = info.CpuSeconds > 0 ? info.CpuSeconds.ToString() : "",
        Threads = info.ThreadCount,
        Handles = info.HandleCount,
        StartTime = FormatWmiDateTime(info.CreationDate)
    };

    private static string FormatWmiDateTime(string wmiDate)
    {
        if (wmiDate.Length >= 14 &&
            DateTime.TryParseExact(wmiDate[..14], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy/MM/dd HH:mm:ss");
        return "N/A";
    }

    private static ProcessDetail CreateProcessDetail(ProcInfo info, string directory)
    {
        var detail = new ProcessDetail
        {
            Directory = directory,
            Name = info.Name,
            PID = info.Pid,
            ParentPID = info.ParentPid,
            CommandLine = info.CommandLine,
            MemMB = Math.Round(info.WorkingSetSize / 1048576.0, 1),
            CPU = info.CpuSeconds > 0 ? info.CpuSeconds.ToString() : "",
            Threads = info.ThreadCount,
            Handles = info.HandleCount,
            StartTime = FormatWmiDateTime(info.CreationDate)
        };

        try
        {
            var proc = Process.GetProcessById(info.Pid);

            try
            {
                detail.CPU = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1).ToString();
                detail.UserCPU = Math.Round(proc.UserProcessorTime.TotalSeconds, 2);
                detail.KernelCPU = Math.Round(proc.PrivilegedProcessorTime.TotalSeconds, 2);
                detail.TotalCPU = Math.Round(proc.TotalProcessorTime.TotalSeconds, 2);
            }
            catch { }

            detail.WorkingSetMB = Math.Round(proc.WorkingSet64 / 1048576.0, 1);
            detail.PeakWorkingSetMB = Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1);
            detail.PrivateBytesMB = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1);
            detail.VirtualSizeMB = Math.Round(proc.VirtualMemorySize64 / 1048576.0, 1);
            detail.PeakVirtualSizeMB = Math.Round(proc.PeakVirtualMemorySize64 / 1048576.0, 1);
            detail.PagedMemoryMB = Math.Round(proc.PagedMemorySize64 / 1048576.0, 1);
            detail.NonpagedMemoryKB = Math.Round(proc.NonpagedSystemMemorySize64 / 1024.0, 1);

            detail.SessionId = proc.SessionId;
            detail.BasePriority = proc.BasePriority;
            try { detail.PriorityClass = proc.PriorityClass.ToString(); }
            catch { detail.PriorityClass = "N/A"; }

            try
            {
                var mainModule = proc.MainModule;
                if (mainModule != null)
                {
                    detail.Path = mainModule.FileName;
                    var vi = mainModule.FileVersionInfo;
                    detail.FileVersion = vi.FileVersion ?? "";
                    detail.Company = vi.CompanyName ?? "";
                    detail.Description = vi.FileDescription ?? "";
                    detail.ProductName = vi.ProductName ?? "";
                }
            }
            catch { }

            try
            {
                var running = DateTime.Now - proc.StartTime;
                detail.RunningTime = $"{(int)running.TotalDays}d {running.Hours}h {running.Minutes}m";
            }
            catch { }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ReadOperationCount, WriteOperationCount, OtherOperationCount, " +
                $"ReadTransferCount, WriteTransferCount, OtherTransferCount, " +
                $"PageFaults FROM Win32_Process WHERE ProcessId = {info.Pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                detail.IOReadOps = Convert.ToInt64(obj["ReadOperationCount"]);
                detail.IOWriteOps = Convert.ToInt64(obj["WriteOperationCount"]);
                detail.IOOtherOps = Convert.ToInt64(obj["OtherOperationCount"]);
                detail.IOReadBytesMB = Math.Round(Convert.ToInt64(obj["ReadTransferCount"]) / 1048576.0, 1);
                detail.IOWriteBytesMB = Math.Round(Convert.ToInt64(obj["WriteTransferCount"]) / 1048576.0, 1);
                detail.IOOtherBytesMB = Math.Round(Convert.ToInt64(obj["OtherTransferCount"]) / 1048576.0, 1);
                detail.PageFaults = Convert.ToInt64(obj["PageFaults"]);
            }
        }
        catch { }

        return detail;
    }

    private static ProcessInfo CreateVirtualFolderInfo(string name, string directory) => new()
    {
        Directory = directory,
        Name = $"[{name}]",
        StartTime = VirtualFolderDescriptions.GetValueOrDefault(name, "")
    };

    private static ModuleInfo CreateModuleInfo(ProcessModule mod, string directory)
    {
        var info = new ModuleInfo
        {
            Directory = directory,
            Name = mod.ModuleName,
            SizeKB = Math.Round(mod.ModuleMemorySize / 1024.0, 1),
            Path = mod.FileName
        };
        try
        {
            var vi = mod.FileVersionInfo;
            info.Version = vi.FileVersion ?? "";
            info.Company = vi.CompanyName ?? "";
            info.Description = vi.FileDescription ?? "";
        }
        catch { }
        return info;
    }

    private static ThreadInfo CreateThreadInfo(ProcessThread thread, string directory)
    {
        var info = new ThreadInfo { Directory = directory, TID = thread.Id };
        try { info.State = thread.ThreadState.ToString(); } catch { info.State = "N/A"; }
        try { info.WaitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason.ToString() : ""; }
        catch { }
        info.Priority = thread.CurrentPriority;
        try { info.CPU = Math.Round(thread.TotalProcessorTime.TotalSeconds, 2); } catch { }
        try { info.StartTime = thread.StartTime.ToString("yyyy/MM/dd HH:mm:ss"); } catch { info.StartTime = "N/A"; }
        try { info.StartAddress = $"0x{thread.StartAddress:X}"; } catch { info.StartAddress = "N/A"; }
        return info;
    }

    private static ServiceInfo CreateServiceInfo(ManagementObject svc, string directory) => new()
    {
        Directory = directory,
        Name = svc["Name"]?.ToString() ?? "",
        DisplayName = svc["DisplayName"]?.ToString() ?? "",
        State = svc["State"]?.ToString() ?? "",
        StartMode = svc["StartMode"]?.ToString() ?? ""
    };

    private static NetworkInfo CreateNetworkInfo(string protocol, string localAddr, int localPort,
        string remoteAddr, int remotePort, string state, string directory) => new()
    {
        Directory = directory,
        Protocol = protocol,
        LocalAddress = $"{localAddr}:{localPort}",
        RemoteAddress = remoteAddr.Length > 0 ? $"{remoteAddr}:{remotePort}" : "",
        State = state
    };

    #endregion

    #region Virtual Folder Data

    private void WriteModules(int pid, string parentPath)
    {
        var directory = EnsureDrivePrefix(parentPath);
        try
        {
            var proc = Process.GetProcessById(pid);
            foreach (ProcessModule mod in proc.Modules)
            {
                var itemPath = BuildChildPath(parentPath, mod.ModuleName);
                WriteItemObject(CreateModuleInfo(mod, directory), itemPath, false);
            }
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "GetModulesFailed", ErrorCategory.ReadError, pid));
        }
    }

    private void WriteThreads(int pid, string parentPath)
    {
        var directory = EnsureDrivePrefix(parentPath);
        try
        {
            var proc = Process.GetProcessById(pid);
            foreach (ProcessThread thread in proc.Threads)
            {
                var segment = $"Thread_{thread.Id}";
                var itemPath = BuildChildPath(parentPath, segment);
                WriteItemObject(CreateThreadInfo(thread, directory), itemPath, false);
            }
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "GetThreadsFailed", ErrorCategory.ReadError, pid));
        }
    }

    private void WriteServices(int pid, string parentPath)
    {
        var directory = EnsureDrivePrefix(parentPath);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, DisplayName, State, StartMode FROM Win32_Service WHERE ProcessId = {pid}");
            foreach (ManagementObject svc in searcher.Get())
            {
                var name = svc["Name"]?.ToString() ?? "unknown";
                var itemPath = BuildChildPath(parentPath, name);
                WriteItemObject(CreateServiceInfo(svc, directory), itemPath, false);
            }
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "GetServicesFailed", ErrorCategory.ReadError, pid));
        }
    }

    private void WriteNetwork(int pid, string parentPath)
    {
        var directory = EnsureDrivePrefix(parentPath);
        try
        {
            // TCP connections
            foreach (var conn in NetworkHelper.GetTcpConnections(pid))
            {
                var segment = $"TCP_{conn.LocalAddr}_{conn.LocalPort}";
                var itemPath = BuildChildPath(parentPath, segment);
                WriteItemObject(CreateNetworkInfo("TCP", conn.LocalAddr, conn.LocalPort,
                    conn.RemoteAddr, conn.RemotePort, conn.State, directory), itemPath, false);
            }
            // UDP listeners
            foreach (var conn in NetworkHelper.GetUdpListeners(pid))
            {
                var segment = $"UDP_{conn.LocalAddr}_{conn.LocalPort}";
                var itemPath = BuildChildPath(parentPath, segment);
                WriteItemObject(CreateNetworkInfo("UDP", conn.LocalAddr, conn.LocalPort,
                    "", 0, "", directory), itemPath, false);
            }
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "GetNetworkFailed", ErrorCategory.ReadError, pid));
        }
    }

    #endregion

    #region Navigation

    protected override bool IsValidPath(string path) => true;

    protected override bool ItemExists(string path)
    {
        var info = ParsePathInfo(path);
        switch (info.Type)
        {
            case PathType.Root:
                return true;
            case PathType.Process:
                var (map, _, _) = BuildTree();
                return map.ContainsKey(info.Pid);
            case PathType.VirtualFolder:
                if (info.Pid < 0) return false;
                var (map2, _, _) = BuildTree();
                return map2.ContainsKey(info.Pid);
            case PathType.VirtualItem:
                return info.Pid >= 0;
            default:
                return false;
        }
    }

    protected override bool IsItemContainer(string path)
    {
        var info = ParsePathInfo(path);
        return info.Type != PathType.VirtualItem;
    }

    protected override void GetItem(string path)
    {
        var info = ParsePathInfo(path);
        var directory = EnsureDrivePrefix(path);

        switch (info.Type)
        {
            case PathType.Root:
                var (map, _, _) = BuildTree();
                var pso = new PSObject();
                pso.Properties.Add(new PSNoteProperty("Description", "Process Tree Root"));
                pso.Properties.Add(new PSNoteProperty("TotalProcesses", map.Count));
                WriteItemObject(pso, path, true);
                break;

            case PathType.Process:
                var (map2, _, _) = BuildTree();
                if (map2.TryGetValue(info.Pid, out var procInfo))
                    WriteItemObject(CreateProcessDetail(procInfo, directory), path, true);
                break;

            case PathType.VirtualFolder:
                WriteItemObject(CreateVirtualFolderInfo(info.VirtualFolder!, directory), path, true);
                break;

            case PathType.VirtualItem:
                // Return the item if found
                WriteItemObject(new PSObject(info.VirtualItem!), path, false);
                break;
        }
    }

    protected override void GetChildItems(string path, bool recurse)
    {
        if (Force) InvalidateCache();

        var info = ParsePathInfo(path);

        switch (info.Type)
        {
            case PathType.Root:
            {
                var (map, children, roots) = BuildTree();
                var directory = EnsureDrivePrefix(path);
                var sorted = SortChildPids(roots, map);
                WriteImmediateChildren(path, directory, sorted, map);
                if (recurse)
                    RecurseChildren(path, sorted, map, children);
                break;
            }
            case PathType.Process:
            {
                var (map, children, _) = BuildTree();
                var childPids = children.TryGetValue(info.Pid, out var c) ? c : new List<int>();
                var directory = EnsureDrivePrefix(path);
                var sorted = SortChildPids(childPids, map);

                // First pass: write immediate child processes
                WriteImmediateChildren(path, directory, sorted, map);

                // Write virtual folders (same Directory group as child processes)
                foreach (var folder in VirtualFolderNames.Order())
                {
                    var folderPath = BuildChildPath(path, folder);
                    WriteItemObject(CreateVirtualFolderInfo(folder, directory), folderPath, true);
                }

                // Second pass: recurse into children
                if (recurse)
                    RecurseChildren(path, sorted, map, children);
                break;
            }
            case PathType.VirtualFolder:
            {
                if (info.Pid < 0) break;
                switch (info.VirtualFolder!.ToLowerInvariant())
                {
                    case "modules":  WriteModules(info.Pid, path); break;
                    case "threads":  WriteThreads(info.Pid, path); break;
                    case "services": WriteServices(info.Pid, path); break;
                    case "network":  WriteNetwork(info.Pid, path); break;
                }
                break;
            }
        }
    }

    private static List<int> SortChildPids(List<int> childPids,
        Dictionary<int, ProcInfo> map)
    {
        return childPids
            .Where(p => map.ContainsKey(p))
            .OrderBy(p => map[p].Name)
            .ToList();
    }

    private void WriteImmediateChildren(string parentPath, string directory, List<int> sortedPids,
        Dictionary<int, ProcInfo> map)
    {
        foreach (int cpid in sortedPids)
        {
            var info = map[cpid];
            var childPath = BuildChildPath(parentPath, FormatSegment(cpid, info.Name));
            WriteItemObject(CreateProcessInfo(info, directory), childPath, true);
        }
    }

    private void RecurseChildren(string parentPath, List<int> sortedPids,
        Dictionary<int, ProcInfo> map, Dictionary<int, List<int>> children)
    {
        foreach (int cpid in sortedPids)
        {
            if (!children.TryGetValue(cpid, out var grandKids) || grandKids.Count == 0)
                continue;
            var info = map[cpid];
            var childPath = BuildChildPath(parentPath, FormatSegment(cpid, info.Name));
            var childDirectory = EnsureDrivePrefix(childPath);

            var sortedGrandKids = SortChildPids(grandKids, map);
            WriteImmediateChildren(childPath, childDirectory, sortedGrandKids, map);
            RecurseChildren(childPath, sortedGrandKids, map, children);
        }
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        if (Force) InvalidateCache();
        var info = ParsePathInfo(path);

        switch (info.Type)
        {
            case PathType.Root:
            {
                var (map, _, roots) = BuildTree();
                foreach (int pid in roots.OrderBy(p => map.TryGetValue(p, out var i) ? i.Name : ""))
                {
                    if (!map.TryGetValue(pid, out var pi)) continue;
                    var segment = FormatSegment(pid, pi.Name);
                    WriteItemObject(segment, BuildChildPath(path, segment), true);
                }
                break;
            }
            case PathType.Process:
            {
                var (map, children, _) = BuildTree();
                var childPids = children.TryGetValue(info.Pid, out var c) ? c : new List<int>();

                // Child process names
                foreach (int cpid in childPids.OrderBy(p => map.TryGetValue(p, out var i) ? i.Name : ""))
                {
                    if (!map.TryGetValue(cpid, out var pi)) continue;
                    var segment = FormatSegment(cpid, pi.Name);
                    WriteItemObject(segment, BuildChildPath(path, segment), true);
                }
                // Virtual folder names are NOT listed here.
                // Tab completion uses GetChildItems (not GetChildNames), so cd Mod<Tab> still works.
                // Excluding them prevents recursive wildcard (dir note* -Recurse) from entering virtual folders.
                break;
            }
            case PathType.VirtualFolder:
            {
                if (info.Pid < 0) break;
                try
                {
                    switch (info.VirtualFolder!.ToLowerInvariant())
                    {
                        case "modules":
                            var proc = Process.GetProcessById(info.Pid);
                            foreach (ProcessModule mod in proc.Modules)
                                WriteItemObject(mod.ModuleName, BuildChildPath(path, mod.ModuleName), false);
                            break;
                        case "threads":
                            var proc2 = Process.GetProcessById(info.Pid);
                            foreach (ProcessThread t in proc2.Threads)
                            {
                                var seg = $"Thread_{t.Id}";
                                WriteItemObject(seg, BuildChildPath(path, seg), false);
                            }
                            break;
                        case "services":
                            using (var searcher = new ManagementObjectSearcher(
                                $"SELECT Name FROM Win32_Service WHERE ProcessId = {info.Pid}"))
                            {
                                foreach (ManagementObject svc in searcher.Get())
                                {
                                    var name = svc["Name"]?.ToString() ?? "";
                                    WriteItemObject(name, BuildChildPath(path, name), false);
                                }
                            }
                            break;
                        case "network":
                            foreach (var conn in NetworkHelper.GetTcpConnections(info.Pid))
                            {
                                var seg = $"TCP_{conn.LocalAddr}_{conn.LocalPort}";
                                WriteItemObject(seg, BuildChildPath(path, seg), false);
                            }
                            foreach (var conn in NetworkHelper.GetUdpListeners(info.Pid))
                            {
                                var seg = $"UDP_{conn.LocalAddr}_{conn.LocalPort}";
                                WriteItemObject(seg, BuildChildPath(path, seg), false);
                            }
                            break;
                    }
                }
                catch { /* process may have exited */ }
                break;
            }
        }
    }

    protected override bool HasChildItems(string path)
    {
        var info = ParsePathInfo(path);
        return info.Type switch
        {
            PathType.Root => true,
            PathType.Process => true, // always has virtual folders at minimum
            PathType.VirtualFolder => false, // prevent recursive wildcard from enumerating modules/threads/etc.
            _ => false
        };
    }

    #endregion

    #region Path Manipulation

    protected override string MakePath(string parent, string child)
    {
        var result = base.MakePath(parent, child);
        if (result.EndsWith(Sep) && result.Length > 1 && result[^2] != ':')
            result = result[..^1];
        return result;
    }

    protected override string NormalizeRelativePath(string path, string basePath)
    {
        var result = base.NormalizeRelativePath(path, basePath);
        if (result.StartsWith(Sep) && result.Length > 1)
            result = result[1..];
        return result;
    }

    #endregion

    #region Actions

    protected override void RemoveItem(string path, bool recurse)
    {
        var info = ParsePathInfo(path);
        if (info.Type != PathType.Process || info.Pid <= 0) return;

        var segments = SplitPath(path);
        if (!ShouldProcess($"Process {segments[^1]} (PID: {info.Pid})", "Stop"))
            return;

        try
        {
            var proc = Process.GetProcessById(info.Pid);
            if (recurse)
                proc.Kill(true);
            else
                proc.Kill();
            WriteItemObject($"Stopped process {info.Pid}", path, false);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "StopProcessFailed",
                ErrorCategory.InvalidOperation, info.Pid));
        }
    }

    #endregion

    #region Drive

    protected override PSDriveInfo NewDrive(PSDriveInfo drive) => drive;

    #endregion
}

[Cmdlet(VerbsCommon.New, "ProcDrive")]
[OutputType(typeof(PSDriveInfo))]
public class NewProcDriveCmdlet : PSCmdlet
{
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = "Proc";

    protected override void ProcessRecord()
    {
        var provider = SessionState.Provider.GetOne("ProcessDrive");
        var root = Name + @":\";
        var driveInfo = new PSDriveInfo(Name, provider, root, "Process Tree Drive", null);
        var result = SessionState.Drive.New(driveInfo, "global");
        WriteObject(result);
    }
}

#region Network Helper (P/Invoke)

static class NetworkHelper
{
    public record TcpConnection(string LocalAddr, int LocalPort, string RemoteAddr, int RemotePort, string State);
    public record UdpListener(string LocalAddr, int LocalPort);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
        bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
        bool bOrder, int ulAf, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    private static ushort ConvertPort(uint port)
        => (ushort)IPAddress.NetworkToHostOrder((short)(port & 0xFFFF));

    private static string GetTcpState(uint state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynRcvd",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => $"Unknown({state})"
    };

    public static List<TcpConnection> GetTcpConnections(int pid)
    {
        var result = new List<TcpConnection>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return result;

            int count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                if (row.dwOwningPid == (uint)pid)
                {
                    result.Add(new TcpConnection(
                        new IPAddress(row.dwLocalAddr).ToString(),
                        ConvertPort(row.dwLocalPort),
                        new IPAddress(row.dwRemoteAddr).ToString(),
                        ConvertPort(row.dwRemotePort),
                        GetTcpState(row.dwState)
                    ));
                }
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    public static List<UdpListener> GetUdpListeners(int pid)
    {
        var result = new List<UdpListener>();
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                return result;

            int count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                if (row.dwOwningPid == (uint)pid)
                {
                    result.Add(new UdpListener(
                        new IPAddress(row.dwLocalAddr).ToString(),
                        ConvertPort(row.dwLocalPort)
                    ));
                }
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}

#endregion
