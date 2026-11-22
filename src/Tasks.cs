// VPN Panel - Tasks: Task Scheduler entries (start with Windows, no-UAC launch).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Shell;

namespace VpnPanel
{
    // Scheduled tasks that run the exe with admin rights without a UAC prompt:
    //  - Logon: "start with Windows" (Windows won't elevate programs from the Run registry key)
    //  - Open: started by the non-elevated exe on every manual launch, so only the first launch asks
    // Uses the Task Scheduler COM API, which also works from the non-elevated exe.
    static class Tasks
    {
        public const string Logon = "VPN Panel";
        public const string Open = "VPN Panel - Abrir";

        static dynamic Folder()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            return service.GetFolder("\\");
        }

        static dynamic Get(dynamic folder, string name)
        {
            try { return folder.GetTask(name); } catch { return null; }
        }

        static dynamic Get(string name)
        {
            try { return Get(Folder(), name); } catch { return null; }
        }

        static string CommandOf(dynamic folder, string name)
        {
            try
            {
                dynamic task = Get(folder, name);
                if (task == null) return null;
                foreach (dynamic action in task.Definition.Actions) return (string)action.Path;
            }
            catch { }
            return null;
        }

        static bool PointsHere(dynamic folder, string name)
        {
            string cmd = CommandOf(folder, name);
            return cmd != null && cmd.Equals(Paths.Exe, StringComparison.OrdinalIgnoreCase);
        }

        public static bool PointsHere(string name)
        {
            try { return PointsHere(Folder(), name); } catch { return false; }
        }

        public static bool Run(string name)
        {
            try
            {
                dynamic task = Get(name);
                if (task == null) return false;
                task.Run(null);
                return true;
            }
            catch { return false; }
        }

        static void Create(dynamic folder, string name)
        {
            if (!Paths.IsInstalled) return;
            bool logon = name == Logon;
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>" +
                (logon ? "Abre o VPN Panel na bandeja ao entrar no Windows." : "Abre o VPN Panel sem o aviso de permissão do Windows.") +
                "</Description></RegistrationInfo>\r\n" +
                (logon ? "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" : "") +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Paths.Exe) + "</Command>" +
                (logon ? "<Arguments>--tray</Arguments>" : "") + "</Exec></Actions>\r\n" +
                "</Task>\r\n";
            // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
            folder.RegisterTask(name, xml, 6, null, null, 3, null);
        }

        public static void Create(string name)
        {
            if (!Paths.IsInstalled) return;
            Create(Folder(), name);
        }

        static void Delete(dynamic folder, string name)
        {
            if (Get(folder, name) != null) folder.DeleteTask(name, 0);
        }

        public static void Delete(string name)
        {
            Delete(Folder(), name);
        }

        // Re-point existing tasks at this exe if it was moved, and add/remove the "open" task.
        // Returns whether the Logon task exists, using a single COM connection.
        public static bool Sync(bool skipUac)
        {
            try
            {
                dynamic folder = Folder();
                bool logonExists = Get(folder, Logon) != null;
                if (Paths.IsInstalled)
                {
                    if (logonExists && !PointsHere(folder, Logon)) Create(folder, Logon);
                    if (skipUac && !PointsHere(folder, Open)) Create(folder, Open);
                    if (!skipUac) Delete(folder, Open);
                }
                return logonExists;
            }
            catch { return false; }
        }
    }
}
