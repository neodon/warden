﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Warden.Core.Exceptions;
using Warden.Core.Models;
using Warden.Core.Utils;
using static Warden.Core.WardenManager;
using Warden.Properties;
using Warden.Windows.Uwp;
using Warden.Windows.Win32;
using static Warden.Core.Utils.ProcessUtils;

namespace Warden.Core
{
    public class StateEventArgs : EventArgs
    {
        public StateEventArgs(int processId, ProcessState state)
        {
            State = state;
            Id = processId;
        }

        public ProcessState State { get; }

        public int Id { get; }
    }

    public class ProcessAddedEventArgs : EventArgs
    {
        public ProcessAddedEventArgs(string name, int parentId, int processId, string processPath, List<string> commandLine)
        {
            Name = name;
            ParentId = parentId;
            Id = processId;
            ProcessPath = processPath;
            CommandLine = commandLine;
        }

        public ProcessAddedEventArgs()
        {
        }

        public string Name { get; internal set; }

        public int ParentId { get; internal set; }

        public int Id { get; internal set; }

        public string ProcessPath { get; internal set; }

        public List<string> CommandLine { get; internal set; }
    }


    public class UntrackedProcessEventArgs : ProcessAddedEventArgs
    {
        public UntrackedProcessEventArgs(string name, int parentId, int processId, string processPath, List<string> commandLine) : base(name, parentId, processId, processPath, commandLine) { }
        public UntrackedProcessEventArgs() : base() { }

        public bool Create { get; set; } = false;
        public List<ProcessFilter> Filters { get; set; } = null;
        public Action<WardenProcess> Callback { get; set; } = null;
    }

    /// <summary>
    /// Provides access to local processes and their children in real-time.
    /// </summary>
    public class WardenProcess
    {
        public delegate void ChildStateUpdateHandler(object sender, StateEventArgs e);

        public delegate void StateUpdateHandler(object sender, StateEventArgs e);

        public delegate void ProcessAddedHandler(object sender, ProcessAddedEventArgs e);

        internal WardenProcess(string name, int id, string path, ProcessState state, List<string> arguments, List<ProcessFilter> filters)
        {
            Filters = filters;
            Name = name;
            Id = id;
            Path = path;
            State = state;
            Arguments = arguments;
            Children = new ObservableCollection<WardenProcess>();
        }

        [IgnoreDataMember] public readonly List<ProcessFilter> Filters;

        public ObservableCollection<WardenProcess> Children { get; internal set; }

        public int ParentId { get; internal set; }

        public int Id { get; internal set; }

        public ProcessState State { get; private set; }

        public string Path { get; internal set; }

        public string Name { get; internal set; }

        [IgnoreDataMember]
        public Action<bool> FoundCallback { get; set; }

        public List<string> Arguments { get; internal set; }

        internal static string DefaultWardenReferProcUac = "winlogon";

        /// <summary>
        /// Used to set the target process which LaunchAsUser will steal a session token from. Default is "winlogon".
        /// If your target process is gone, this will default back to winlogon.
        /// </summary>
        public static string WardenReferProcUac { get; set; } = DefaultWardenReferProcUac;

        internal void SetParent(int parentId)
        {
            if (parentId == Id)
            {
                return;
            }
            ParentId = parentId;
        }

        /// <summary>
        /// Finds a child process by its id
        /// </summary>
        /// <param name="id"></param>
        /// <returns>The WardenProcess of the child.</returns>
        public WardenProcess FindChildById(int id)
        {
            return FindChildById(id, Children);
        }

        /// <summary>
        /// Adds a child to a collection.
        /// </summary>
        /// <param name="child"></param>
        /// <returns></returns>
        internal bool AddChild(WardenProcess child)
        {
            if (child == null)
            {
                return false;
            }
            if (Children == null)
            {
                Children = new ObservableCollection<WardenProcess>();
            }
            child.OnChildStateChange += OnChildOnStateChange;
            Children.Add(child);
            return true;
        }

        private void OnChildOnStateChange(object sender, StateEventArgs stateEventArgs)
        {
            OnChildStateChange?.Invoke(this, stateEventArgs);
        }

        /// <summary>
        /// Updates the state of a process and fires events.
        /// </summary>
        /// <param name="state"></param>
        internal void UpdateState(ProcessState state)
        {
            State = state;
            OnStateChange?.Invoke(this, new StateEventArgs(Id, State));
            if (ParentId > 0)
            {
                OnChildStateChange?.Invoke(this, new StateEventArgs(Id, State));
            }
        }

