# Native SCM recovery configuration. Definitions are inert until explicitly invoked.
function Initialize-MonitorServiceRecovery {
    if ('AirWaveApMonitor.Operations.V1.BoundedServiceRecovery' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace AirWaveApMonitor.Operations.V1 {
    public sealed class RecoveryPolicy {
        public uint ResetPeriod { get; internal set; }
        public uint[] ActionTypes { get; internal set; }
        public uint[] Delays { get; internal set; }
        public bool NonCrashFailures { get; internal set; }
    }
    public sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() { return Native.CloseServiceHandle(handle); }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScAction { public uint Type; public uint Delay; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FailureActions {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint Count;
        public IntPtr Actions;
    }
    internal static class Native {
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        internal static extern ServiceHandle OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        internal static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", ExactSpelling=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", EntryPoint="ChangeServiceConfig2W", ExactSpelling=true, SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeActions(ServiceHandle service, uint level, ref FailureActions actions);
        [DllImport("advapi32.dll", EntryPoint="ChangeServiceConfig2W", ExactSpelling=true, SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeFlag(ServiceHandle service, uint level, ref int flag);
        [DllImport("advapi32.dll", ExactSpelling=true, SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig2W(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint needed);
    }
    public static class BoundedServiceRecovery {
        public static void Configure(string name) {
            using (ServiceHandle manager = Native.OpenSCManagerW(null, null, 1)) {
                Check(manager);
                // SERVICE_CHANGE_CONFIG | SERVICE_START; restart actions require both rights.
                using (ServiceHandle service = Native.OpenServiceW(manager, name, 2 | 16)) {
                    Check(service);
                    int stride = Marshal.SizeOf(typeof(ScAction));
                    IntPtr actions = Marshal.AllocHGlobal(stride * 4);
                    try {
                        for (int i = 0; i < 4; i++) {
                            // SCM repeats its last entry forever: terminate the sequence with NONE.
                            ScAction action = new ScAction { Type = i < 3 ? 1u : 0u, Delay = i < 3 ? 60000u : 0u };
                            Marshal.StructureToPtr(action, IntPtr.Add(actions, stride * i), false);
                        }
                        FailureActions policy = new FailureActions { ResetPeriod = 86400, Count = 4, Actions = actions };
                        if (!Native.ChangeActions(service, 2, ref policy)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        int nonCrashFailures = 1;
                        if (!Native.ChangeFlag(service, 4, ref nonCrashFailures)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    } finally { Marshal.FreeHGlobal(actions); }
                }
            }
        }
        public static RecoveryPolicy Read(string name) {
            using (ServiceHandle manager = Native.OpenSCManagerW(null, null, 1)) {
                Check(manager);
                using (ServiceHandle service = Native.OpenServiceW(manager, name, 1)) {
                    Check(service);
                    uint size;
                    IntPtr buffer = Query(service, 2, out size);
                    RecoveryPolicy result;
                    try {
                        if (size < Marshal.SizeOf(typeof(FailureActions))) throw new InvalidOperationException("Invalid SCM failure-action structure size.");
                        FailureActions policy = (FailureActions)Marshal.PtrToStructure(buffer, typeof(FailureActions));
                        int stride = Marshal.SizeOf(typeof(ScAction));
                        long offset = policy.Actions.ToInt64() - buffer.ToInt64();
                        if (policy.Count > 1024 || (policy.Count > 0 && (offset < 0 || offset + (long)policy.Count * stride > size)))
                            throw new InvalidOperationException("Invalid SCM failure-action buffer.");
                        result = new RecoveryPolicy { ResetPeriod = policy.ResetPeriod, ActionTypes = new uint[policy.Count], Delays = new uint[policy.Count] };
                        for (int i = 0; i < policy.Count; i++) {
                            ScAction action = (ScAction)Marshal.PtrToStructure(IntPtr.Add(policy.Actions, stride * i), typeof(ScAction));
                            result.ActionTypes[i] = action.Type;
                            result.Delays[i] = action.Delay;
                        }
                    } finally { Marshal.FreeHGlobal(buffer); }
                    buffer = Query(service, 4, out size);
                    try { result.NonCrashFailures = Marshal.ReadInt32(buffer) != 0; }
                    finally { Marshal.FreeHGlobal(buffer); }
                    return result;
                }
            }
        }
        private static IntPtr Query(ServiceHandle service, uint level, out uint size) {
            bool first = Native.QueryServiceConfig2W(service, level, IntPtr.Zero, 0, out size);
            int error = Marshal.GetLastWin32Error();
            if (first || error != 122) throw new Win32Exception(error);
            if (size < 4 || size > 8192) throw new InvalidOperationException("Invalid SCM configuration size.");
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            uint needed;
            if (!Native.QueryServiceConfig2W(service, level, buffer, size, out needed)) {
                error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(buffer);
                throw new Win32Exception(error);
            }
            return buffer;
        }
        private static void Check(ServiceHandle handle) {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
}
function Get-MonitorServiceRecovery {
    param([string]$ServiceName)
    Assert-MonitorWindows
    $ServiceName = Test-MonitorRegistrationName $ServiceName
    Initialize-MonitorServiceRecovery
    return [AirWaveApMonitor.Operations.V1.BoundedServiceRecovery]::Read($ServiceName)
}
function Set-MonitorServiceRecovery {
    param([string]$ServiceName)
    Assert-MonitorWindows
    $ServiceName = Test-MonitorRegistrationName $ServiceName
    Initialize-MonitorServiceRecovery
    [AirWaveApMonitor.Operations.V1.BoundedServiceRecovery]::Configure($ServiceName)
    $actual = Get-MonitorServiceRecovery -ServiceName $ServiceName
    if ($actual.ResetPeriod -ne 86400 -or ($actual.ActionTypes -join ',') -ne '1,1,1,0' -or
        ($actual.Delays -join ',') -ne '60000,60000,60000,0' -or -not $actual.NonCrashFailures) {
        throw '서비스 복구 정책의 실제 등록값이 일치하지 않습니다.'
    }
}
