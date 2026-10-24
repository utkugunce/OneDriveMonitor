using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OneDriveMonitor
{
    public class ProcessLockInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string AppName { get; set; } = "";
    }

    public static class RestartManagerHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        public enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

        private const int ERROR_MORE_DATA = 234;

        public static List<ProcessLockInfo> FindLockingProcesses(string filePath)
        {
            var result = new List<ProcessLockInfo>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return result;

            string sessionKey = Guid.NewGuid().ToString();
            int res = RmStartSession(out uint sessionHandle, 0, sessionKey);
            if (res != 0) return result;

            try
            {
                string[] files = new string[] { filePath };
                res = RmRegisterResources(sessionHandle, (uint)files.Length, files, 0, null, 0, null);
                if (res != 0) return result;

                uint procInfoNeeded = 0;
                uint procInfo = 0;
                uint rebootReasons = 0;

                res = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, null, out rebootReasons);
                if (res == ERROR_MORE_DATA && procInfoNeeded > 0)
                {
                    var processInfo = new RM_PROCESS_INFO[procInfoNeeded];
                    procInfo = procInfoNeeded;
                    res = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, processInfo, out rebootReasons);
                    if (res == 0)
                    {
                        for (int i = 0; i < procInfo; i++)
                        {
                            int pid = processInfo[i].Process.dwProcessId;
                            string procName = processInfo[i].strAppName;
                            try
                            {
                                var p = Process.GetProcessById(pid);
                                procName = p.ProcessName;
                            }
                            catch { }

                            result.Add(new ProcessLockInfo
                            {
                                ProcessId = pid,
                                ProcessName = procName,
                                AppName = processInfo[i].strAppName
                            });
                        }
                    }
                }
            }
            catch { }
            finally
            {
                RmEndSession(sessionHandle);
            }

            return result;
        }

        public static bool IsFileLocked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
            try
            {
                using var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