        /// <summary>
        /// This event is fired when the process state has changed.
        /// </summary>
        public event StateUpdateHandler OnStateChange;

        /// <summary>
        /// This event is fired when a child for the current process has a state change.
        /// </summary>
        public event ChildStateUpdateHandler OnChildStateChange;

        /// <summary>
        /// This event is fired when a process is added to the main process or its children
        /// </summary>
        public event ProcessAddedHandler OnProcessAdded;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="args"></param>
        public void InvokeProcessAdd(ProcessAddedEventArgs args)
        {
            OnProcessAdded?.Invoke(this, args);
        }

        /// <summary>
        /// Crawls a process tree and updates the states.
        /// </summary>
        public void RefreshTree()
        {
            try
            {
                var p = Process.GetProcessById(Id);
                p.Refresh();
                State = p.HasExited ? ProcessState.Dead : ProcessState.Alive;
            }
            catch
            {
                State = ProcessState.Dead;
            }
            if (Children != null)
            {
                RefreshChildren(Children);
            }
        }

        /// <summary>
        /// Updates the children of a process.
        /// </summary>
        /// <param name="children"></param>
        private void RefreshChildren(ObservableCollection<WardenProcess> children)
        {
            foreach (var child in children)
            {
                if (child == null)
                {
                    continue;
                }
                try
                {
                    var p = Process.GetProcessById(child.Id);
                    p.Refresh();
                    child.State = p.HasExited ? ProcessState.Dead : ProcessState.Alive;
                }
                catch
                {
                    child.State = ProcessState.Dead;
                }
                if (child.Children != null)
                {
                    RefreshChildren(child.Children);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        internal static List<string> DefaultProcesses = new List<string>
        {
            "svchost", "runtimebroker", "backgroundtaskhost", "gamebarpresencewriter", "searchfilterhost"
        };

        internal bool IsFiltered()
        {
            if (Filters == null || Filters.Count <= 0)
            {
                return false;
            }
            foreach (var defaultProcess in DefaultProcesses)
            {
                if (Path.ToLower().Contains(defaultProcess))
                {
                    return true;
                }
            }
            foreach (var filter in Filters)
            {
                if (filter.Name.Equals(Name, StringComparison.CurrentCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if the tree contains any applications that are alive.
        /// </summary>
        /// <returns></returns>
        public bool IsTreeActive()
        {
            if (State == ProcessState.Alive)
            {
                return true;
            }
            return Children != null && CheckChildren(Children);
        }

        /// <summary>
        /// Checks if any of the children are alive.
        /// </summary>
        /// <param name="children"></param>
        /// <returns></returns>
        private bool CheckChildren(ObservableCollection<WardenProcess> children)
        {
            if (children == null)
            {
                return false;
            }
            foreach (var child in children)
            {
                if (child == null)
                {
                    continue;
                }
                if (child.State == ProcessState.Alive)
                {
                    return true;
                }
                if (child.Children == null)
                {
                    continue;
                }
                if (CheckChildren(child.Children))
                {
                    return true;
                }
            }
            return false;
        }

        private void TaskKill()
        {
            var taskKill = new TaskKill
            {
                Arguments = new List<TaskSwitch>
                {
                    TaskSwitch.Force,
                    TaskSwitch.ProcessId.SetValue(Id.ToString()),
                    Options.DeepKill ? TaskSwitch.TerminateChildren : null
                }
            };
            taskKill.Execute(out var output, out var error);
            if (!string.IsNullOrWhiteSpace(output))
            {
                Debug.WriteLine(output?.Trim());
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine(error?.Trim());
            }
        }

        /// <summary>
        /// Kills the process and its children
        /// </summary>
        public void Kill()
        {
            try
            {
                if (Options.KillWhitelist?.Any(x => Name.StartsWith(x, StringComparison.InvariantCultureIgnoreCase)) == false)
                {
                    TaskKill();
                }
                if (Children == null || Children.Count <= 0 || !Options.DeepKill)
                {
                    return;
                }
                foreach (var child in Children)
                {
                    child?.Kill();
                }
            }
            catch
            {
                //
            }
        }

        #region static class

        /// <summary>
        /// Checks if a process is currently running, if so we build the WardenProcess right away or fetch a monitored one.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="process"></param>
        /// <returns></returns>
        private static bool CheckForWardenProcess(string path, out WardenProcess process)
        {
            using (var runningProcess = GetProcess(path))
            {
                if (runningProcess == null)
                {
                    process = null;
                    return false;
                }

                foreach (var key in ManagedProcesses.Keys)
                {
                    if (ManagedProcesses.TryGetValue(key, out process))
                    {
                        if (process.Id == runningProcess.Id)
                        {
                            return true;
                        }
                    }
                }
                process = null;
                return false;
            }
        }

        /// <summary>
        /// Launches a system URI and waits for the target process to appear.
        /// </summary>
        /// <param name="startInfo"></param>
        /// <param name="cancelToken"></param>
        /// <returns></returns>
        public static async Task<WardenProcess> StartUri(WardenStartInfo startInfo, CancellationTokenSource cancelToken)
        {
            if (!Initialized)
            {
                throw new WardenManageException(Resources.Exception_Not_Initialized);
            }
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
            {
                throw new ArgumentException("fileName cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(startInfo.TargetFileName))
            {
                throw new ArgumentException("targetFileName cannot be empty.");
            }
            if (CheckForWardenProcess(startInfo.TargetFileName, out var existingProcess))
            {
                return existingProcess;
            }
            if (string.IsNullOrWhiteSpace(startInfo.Arguments))
            {
                startInfo.Arguments = string.Empty;
            }
            //lets add it to the dictionary ahead of time in case our program launches faster than we can return
            var key = Guid.NewGuid();
            if (ManagedProcesses.TryAdd(key, new WardenProcess(System.IO.Path.GetFileNameWithoutExtension(startInfo.TargetFileName), GenerateProcessId(), startInfo.TargetFileName, ProcessState.Alive, startInfo.Arguments.SplitSpace(), startInfo.Filters)))
            {
                if (await UriShell.LaunchUri(startInfo, cancelToken))
                {
                    if (ManagedProcesses.TryGetValue(key, out var process))
                    {
                        return process;
                    }
                }
            }
            ManagedProcesses.TryRemove(key, out var failedProcess);
            return null;
        }

        /// <summary>
        /// Launches a system URI and returns an empty Warden process set to Alive
        /// This spawns an asynchronous loop that will execute a callback if the target process is found
        /// However the function returns right away to ensure it does not block. 
        /// </summary>
        /// <param name="startInfo"></param>
        /// <param name="callback"></param>
        /// <returns></returns>
        public static WardenProcess StartUriDeferred(WardenStartInfo startInfo, Action<bool> callback)
        {
            if (!Initialized)
            {
                throw new WardenManageException(Resources.Exception_Not_Initialized);
            }
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
            {
                throw new ArgumentException("fileName cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(startInfo.TargetFileName))
            {
                throw new ArgumentException("targetFileName cannot be empty.");
            }
            if (CheckForWardenProcess(startInfo.TargetFileName, out var existingProcess))
            {
                return existingProcess;
            }
            if (string.IsNullOrWhiteSpace(startInfo.Arguments))
            {
                startInfo.Arguments = string.Empty;
            }

            //lets add it to the dictionary ahead of time in case our program launches faster than we can return
            var key = Guid.NewGuid();
            var warden = new WardenProcess(System.IO.Path.GetFileNameWithoutExtension(startInfo.TargetFileName),
                GenerateProcessId(), startInfo.TargetFileName, ProcessState.Alive, startInfo.Arguments.SplitSpace(), startInfo.Filters)
            {
                FoundCallback = callback
            };
            if (ManagedProcesses.TryAdd(key, warden))
            {
                if (UriShell.LaunchUriDeferred(startInfo))
                {
                    if (ManagedProcesses.TryGetValue(key, out var process))
                    {
                        return process;
                    }
                }
            }
            ManagedProcesses.TryRemove(key, out var failedProcess);
            return null;
        }

        /// <summary>
        /// Starts a monitored UWP process using the applications family package name and app ID.
        /// </summary>
        /// <param name="startInfo"></param>
        /// <returns></returns>
        public static WardenProcess StartUwp(WardenStartInfo startInfo)
        {
            if (!Initialized)
            {
                throw new WardenManageException(Resources.Exception_Not_Initialized);
            }
            if (string.IsNullOrWhiteSpace(startInfo.PackageFamilyName))
            {
                throw new ArgumentException("packageFamilyName cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(startInfo.ApplicationId))
            {
                throw new ArgumentException("applicationId cannot be empty.");
            }
            if (CheckForWardenProcess(startInfo.FileName, out var existingProcess))
            {
                return existingProcess;
            }
            if (string.IsNullOrWhiteSpace(startInfo.Arguments))
            {
                startInfo.Arguments = string.Empty;
            }
            return UwpShell.LaunchApp(startInfo);
        }

        /// <summary>
        /// Starts a monitored process using the applications full path.
        /// This method should only be used for win32 applications 
        /// </summary>
        /// <param name="startInfo"></param>
        /// <returns></returns>
        public static WardenProcess Start(WardenStartInfo startInfo)
        {
            if (!Initialized)
            {
                throw new WardenManageException(Resources.Exception_Not_Initialized);
            }
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
            {
                throw new ArgumentException("fileName cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
               startInfo.WorkingDirectory = PathUtils.GetDirectoryName(startInfo.FileName);
            }
            if (CheckForWardenProcess(startInfo.FileName, out var existingProcess))
            {
                return existingProcess;
            }
            if (string.IsNullOrWhiteSpace(startInfo.Arguments))
            {
                startInfo.Arguments = string.Empty;
            }
            return UserShell.LaunchWin32App(startInfo);
        }


        /// <summary>
        /// Finds a process in the tree using recursion
        /// </summary>
        /// <param name="id"></param>
        /// <param name="children"></param>
        /// <returns></returns>
        internal static WardenProcess FindChildById(int id, ObservableCollection<WardenProcess> children)
        {
            if (children == null)
            {
                return null;
            }
            foreach (var child in children)
            {
                if (child.Id == id)
                {
                    return child;
                }
                if (child.Children == null)
                {
                    continue;
                }
                var nested = FindChildById(id, child.Children);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Attempts to create a Warden process tree from an existing system process.
        /// </summary>
        /// <param name="pId"></param>
        /// <param name="filters">A list of filters so certain processes are not added to the tree.</param>
        /// <param name="processPath"></param>
        /// <param name="commandLine"></param>
        /// <param name="track"></param>
        /// <returns></returns>
        public static WardenProcess GetProcessFromId(int pId, List<ProcessFilter> filters = null, string processPath = null, List<string> commandLine = null, bool track = true)
        {
            var process = BuildTreeById(pId, filters, processPath, commandLine);
            if (process == null)
            {
                return null;
            }
            if (!track) return process;
            var key = Guid.NewGuid();
            if (ManagedProcesses.TryAdd(key, process))
            { 
                return process;
            }
            ManagedProcesses.TryRemove(key, out var _);
            return null;
        }


        private static WardenProcess BuildTreeById(int pId, List<ProcessFilter> filters, string processPath, List<string> commandLine)
        {
            try
            {
                Process process = null;
                try
                {
                    process = Process.GetProcessById(pId);
                }
                catch
                {
                    process = Process.GetProcesses().FirstOrDefault(it => it.Id == pId);
                }

                if (process == null)
                {
                    return null;
                }
                var processName = process.ProcessName;
                var processId = process.Id;
                var path = processPath ?? GetProcessPath(processId);
                var state = process.HasExited ? ProcessState.Dead : ProcessState.Alive;
                var arguments = commandLine ?? GetCommandLine(processId);
                var warden = new WardenProcess(processName, processId, path, state, arguments, filters);
                return warden;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a WardenProcess from a process id and sets a parent.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="parentId"></param>
        /// <param name="id"></param>
        /// <param name="path"></param>
        /// <param name="commandLineArguments"></param>
        /// <param name="filters">A list of filters so certain processes are not added to the tree.</param>
        /// <returns>A WardenProcess that will be added to a child list.</returns>
        internal static WardenProcess CreateProcessFromId(string name, int parentId, int id, string path,
            List<string> commandLineArguments,
            List<ProcessFilter> filters)
        {
            
            WardenProcess warden;
            try
            {
                var process = Process.GetProcessById(id);
                var processName = process.ProcessName;
                var processId = process.Id;
                var state = process.HasExited ? ProcessState.Dead : ProcessState.Alive;
                warden = new WardenProcess(processName, processId, path, state, commandLineArguments, filters);
                warden.SetParent(parentId);
                return warden;
            }
            catch
            {
                //
            }
            warden = new WardenProcess(name, id, path, ProcessState.Dead, commandLineArguments, filters);
            warden.SetParent(parentId);
            return warden;
        }

        #endregion
    }
}
