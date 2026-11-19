using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    // Records where the running tray app lives so the Stream Deck plugin can start it.
    // The plugin reads this per-user file instead of trusting a value from the named pipe.
    public static class AppLocation
    {
        public static string DefaultPath()
        { return DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); }
        // No record when the local app data folder is unknown, rather than a path relative to the current folder.
        internal static string DefaultPath(string localAppData)
        { return string.IsNullOrEmpty(localAppData)?null:Path.Combine(localAppData,"AIUsageManager","app-location.json"); }
        public static bool Save(string file,string executablePath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var value=new Dictionary<string,object> { {"schemaVersion",1}, {"path",executablePath} };
                File.WriteAllText(file+".tmp",new JavaScriptSerializer().Serialize(value));
                if(File.Exists(file))File.Replace(file+".tmp",file,null);else File.Move(file+".tmp",file);
                return true;
            }
            catch(IOException) { return false; }
            catch(UnauthorizedAccessException) { return false; }
            catch(ArgumentException) { return false; }
            catch(NotSupportedException) { return false; }
        }
    }
}
