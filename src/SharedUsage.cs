using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    // A successful response, with the time it arrived. Body is the response JSON as read, never written back as text.
    internal sealed class SharedResult
    {
        public DateTimeOffset FetchedAt;
        public Dictionary<string, object> Body;
    }
    internal sealed class SharedRateLimit
    {
        public DateTimeOffset ObservedAt;
        public DateTimeOffset Until;
    }
    // The folder that the apps showing AI usage on this PC share (docs/usage-shared-cache-protocol.md, version 1):
    // per service, the last successful response (<name>.json) and the time before which no app calls it (<name>.rate-limit.json).
    // A missing, unreadable or broken file counts as no file, and a failed write is dropped: the folder never stops a fetch.
    internal sealed class SharedUsageFolder
    {
        const int Version = 1;
        const long MaxBytes = 1048576;
        const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        static readonly string[] RemovedKeys = { "email", "user_id", "account_id" };
        const string ReadMe =
            "This folder is shared by apps that show AI usage (for example \"Input Assistant for AI Agents\"\n" +
            "for VS Code and \"AIUsageManager\"). Each app writes the usage it fetched here so that the other\n" +
            "apps on this PC can reuse it instead of calling the same service again.\n" +
            "It contains usage numbers and reset times only. No login information is stored here.\n" +
            "It is safe to delete this folder at any time; the apps will create it again.\n";
        static readonly string Writer = "ai-usage-manager/" + typeof(SharedUsageFolder).Assembly.GetName().Version.ToString(3);
        readonly string folder;
        public SharedUsageFolder(string folder) { this.folder = folder; }
        public static string DefaultPath(string profile) { return Path.Combine(profile, ".ai-usage-shared"); }

        public SharedResult ReadResult(string provider, DateTimeOffset now)
        {
            var data = Read(provider + ".json", provider);
            var fetched = Time(data, "fetchedAt");
            var body = Json.Obj(data, "body");
            if (!fetched.HasValue || body == null || fetched.Value > now.AddSeconds(60)) return null;
            return new SharedResult { FetchedAt = fetched.Value, Body = body };
        }
        public SharedRateLimit ReadRateLimit(string provider, DateTimeOffset now)
        {
            var data = Read(provider + ".rate-limit.json", provider);
            var observed = Time(data, "observedAt");
            var until = Time(data, "until");
            if (!observed.HasValue || !until.HasValue || observed.Value > now.AddSeconds(60)) return null;
            // A limit longer than 60 minutes ends 60 minutes after the HTTP 429 (section 6).
            var longest = observed.Value.AddMinutes(60);
            return new SharedRateLimit { ObservedAt = observed.Value, Until = until.Value > longest ? longest : until.Value };
        }
        // Retry-After if the service sent one, otherwise 5 minutes; never under 3 or over 60 minutes (section 3.2).
        public static DateTimeOffset Until(DateTimeOffset observedAt, TimeSpan? retryAfter)
        {
            var wait = retryAfter ?? TimeSpan.FromMinutes(5);
            if (wait < TimeSpan.FromMinutes(3)) wait = TimeSpan.FromMinutes(3);
            if (wait > TimeSpan.FromMinutes(60)) wait = TimeSpan.FromMinutes(60);
            return observedAt + wait;
        }
        // Only a response this app received itself, never one read from this folder (section 4).
        public bool WriteResult(string provider, string response, DateTimeOffset fetchedAt)
        {
            var body = Json.Parse(response);
            if (body == null) return false;
            Remove(body);
            return Write(provider + ".json", new Dictionary<string, object> {
                { "version", Version }, { "provider", provider }, { "fetchedAt", Text(fetchedAt) }, { "writer", Writer }, { "body", body } });
        }
        public bool WriteRateLimit(string provider, DateTimeOffset observedAt, DateTimeOffset until)
        {
            return Write(provider + ".rate-limit.json", new Dictionary<string, object> {
                { "version", Version }, { "provider", provider }, { "observedAt", Text(observedAt) }, { "until", Text(until) }, { "writer", Writer } });
        }

        Dictionary<string, object> Read(string name, string provider)
        {
            string path = Path.Combine(folder, name);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length > MaxBytes) return null;
                    var data = Json.Parse(ReadOnlyFiles.Small(path));
                    if (data != null) return Number(data, "version") == Version && Json.Str(data, "provider") == provider ? data : null;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { return null; }
                catch (NotSupportedException) { return null; }
                // The other app may have been replacing the file just then: read it once more (section 6).
                if (attempt == 0) Thread.Sleep(50);
            }
            return null;
        }
        static double? Number(Dictionary<string, object> data, string key) { return Json.Number(Json.Get(data, key)); }
        // Times are ISO 8601 text ending in Z or an offset such as +09:00 (section 3). A number, a time without either
        // (another app might read it as local time) or any other text is not a time here.
        static readonly string[] TimeFormats = {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" };
        static DateTimeOffset? Time(Dictionary<string, object> data, string key)
        {
            string value = Json.Str(data, key); DateTimeOffset result;
            return value != null && DateTimeOffset.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result) ? result : (DateTimeOffset?)null;
        }
        static string Text(DateTimeOffset value) { return value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture); }
        static string Serialize(object value) { return new JavaScriptSerializer { MaxJsonLength = 4194304, RecursionLimit = 64 }.Serialize(value); }
        // Keys that name the person are not needed to show usage (section 3.3).
        static void Remove(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                foreach (string key in RemovedKeys) map.Remove(key);
                foreach (var child in map.Values) Remove(child);
                return;
            }
            var list = value as object[];
            if (list != null) foreach (var child in list) Remove(child);
        }
        // Write a temporary file in the folder, then put it in place in one step, so no app reads half a file (section 4).
        bool Write(string name, Dictionary<string, object> content)
        {
            string temp = null;
            try
            {
                Directory.CreateDirectory(folder);
                PutReadMe();
                int process; using (var current = Process.GetCurrentProcess()) process = current.Id;
                temp = Path.Combine(folder, name + "." + process + ".tmp");
                File.WriteAllText(temp, Serialize(content), new UTF8Encoding(false));
                if (MoveFileEx(temp, Path.Combine(folder, name), MoveFileReplaceExisting)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (InvalidOperationException) { }
            if (temp != null) try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        void PutReadMe()
        {
            string path = Path.Combine(folder, "README.txt");
            if (File.Exists(path)) return;
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(ReadMe);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        const int MoveFileReplaceExisting = 1;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
