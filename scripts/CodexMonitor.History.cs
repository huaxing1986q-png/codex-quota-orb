using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexMonitor
{
    public sealed class DailyTokenUsage
    {
        public DateTime Day;
        public long Tokens;
    }

    public sealed class TokenHistorySnapshot
    {
        public bool Available;
        public string Status = "unavailable";
        public string Message = "Token history is unavailable.";
        public DateTime SampleUtc;
        public DateTime SinceLocal;
        public long TotalTokens;
        public long TodayTokens;
        public long WeekTokens;
        public long MonthTokens;
        public int SessionFiles;
        public int ReusedFiles;
        public List<DailyTokenUsage> Days = new List<DailyTokenUsage>();
    }

    internal sealed class HistoryCacheDocument
    {
        public int Version { get; set; }
        public Dictionary<string, HistoryCacheEntry> Files { get; set; }
    }

    internal sealed class HistoryCacheEntry
    {
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long TotalTokens { get; set; }
        public string FirstDay { get; set; }
        public Dictionary<string, long> Days { get; set; }
    }

    public static class TokenHistoryReader
    {
        private const int CacheVersion = 2;
        private const int MaxCacheBytes = 8 * 1024 * 1024;
        private const int StreamBufferBytes = 128 * 1024;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxCacheBytes };

        public static string ReadLatestJson(string sessionsRoot, string cachePath)
        {
            TokenHistorySnapshot snapshot = ReadLatest(sessionsRoot, cachePath, null);
            Dictionary<string, object> output = new Dictionary<string, object>();
            output["available"] = snapshot.Available;
            output["status"] = snapshot.Status;
            output["total_tokens"] = snapshot.TotalTokens;
            output["today_tokens"] = snapshot.TodayTokens;
            output["week_tokens"] = snapshot.WeekTokens;
            output["month_tokens"] = snapshot.MonthTokens;
            output["days"] = snapshot.Days.Count;
            output["session_files"] = snapshot.SessionFiles;
            output["reused_files"] = snapshot.ReusedFiles;
            output["sample_utc"] = snapshot.SampleUtc.ToString("o");
            output["message"] = snapshot.Message;
            return Json.Serialize(output);
        }

        public static TokenHistorySnapshot ReadLatest(string sessionsRoot, string cachePath, Action<int, int> progress)
        {
            TokenHistorySnapshot snapshot = new TokenHistorySnapshot();
            snapshot.SampleUtc = DateTime.UtcNow;
            try
            {
                List<FileInfo> files = EnumerateSessionFiles(sessionsRoot);
                HistoryCacheDocument cache = ReadCache(cachePath);
                Dictionary<string, HistoryCacheEntry> nextEntries = new Dictionary<string, HistoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, long> aggregate = new Dictionary<string, long>(StringComparer.Ordinal);
                DateTime since = DateTime.MaxValue;
                int reused = 0;

                for (int index = 0; index < files.Count; index++)
                {
                    FileInfo file = files[index];
                    if (progress != null) progress(index, files.Count);
                    string key = RelativeKey(sessionsRoot, file.FullName);
                    HistoryCacheEntry entry;
                    bool canReuse = cache.Files.TryGetValue(key, out entry)
                        && entry != null
                        && entry.Length == file.Length
                        && entry.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks
                        && entry.Days != null;
                    if (!canReuse)
                    {
                        entry = ScanFile(file);
                    }
                    else reused++;

                    nextEntries[key] = entry;
                    MergeDays(aggregate, entry.Days);
                    DateTime first;
                    if (TryParseDay(entry.FirstDay, out first) && first < since) since = first;
                }

                if (progress != null) progress(files.Count, files.Count);
                cache.Files = nextEntries;
                WriteCache(cachePath, cache);

                DateTime today = DateTime.Today;
                int mondayOffset = ((int)today.DayOfWeek + 6) % 7;
                DateTime weekStart = today.AddDays(-mondayOffset);
                DateTime monthStart = new DateTime(today.Year, today.Month, 1);
                List<string> dayKeys = new List<string>(aggregate.Keys);
                dayKeys.Sort(StringComparer.Ordinal);
                foreach (string dayKey in dayKeys)
                {
                    DateTime day;
                    if (!TryParseDay(dayKey, out day)) continue;
                    long tokens = aggregate[dayKey];
                    snapshot.Days.Add(new DailyTokenUsage { Day = day, Tokens = tokens });
                    snapshot.TotalTokens = SafeAdd(snapshot.TotalTokens, tokens);
                    if (day == today) snapshot.TodayTokens = SafeAdd(snapshot.TodayTokens, tokens);
                    if (day >= weekStart && day <= today) snapshot.WeekTokens = SafeAdd(snapshot.WeekTokens, tokens);
                    if (day >= monthStart && day <= today) snapshot.MonthTokens = SafeAdd(snapshot.MonthTokens, tokens);
                }

                snapshot.Available = files.Count > 0;
                snapshot.Status = snapshot.Available ? "ok" : "empty";
                snapshot.Message = snapshot.Available ? null : "No local Codex session history was found.";
                snapshot.SinceLocal = since == DateTime.MaxValue ? DateTime.MinValue : since;
                snapshot.SessionFiles = files.Count;
                snapshot.ReusedFiles = reused;
                return snapshot;
            }
            catch
            {
                snapshot.Status = "unavailable";
                snapshot.Message = "Local Codex token history could not be read.";
                return snapshot;
            }
        }

        internal static int AppendSelfTests(List<string> failures)
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexMonitor.History." + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string session = Path.Combine(root, "rollout-test.jsonl");
                string[] lines = new[] {
                    "{\"timestamp\":\"2026-07-20T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":100},\"last_token_usage\":{\"total_tokens\":100}}}}",
                    "{\"timestamp\":\"2026-07-20T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":250},\"last_token_usage\":{\"total_tokens\":150}}}}",
                    "{\"timestamp\":\"2026-07-21T03:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":300},\"last_token_usage\":{\"total_tokens\":50}}}}"
                };
                File.WriteAllLines(session, lines, new UTF8Encoding(false));
                string cache = Path.Combine(root, "cache.json");
                TokenHistorySnapshot parsed = ReadLatest(root, cache, null);
                Assert(failures, parsed.Available, "history fixture available");
                Assert(failures, parsed.TotalTokens == 300, "history total token sum");
                Assert(failures, parsed.Days.Count == 2, "history daily buckets");
                Assert(failures, parsed.Days[0].Tokens == 250 && parsed.Days[1].Tokens == 50, "history incremental allocation");
                TokenHistorySnapshot reused = ReadLatest(root, cache, null);
                Assert(failures, reused.ReusedFiles == 1, "history cache reuse");
                return 5;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static List<FileInfo> EnumerateSessionFiles(string sessionsRoot)
        {
            List<FileInfo> files = new List<FileInfo>();
            AddFiles(files, sessionsRoot);
            try
            {
                DirectoryInfo root = new DirectoryInfo(sessionsRoot);
                DirectoryInfo codexHome = root.Parent;
                if (codexHome != null) AddFiles(files, Path.Combine(codexHome.FullName, "archived_sessions"));
            }
            catch { }
            files.Sort(delegate(FileInfo left, FileInfo right) {
                return StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName);
            });
            return files;
        }

        private static void AddFiles(List<FileInfo> files, string root)
        {
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                    files.Add(new FileInfo(path));
            }
            catch { }
        }

        private static HistoryCacheEntry ScanFile(FileInfo file)
        {
            HistoryCacheEntry entry = new HistoryCacheEntry();
            entry.Length = file.Length;
            entry.LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
            entry.Days = new Dictionary<string, long>(StringComparer.Ordinal);
            long previousTotal = 0;
            string lastDay = null;

            using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes, FileOptions.SequentialScan))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, StreamBufferBytes))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) continue;
                    DateTime timestampUtc;
                    long cumulative;
                    long incremental;
                    if (!TryParseTokenLine(line, out timestampUtc, out cumulative, out incremental)) continue;
                    long delta;
                    if (cumulative > 0)
                    {
                        delta = cumulative >= previousTotal ? cumulative - previousTotal : cumulative;
                        previousTotal = cumulative;
                    }
                    else
                    {
                        delta = incremental;
                        previousTotal = SafeAdd(previousTotal, delta);
                    }
                    if (delta <= 0) continue;
                    string day = timestampUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    long existing;
                    entry.Days.TryGetValue(day, out existing);
                    entry.Days[day] = SafeAdd(existing, delta);
                    entry.TotalTokens = SafeAdd(entry.TotalTokens, delta);
                    lastDay = day;
                    if (String.IsNullOrEmpty(entry.FirstDay) || StringComparer.Ordinal.Compare(day, entry.FirstDay) < 0)
                        entry.FirstDay = day;
                }
            }

            if (entry.TotalTokens == 0 && previousTotal > 0 && !String.IsNullOrEmpty(lastDay))
            {
                entry.TotalTokens = previousTotal;
                entry.Days[lastDay] = previousTotal;
            }
            return entry;
        }

        private static bool TryParseTokenLine(string line, out DateTime timestampUtc, out long cumulative, out long incremental)
        {
            timestampUtc = DateTime.MinValue;
            cumulative = 0;
            incremental = 0;
            try
            {
                Dictionary<string, object> root = Json.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || Convert.ToString(Value(root, "type"), CultureInfo.InvariantCulture) != "event_msg") return false;
                Dictionary<string, object> payload = Value(root, "payload") as Dictionary<string, object>;
                if (payload == null || Convert.ToString(Value(payload, "type"), CultureInfo.InvariantCulture) != "token_count") return false;
                Dictionary<string, object> info = Value(payload, "info") as Dictionary<string, object>;
                if (info == null) return false;
                Dictionary<string, object> total = Value(info, "total_token_usage") as Dictionary<string, object>;
                Dictionary<string, object> last = Value(info, "last_token_usage") as Dictionary<string, object>;
                cumulative = LongValue(total, "total_tokens");
                incremental = LongValue(last, "total_tokens");
                string timestamp = Convert.ToString(Value(root, "timestamp"), CultureInfo.InvariantCulture);
                if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestampUtc))
                    return false;
                return cumulative > 0 || incremental > 0;
            }
            catch { return false; }
        }

        private static HistoryCacheDocument ReadCache(string path)
        {
            HistoryCacheDocument empty = new HistoryCacheDocument {
                Version = CacheVersion,
                Files = new Dictionary<string, HistoryCacheEntry>(StringComparer.OrdinalIgnoreCase)
            };
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0 || file.Length > MaxCacheBytes) return empty;
                HistoryCacheDocument parsed = Json.Deserialize<HistoryCacheDocument>(File.ReadAllText(path, Encoding.UTF8));
                if (parsed == null || parsed.Version != CacheVersion || parsed.Files == null) return empty;
                return parsed;
            }
            catch { return empty; }
        }

        private static void WriteCache(string path, HistoryCacheDocument cache)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string temporary = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(cache), new UTF8Encoding(false));
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
            catch { }
        }

        private static string RelativeKey(string sessionsRoot, string path)
        {
            try
            {
                Uri root = new Uri(Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Uri file = new Uri(Path.GetFullPath(path));
                return Uri.UnescapeDataString(root.MakeRelativeUri(file).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return Path.GetFileName(path); }
        }

        private static void MergeDays(Dictionary<string, long> target, Dictionary<string, long> source)
        {
            if (source == null) return;
            foreach (KeyValuePair<string, long> pair in source)
            {
                long existing;
                target.TryGetValue(pair.Key, out existing);
                target[pair.Key] = SafeAdd(existing, pair.Value);
            }
        }

        private static object Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value : null;
        }

        private static long LongValue(Dictionary<string, object> values, string key)
        {
            object value = Value(values, key);
            if (value == null) return 0;
            long parsed;
            return Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static bool TryParseDay(string value, out DateTime day)
        {
            return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        private static long SafeAdd(long left, long right)
        {
            if (right > 0 && left > Int64.MaxValue - right) return Int64.MaxValue;
            if (right < 0 && left < Int64.MinValue - right) return Int64.MinValue;
            return left + right;
        }

        private static void Assert(List<string> failures, bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }
    }
}
