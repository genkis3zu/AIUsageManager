using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace AIUsageManager
{
    // Where the start at sign-in is kept. Only the tray menu changes it, when the user turns it on or off.
    public interface IStartupStore
    {
        string Read();
        bool Write(string command);
        bool Delete();
        // Windows settings (Startup apps) and Task Manager turn an entry off with a separate flag and keep the entry itself.
        bool IsDisabled();
        bool ClearDisabled();
    }

    // One value under the current user's Run key: Windows runs the command at sign-in. No administrator rights are needed.
    public sealed class RegistryStartupStore : IStartupStore
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "AIUsageManager";
        // Windows' on/off flag for the same name: 12 bytes, the first one 02 when on and 03 when turned off.
        public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        readonly string keyPath, approvedKeyPath, valueName;
        public RegistryStartupStore(string keyPath, string approvedKeyPath, string valueName)
        { this.keyPath = keyPath; this.approvedKeyPath = approvedKeyPath; this.valueName = valueName; }
        public static RegistryStartupStore Default() { return new RegistryStartupStore(RunKey, ApprovedKey, ValueName); }
        public string Read()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) return key == null ? null : key.GetValue(valueName) as string; }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }
        public bool Write(string command)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key == null) return false;
                    key.SetValue(valueName, command, RegistryValueKind.String);
                    return true;
                }
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
        public bool Delete() { return DeleteValue(keyPath); }
        public bool IsDisabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(approvedKeyPath))
                {
                    var flag = key == null ? null : key.GetValue(valueName) as byte[];
                    return flag != null && flag.Length > 0 && (flag[0] & 1) == 1;
                }
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
        public bool ClearDisabled() { return DeleteValue(approvedKeyPath); }
        bool DeleteValue(string path)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (key != null) key.DeleteValue(valueName, false);
                    return true;
                }
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
    }

    public static class StartAtSignIn
    {
        // A second start with this argument leaves the running tray app alone (no window), so the start at sign-in
        // and the start from the Stream Deck plugin can happen at the same time.
        public const string Argument = "--background";
        public static string Command(string executablePath) { return "\"" + executablePath + "\" " + Argument; }
        // On only when the entry starts this copy of the app and Windows has not turned it off;
        // an entry left by a copy in another folder shows as off.
        public static bool IsOn(IStartupStore store, string executablePath)
        {
            string value = store.Read();
            return value != null && string.Equals(value.Trim(), Command(executablePath), StringComparison.OrdinalIgnoreCase) && !store.IsDisabled();
        }
        // Turning it on in the tray also clears an "off" set earlier in Windows settings: the user has just asked for it.
        public static bool Set(IStartupStore store, string executablePath, bool on)
        { return on ? store.Write(Command(executablePath)) && store.ClearDisabled() : store.Delete(); }
    }
}
