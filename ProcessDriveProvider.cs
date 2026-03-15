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

enum PathType { Root, Process, VirtualFolder, VirtualItem }

sealed record PathInfo(PathType Type, int Pid, string? VirtualFolder, string? VirtualItem);

[CmdletProvider("ProcessDrive", ProviderCapabilities.ShouldProcess)]
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
        long WorkingSetSize, int ThreadCount, int HandleCount, string CreationDate);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
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
                "WorkingSetSize, ThreadCount, HandleCount, CreationDate FROM Win32_Process");

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
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                map[pid] = new ProcInfo(pid, ppid, name, cmdLine, ws, threads, handles, creation);
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

    #region PSObject Factories

    private PSObject CreateProcessObject(ProcInfo info, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.ProcessInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("Name", info.Name));
        pso.Properties.Add(new PSNoteProperty("PID", info.Pid));
        pso.Properties.Add(new PSNoteProperty("ParentPID", info.ParentPid));
        pso.Properties.Add(new PSNoteProperty("CommandLine", info.CommandLine));
        pso.Properties.Add(new PSNoteProperty("Mem(MB)", Math.Round(info.WorkingSetSize / 1048576.0, 1)));
        pso.Properties.Add(new PSNoteProperty("CPU(s)", ""));
        pso.Properties.Add(new PSNoteProperty("Threads", info.ThreadCount));
        pso.Properties.Add(new PSNoteProperty("Handles", info.HandleCount));
        pso.Properties.Add(new PSNoteProperty("StartTime", FormatWmiDateTime(info.CreationDate)));
        return pso;
    }

    private static string FormatWmiDateTime(string wmiDate)
    {
        // WMI format: "20260313095354.000000+540"
        if (wmiDate.Length >= 14 &&
            DateTime.TryParseExact(wmiDate[..14], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy/MM/dd HH:mm:ss");
        return "N/A";
    }

    private PSObject CreateDetailedProcessObject(ProcInfo info, string directory)
    {
        var pso = CreateProcessObject(info, directory);
        pso.TypeNames.Insert(0, "ProcessDrive.ProcessDetail");

        // Single Process API call for all detailed properties
        try
        {
            var proc = Process.GetProcessById(info.Pid);

            // CPU (not available from WMI cache, so fill in the dir column too)
            try
            {
                pso.Properties["CPU(s)"].Value = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1);
                pso.Properties.Add(new PSNoteProperty("UserCPU(s)", Math.Round(proc.UserProcessorTime.TotalSeconds, 2)));
                pso.Properties.Add(new PSNoteProperty("KernelCPU(s)", Math.Round(proc.PrivilegedProcessorTime.TotalSeconds, 2)));
                pso.Properties.Add(new PSNoteProperty("TotalCPU(s)", Math.Round(proc.TotalProcessorTime.TotalSeconds, 2)));
            }
            catch { }

            // Memory details
            pso.Properties.Add(new PSNoteProperty("WorkingSet(MB)", Math.Round(proc.WorkingSet64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("PeakWorkingSet(MB)", Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("PrivateBytes(MB)", Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("VirtualSize(MB)", Math.Round(proc.VirtualMemorySize64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("PeakVirtualSize(MB)", Math.Round(proc.PeakVirtualMemorySize64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("PagedMemory(MB)", Math.Round(proc.PagedMemorySize64 / 1048576.0, 1)));
            pso.Properties.Add(new PSNoteProperty("NonpagedMemory(KB)", Math.Round(proc.NonpagedSystemMemorySize64 / 1024.0, 1)));

            // Process details
            pso.Properties.Add(new PSNoteProperty("SessionId", proc.SessionId));
            pso.Properties.Add(new PSNoteProperty("BasePriority", proc.BasePriority));
            try { pso.Properties.Add(new PSNoteProperty("PriorityClass", proc.PriorityClass.ToString())); }
            catch { pso.Properties.Add(new PSNoteProperty("PriorityClass", "N/A")); }

            // File info
            try
            {
                var mainModule = proc.MainModule;
                if (mainModule != null)
                {
                    pso.Properties.Add(new PSNoteProperty("Path", mainModule.FileName));
                    var vi = mainModule.FileVersionInfo;
                    pso.Properties.Add(new PSNoteProperty("FileVersion", vi.FileVersion ?? ""));
                    pso.Properties.Add(new PSNoteProperty("Company", vi.CompanyName ?? ""));
                    pso.Properties.Add(new PSNoteProperty("Description", vi.FileDescription ?? ""));
                    pso.Properties.Add(new PSNoteProperty("ProductName", vi.ProductName ?? ""));
                }
            }
            catch { }

            // Running time
            try
            {
                var running = DateTime.Now - proc.StartTime;
                pso.Properties.Add(new PSNoteProperty("RunningTime", $"{(int)running.TotalDays}d {running.Hours}h {running.Minutes}m"));
            }
            catch { }
        }
        catch { }

        // I/O stats from WMI (single query for this PID)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ReadOperationCount, WriteOperationCount, OtherOperationCount, " +
                $"ReadTransferCount, WriteTransferCount, OtherTransferCount, " +
                $"PageFaults FROM Win32_Process WHERE ProcessId = {info.Pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                var reads = Convert.ToInt64(obj["ReadTransferCount"]);
                var writes = Convert.ToInt64(obj["WriteTransferCount"]);
                var other = Convert.ToInt64(obj["OtherTransferCount"]);
                pso.Properties.Add(new PSNoteProperty("IO.ReadOps", Convert.ToInt64(obj["ReadOperationCount"])));
                pso.Properties.Add(new PSNoteProperty("IO.WriteOps", Convert.ToInt64(obj["WriteOperationCount"])));
                pso.Properties.Add(new PSNoteProperty("IO.OtherOps", Convert.ToInt64(obj["OtherOperationCount"])));
                pso.Properties.Add(new PSNoteProperty("IO.ReadBytes(MB)", Math.Round(reads / 1048576.0, 1)));
                pso.Properties.Add(new PSNoteProperty("IO.WriteBytes(MB)", Math.Round(writes / 1048576.0, 1)));
                pso.Properties.Add(new PSNoteProperty("IO.OtherBytes(MB)", Math.Round(other / 1048576.0, 1)));
                pso.Properties.Add(new PSNoteProperty("PageFaults", Convert.ToInt64(obj["PageFaults"])));
            }
        }
        catch { }

        return pso;
    }

    private PSObject CreateVirtualFolderObject(string name, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.ProcessInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("Name", $"[{name}]"));
        pso.Properties.Add(new PSNoteProperty("PID", ""));
        pso.Properties.Add(new PSNoteProperty("ParentPID", ""));
        pso.Properties.Add(new PSNoteProperty("CommandLine", ""));
        pso.Properties.Add(new PSNoteProperty("Mem(MB)", ""));
        pso.Properties.Add(new PSNoteProperty("CPU(s)", ""));
        pso.Properties.Add(new PSNoteProperty("Threads", ""));
        pso.Properties.Add(new PSNoteProperty("Handles", ""));
        pso.Properties.Add(new PSNoteProperty("StartTime", VirtualFolderDescriptions.GetValueOrDefault(name, "")));
        return pso;
    }

    private PSObject CreateModuleObject(ProcessModule mod, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.ModuleInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("Name", mod.ModuleName));
        pso.Properties.Add(new PSNoteProperty("Size(KB)", Math.Round(mod.ModuleMemorySize / 1024.0, 1)));
        pso.Properties.Add(new PSNoteProperty("Path", mod.FileName));
        try
        {
            var vi = mod.FileVersionInfo;
            pso.Properties.Add(new PSNoteProperty("Version", vi.FileVersion ?? ""));
            pso.Properties.Add(new PSNoteProperty("Company", vi.CompanyName ?? ""));
            pso.Properties.Add(new PSNoteProperty("Description", vi.FileDescription ?? ""));
        }
        catch
        {
            pso.Properties.Add(new PSNoteProperty("Version", ""));
            pso.Properties.Add(new PSNoteProperty("Company", ""));
            pso.Properties.Add(new PSNoteProperty("Description", ""));
        }
        return pso;
    }

    private PSObject CreateThreadObject(ProcessThread thread, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.ThreadInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("TID", thread.Id));
        try { pso.Properties.Add(new PSNoteProperty("State", thread.ThreadState.ToString())); }
        catch { pso.Properties.Add(new PSNoteProperty("State", "N/A")); }
        try
        {
            pso.Properties.Add(new PSNoteProperty("WaitReason",
                thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason.ToString() : ""));
        }
        catch { pso.Properties.Add(new PSNoteProperty("WaitReason", "")); }
        pso.Properties.Add(new PSNoteProperty("Priority", thread.CurrentPriority));
        try { pso.Properties.Add(new PSNoteProperty("CPU(s)", Math.Round(thread.TotalProcessorTime.TotalSeconds, 2))); }
        catch { pso.Properties.Add(new PSNoteProperty("CPU(s)", 0)); }
        try { pso.Properties.Add(new PSNoteProperty("StartTime", thread.StartTime.ToString("yyyy/MM/dd HH:mm:ss"))); }
        catch { pso.Properties.Add(new PSNoteProperty("StartTime", "N/A")); }
        try { pso.Properties.Add(new PSNoteProperty("StartAddress", $"0x{thread.StartAddress:X}")); }
        catch { pso.Properties.Add(new PSNoteProperty("StartAddress", "N/A")); }
        return pso;
    }

    private PSObject CreateServiceObject(ManagementObject svc, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.ServiceInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("Name", svc["Name"]?.ToString() ?? ""));
        pso.Properties.Add(new PSNoteProperty("DisplayName", svc["DisplayName"]?.ToString() ?? ""));
        pso.Properties.Add(new PSNoteProperty("State", svc["State"]?.ToString() ?? ""));
        pso.Properties.Add(new PSNoteProperty("StartMode", svc["StartMode"]?.ToString() ?? ""));
        return pso;
    }

    private static PSObject CreateNetworkObject(string protocol, string localAddr, int localPort,
        string remoteAddr, int remotePort, string state, string directory)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ProcessDrive.NetworkInfo");
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        pso.Properties.Add(new PSNoteProperty("Protocol", protocol));
        pso.Properties.Add(new PSNoteProperty("LocalAddress", $"{localAddr}:{localPort}"));
        pso.Properties.Add(new PSNoteProperty("RemoteAddress", remoteAddr.Length > 0 ? $"{remoteAddr}:{remotePort}" : ""));
        pso.Properties.Add(new PSNoteProperty("State", state));
        return pso;
    }

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
                WriteItemObject(CreateModuleObject(mod, directory), itemPath, false);
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
                WriteItemObject(CreateThreadObject(thread, directory), itemPath, false);
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
                WriteItemObject(CreateServiceObject(svc, directory), itemPath, false);
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
                WriteItemObject(CreateNetworkObject("TCP", conn.LocalAddr, conn.LocalPort,
                    conn.RemoteAddr, conn.RemotePort, conn.State, directory), itemPath, false);
            }
            // UDP listeners
            foreach (var conn in NetworkHelper.GetUdpListeners(pid))
            {
                var segment = $"UDP_{conn.LocalAddr}_{conn.LocalPort}";
                var itemPath = BuildChildPath(parentPath, segment);
                WriteItemObject(CreateNetworkObject("UDP", conn.LocalAddr, conn.LocalPort,
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
                    WriteItemObject(CreateDetailedProcessObject(procInfo, directory), path, true);
                break;

            case PathType.VirtualFolder:
                WriteItemObject(CreateVirtualFolderObject(info.VirtualFolder!, directory), path, true);
                break;

            case PathType.VirtualItem:
                // Return the item if found
                WriteItemObject(new PSObject(info.VirtualItem!), path, false);
                break;
        }
    }

    protected override void GetChildItems(string path, bool recurse)
    {
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
                    WriteItemObject(CreateVirtualFolderObject(folder, directory), folderPath, true);
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
            WriteItemObject(CreateProcessObject(info, directory), childPath, true);
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
