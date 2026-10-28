using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ZapretLauncher
{
    // Автозапуск через Планировщик задач: при входе в Windows, с наивысшими правами (UAC подтверждается
    // один раз — при включении), без лимита времени работы и без запрета на питании от батареи.
    static class Autostart
    {
        const string TaskName = "ZapretLauncher";

        public static bool Enabled()
        {
            int code; string o;
            Schtasks("/Query /TN \"" + TaskName + "\"", out code, out o);
            return code == 0;
        }

        public static void Set(bool on, string exe)
        {
            int code; string output;
            if (!on)
            {
                Schtasks("/Delete /TN \"" + TaskName + "\" /F", out code, out output);
                if (code != 0 && Enabled()) throw new Exception("Не удалось удалить задачу автозапуска: " + output.Trim());
                return;
            }
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Zapret Launcher: запуск в трее при входе в Windows</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "ZapretLauncher-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out code, out output);
                if (code != 0) throw new Exception("Не удалось создать задачу автозапуска: " + output.Trim());
            }
            finally { try { File.Delete(file); } catch { } }
        }

        static void Schtasks(string args, out int code, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit(10000);
                code = p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
