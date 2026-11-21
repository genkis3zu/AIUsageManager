using System;
using Microsoft.Win32;
using AIUsageManager;

internal static partial class Tests
{
    sealed class FakeStartup : IStartupStore
    {
        public string Value; public bool Fail, Disabled, FailEnable;
        public string Read() { return Value; }
        public bool Write(string command) { if (Fail) return false; Value = command; return true; }
        public bool Delete() { if (Fail) return false; Value = null; return true; }
        public bool IsDisabled() { return Disabled; }
        public bool ClearDisabled() { if (FailEnable) return false; Disabled = false; return true; }
    }
    static void RunStartupTests()
    {
        const string exe = @"C:\Apps\AI Usage\AIUsageManager.exe";
        Check("Starting at sign-in runs this app, quoted, in the background", () => {
            Assert(StartAtSignIn.Command(exe) == "\"C:\\Apps\\AI Usage\\AIUsageManager.exe\" --background" && StartAtSignIn.Argument == "--background");
        });
        Check("Starting at sign-in is on only when the entry runs this app", () => {
            var store = new FakeStartup();
            Assert(!StartAtSignIn.IsOn(store, exe));
            store.Value = "\"c:\\apps\\ai usage\\aiusagemanager.exe\" --background";
            Assert(StartAtSignIn.IsOn(store, exe));
            store.Value = "\"D:\\Old\\AIUsageManager.exe\" --background";
            Assert(!StartAtSignIn.IsOn(store, exe));
            store.Value = "  " + StartAtSignIn.Command(exe) + " ";
            Assert(StartAtSignIn.IsOn(store, exe));
        });
        Check("An entry turned off in Windows settings shows as off, and turning it on again clears that", () => {
            var store = new FakeStartup { Value = StartAtSignIn.Command(exe), Disabled = true };
            Assert(!StartAtSignIn.IsOn(store, exe));
            Assert(StartAtSignIn.Set(store, exe, true) && !store.Disabled && StartAtSignIn.IsOn(store, exe));
            store.Disabled = true; store.FailEnable = true;
            Assert(!StartAtSignIn.Set(store, exe, true) && !StartAtSignIn.IsOn(store, exe));
        });
        Check("Turning starting at sign-in on and off writes and removes the entry", () => {
            var store = new FakeStartup { Value = "\"D:\\Old\\AIUsageManager.exe\" --background" };
            Assert(StartAtSignIn.Set(store, exe, true) && store.Value == StartAtSignIn.Command(exe));
            Assert(StartAtSignIn.Set(store, exe, false) && store.Value == null);
            store.Fail = true;
            Assert(!StartAtSignIn.Set(store, exe, true) && !StartAtSignIn.Set(store, exe, false));
        });
        Check("The sign-in entry goes to the current user's Run key; Windows keeps its on/off flag beside it", () => {
            Assert(RegistryStartupStore.RunKey == @"Software\Microsoft\Windows\CurrentVersion\Run" && RegistryStartupStore.ValueName == "AIUsageManager");
            Assert(RegistryStartupStore.ApprovedKey == @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        });
        Check("The registry entry and the Windows on/off flag are read and changed under the given keys", () => {
            const string parent = @"Software\AIUsageManager.Tests";
            string own = parent + @"\" + Guid.NewGuid().ToString("N");
            try
            {
                var store = new RegistryStartupStore(own + @"\Run", own + @"\Approved", "AIUsageManager");
                Assert(store.Read() == null && store.Delete() && !store.IsDisabled() && store.ClearDisabled());
                Assert(store.Write("\"x.exe\" --background") && store.Read() == "\"x.exe\" --background");
                Assert(store.Delete() && store.Read() == null);
                using (var key = Registry.CurrentUser.CreateSubKey(own + @"\Approved"))
                {
                    key.SetValue("AIUsageManager", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                    Assert(store.IsDisabled());
                    key.SetValue("AIUsageManager", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                    Assert(!store.IsDisabled());
                    key.SetValue("AIUsageManager", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
                Assert(store.ClearDisabled() && !store.IsDisabled());
            }
            finally
            {
                // Only this run's key: another test run may be using the parent at the same time.
                Registry.CurrentUser.DeleteSubKeyTree(own, false);
                try { Registry.CurrentUser.DeleteSubKey(parent, false); } catch (InvalidOperationException) { }
            }
        });
    }
}
