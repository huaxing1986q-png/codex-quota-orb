using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexMonitor
{
    public sealed class DailyTokenUsage
    {
        public DateTime Day;
        public long Tokens;
    }

    public sealed class ContextCapacitySnapshot
    {
        public bool Available;
        public string Status = "unavailable";
        public DateTime SampleUtc;
        public DateTime ActivityUtc;
        public string SessionId;
        public string SelectionSource;
        public long CapacityTokens;
        public long InputTokens;
        public long CachedInputTokens;
        public bool InputBreakdownAvailable = true;
        public long OutputTokens;
        public long ReasoningOutputTokens;
        public long SessionTotalTokens;
        public int ProjectCount;
        public int ConversationCount;

        public long FreshInputTokens
        {
            get { return Math.Max(0, InputTokens - CachedInputTokens); }
        }

        public long RemainingTokens
        {
            get { return Math.Max(0, CapacityTokens - InputTokens); }
        }

        public double UsedPercent
        {
            get
            {
                if (!Available || CapacityTokens <= 0) return -1;
                return Math.Max(0, Math.Min(100, InputTokens * 100d / CapacityTokens));
            }
        }
    }

    public sealed class ConversationTokenUsage
    {
        public string SessionId;
        public string ProjectPath;
        public string ProjectName;
        public DateTime StartedLocal;
        public DateTime UpdatedLocal;
        public long Tokens;
        public long ContextInputTokens;
        public long CachedInputTokens;
        public long OutputTokens;
        public long ReasoningOutputTokens;
    }

    public sealed class ProjectTokenUsage
    {
        public string ProjectPath;
        public string ProjectName;
        public long Tokens;
        public long ContextInputTokens;
        public long CachedInputTokens;
        public long OutputTokens;
        public long ReasoningOutputTokens;
        public int Conversations;
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
        public ContextCapacitySnapshot Context = new ContextCapacitySnapshot();
        public List<ProjectTokenUsage> Projects = new List<ProjectTokenUsage>();
        public List<ConversationTokenUsage> Conversations = new List<ConversationTokenUsage>();
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
        public long ContextInputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public bool HasContextBreakdown { get; set; }
        public string FirstDay { get; set; }
        public Dictionary<string, long> Days { get; set; }
    }

    internal sealed class ParsedTokenLine
    {
        public DateTime TimestampUtc;
        public long CumulativeTotal;
        public long IncrementalTotal;
        public long CumulativeInput;
        public long IncrementalInput;
        public long CumulativeCachedInput;
        public long IncrementalCachedInput;
        public long CumulativeOutput;
        public long IncrementalOutput;
        public long CumulativeReasoningOutput;
        public long IncrementalReasoningOutput;
        public bool HasCumulativeInput;
        public bool HasIncrementalInput;
        public bool HasCumulativeCachedInput;
        public bool HasIncrementalCachedInput;
        public bool HasCumulativeOutput;
        public bool HasIncrementalOutput;
        public bool HasCumulativeReasoningOutput;
        public bool HasIncrementalReasoningOutput;
    }

    public static class TokenHistoryReader
    {
        private const int CacheVersion = 3;
        private const int MaxCacheBytes = 8 * 1024 * 1024;
        private const int StreamBufferBytes = 128 * 1024;
        private const int MaxContextTailBytes = 4 * 1024 * 1024;
        private const int MaxContextCandidateFiles = 24;
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
            output["projects"] = snapshot.Projects.Count;
            output["conversations"] = snapshot.Conversations.Count;
            output["context"] = new Dictionary<string, object> {
                { "available", snapshot.Context.Available },
                { "status", snapshot.Context.Status },
                { "scope", "cumulative_local_history" },
                { "input_tokens", snapshot.Context.InputTokens },
                { "cached_input_tokens", snapshot.Context.CachedInputTokens },
                { "fresh_input_tokens", snapshot.Context.InputBreakdownAvailable ? (object)snapshot.Context.FreshInputTokens : null },
                { "input_breakdown_available", snapshot.Context.InputBreakdownAvailable },
                { "output_tokens", snapshot.Context.OutputTokens },
                { "reasoning_output_tokens", snapshot.Context.ReasoningOutputTokens },
                { "history_total_tokens", snapshot.TotalTokens },
                { "share_of_history_percent", snapshot.TotalTokens <= 0 ? 0 : snapshot.Context.InputTokens * 100d / snapshot.TotalTokens },
                { "projects", snapshot.Context.ProjectCount },
                { "conversations", snapshot.Context.ConversationCount },
                { "sample_utc", snapshot.Context.SampleUtc == DateTime.MinValue ? null : (object)snapshot.Context.SampleUtc.ToString("o") }
            };
            return Json.Serialize(output);
        }

        public static TokenHistorySnapshot ReadLatest(string sessionsRoot, string cachePath, Action<int, int> progress)
        {
            TokenHistorySnapshot snapshot = new TokenHistorySnapshot();
            snapshot.SampleUtc = DateTime.UtcNow;
            snapshot.Context = new ContextCapacitySnapshot { SampleUtc = snapshot.SampleUtc };
            try
            {
                List<FileInfo> files = EnumerateSessionFiles(sessionsRoot);
                HistoryCacheDocument cache = ReadCache(cachePath);
                Dictionary<string, HistoryCacheEntry> nextEntries = new Dictionary<string, HistoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, long> aggregate = new Dictionary<string, long>(StringComparer.Ordinal);
                Dictionary<string, ProjectTokenUsage> projectTotals = new Dictionary<string, ProjectTokenUsage>(StringComparer.OrdinalIgnoreCase);
                DateTime since = DateTime.MaxValue;
                int reused = 0;
                bool hasCumulativeContext = false;
                bool completeContextBreakdown = true;

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
                    ConversationTokenUsage conversation = ReadConversation(file, entry.TotalTokens);
                    conversation.ContextInputTokens = Math.Max(0, entry.ContextInputTokens);
                    conversation.CachedInputTokens = Math.Max(0, Math.Min(conversation.ContextInputTokens, entry.CachedInputTokens));
                    conversation.OutputTokens = Math.Max(0, entry.OutputTokens);
                    conversation.ReasoningOutputTokens = Math.Max(0, Math.Min(conversation.OutputTokens, entry.ReasoningOutputTokens));
                    snapshot.Conversations.Add(conversation);
                    ProjectTokenUsage project;
                    string projectKey = String.IsNullOrWhiteSpace(conversation.ProjectPath) ? "(unknown)" : conversation.ProjectPath;
                    if (!projectTotals.TryGetValue(projectKey, out project))
                    {
                        project = new ProjectTokenUsage {
                            ProjectPath = conversation.ProjectPath,
                            ProjectName = conversation.ProjectName
                        };
                        projectTotals[projectKey] = project;
                    }
                    project.Tokens = SafeAdd(project.Tokens, conversation.Tokens);
                    project.ContextInputTokens = SafeAdd(project.ContextInputTokens, conversation.ContextInputTokens);
                    project.CachedInputTokens = SafeAdd(project.CachedInputTokens, conversation.CachedInputTokens);
                    project.OutputTokens = SafeAdd(project.OutputTokens, conversation.OutputTokens);
                    project.ReasoningOutputTokens = SafeAdd(project.ReasoningOutputTokens, conversation.ReasoningOutputTokens);
                    project.Conversations++;
                    snapshot.Context.InputTokens = SafeAdd(snapshot.Context.InputTokens, conversation.ContextInputTokens);
                    snapshot.Context.CachedInputTokens = SafeAdd(snapshot.Context.CachedInputTokens, conversation.CachedInputTokens);
                    snapshot.Context.OutputTokens = SafeAdd(snapshot.Context.OutputTokens, conversation.OutputTokens);
                    snapshot.Context.ReasoningOutputTokens = SafeAdd(snapshot.Context.ReasoningOutputTokens, conversation.ReasoningOutputTokens);
                    if (entry.HasContextBreakdown) hasCumulativeContext = true;
                    else if (entry.TotalTokens > 0) completeContextBreakdown = false;
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
                snapshot.Projects.AddRange(projectTotals.Values);
                snapshot.Context.CachedInputTokens = Math.Min(snapshot.Context.InputTokens, snapshot.Context.CachedInputTokens);
                snapshot.Context.ReasoningOutputTokens = Math.Min(snapshot.Context.OutputTokens, snapshot.Context.ReasoningOutputTokens);
                snapshot.Context.Available = hasCumulativeContext;
                snapshot.Context.Status = hasCumulativeContext
                    ? (completeContextBreakdown ? "ok" : "partial")
                    : (files.Count == 0 ? "empty" : "unavailable");
                snapshot.Context.InputBreakdownAvailable = hasCumulativeContext && completeContextBreakdown;
                snapshot.Context.SessionTotalTokens = snapshot.TotalTokens;
                snapshot.Projects.Sort(delegate(ProjectTokenUsage left, ProjectTokenUsage right) {
                    int tokens = right.ContextInputTokens.CompareTo(left.ContextInputTokens);
                    return tokens != 0 ? tokens : StringComparer.CurrentCultureIgnoreCase.Compare(left.ProjectName, right.ProjectName);
                });
                snapshot.Conversations.Sort(delegate(ConversationTokenUsage left, ConversationTokenUsage right) {
                    int tokens = right.ContextInputTokens.CompareTo(left.ContextInputTokens);
                    return tokens != 0 ? tokens : right.UpdatedLocal.CompareTo(left.UpdatedLocal);
                });
                snapshot.Context.ProjectCount = snapshot.Projects.Count;
                snapshot.Context.ConversationCount = snapshot.Conversations.Count;
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
                    "{\"timestamp\":\"2026-07-20T00:30:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"01234567-89ab-cdef-0123-456789abcdef\",\"cwd\":\"C:\\\\work\\\\alpha\"}}",
                    "{\"timestamp\":\"2026-07-20T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":100},\"last_token_usage\":{\"total_tokens\":100}}}}",
                    "{\"timestamp\":\"2026-07-20T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":250},\"last_token_usage\":{\"total_tokens\":150}}}}",
                    "{\"timestamp\":\"2026-07-21T03:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":260,\"cached_input_tokens\":180,\"output_tokens\":40,\"reasoning_output_tokens\":12,\"total_tokens\":300},\"last_token_usage\":{\"input_tokens\":90,\"cached_input_tokens\":70,\"output_tokens\":10,\"reasoning_output_tokens\":4,\"total_tokens\":50},\"model_context_window\":200}}}"
                };
                File.WriteAllLines(session, lines, new UTF8Encoding(false));
                string cache = Path.Combine(root, "cache.json");
                TokenHistorySnapshot parsed = ReadLatest(root, cache, null);
                Assert(failures, parsed.Available, "history fixture available");
                Assert(failures, parsed.TotalTokens == 300, "history total token sum");
                Assert(failures, parsed.Days.Count == 2, "history daily buckets");
                Assert(failures, parsed.Days[0].Tokens == 250 && parsed.Days[1].Tokens == 50, "history incremental allocation");
                Assert(failures, parsed.Context.Available, "cumulative context fixture available");
                Assert(failures, parsed.Context.CapacityTokens == 0 && parsed.Context.InputTokens == 260, "cumulative context input");
                Assert(failures, parsed.Context.CachedInputTokens == 180 && parsed.Context.FreshInputTokens == 80, "cumulative context input structure");
                Assert(failures, parsed.Context.InputBreakdownAvailable, "cumulative context input breakdown available");
                Assert(failures, parsed.Context.OutputTokens == 40 && parsed.Context.ReasoningOutputTokens == 12, "cumulative context output structure");
                Assert(failures, parsed.Context.SessionTotalTokens == 300, "cumulative context history total");
                Assert(failures, parsed.Context.ProjectCount == 1 && parsed.Context.ConversationCount == 1, "cumulative context coverage counts");
                string compactedLine = "{\"timestamp\":\"2026-07-21T04:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":191751509},\"last_token_usage\":{\"input_tokens\":0,\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":45442},\"model_context_window\":258400}}}";
                ContextCapacitySnapshot compacted;
                Assert(failures, TryParseContextLine(compactedLine, out compacted), "compacted context fixture parses");
                Assert(failures, compacted != null && compacted.InputTokens == 45442, "compacted context derives occupied input from last total");
                Assert(failures, compacted != null && Math.Abs(compacted.UsedPercent - (45442d * 100d / 258400d)) < 0.001, "compacted context avoids false zero percent");
                Assert(failures, compacted != null && !compacted.InputBreakdownAvailable, "compacted context marks input breakdown unavailable");
                string activeCompactedLine = "{\"timestamp\":\"2026-07-21T03:30:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":300},\"last_token_usage\":{\"input_tokens\":0,\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":80},\"model_context_window\":200}}}";
                File.AppendAllText(session, Environment.NewLine + activeCompactedLine, new UTF8Encoding(false));
                string background = Path.Combine(root, "rollout-2026-07-20T00-00-00-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl");
                File.WriteAllLines(background, new[] {
                    "{\"timestamp\":\"2026-07-20T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"cwd\":\"C:\\\\work\\\\background\"}}",
                    "{\"timestamp\":\"2026-07-21T04:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":77191446},\"last_token_usage\":{\"input_tokens\":0,\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":62554},\"model_context_window\":258400}}}"
                }, new UTF8Encoding(false));
                ContextCapacitySnapshot selected = ReadCurrentContext(root);
                Assert(failures, selected != null && selected.SessionTotalTokens == 300, "background compaction does not replace active context");
                Assert(failures, selected != null && selected.SampleUtc == DateTime.Parse("2026-07-21T03:30:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), "selected context keeps source sample time");
                string parsedConversationId;
                Assert(failures,
                    TryParseActiveConversationLine("2026-07-21T04:00:00Z info thread_stream_view_activity_changed active=true conversationId=01234567-89ab-cdef-0123-456789abcdef rendererWindowAppearance=primary rendererWindowVisible=true", out parsedConversationId)
                        && parsedConversationId == "01234567-89ab-cdef-0123-456789abcdef",
                    "active Codex log line exposes foreground conversation");
                Assert(failures, parsed.Projects.Count == 1 && parsed.Projects[0].Tokens == 300 && parsed.Projects[0].ContextInputTokens == 260 && parsed.Projects[0].Conversations == 1, "project aggregation");
                Assert(failures, parsed.Conversations.Count == 1 && parsed.Conversations[0].ContextInputTokens == 260 && parsed.Conversations[0].SessionId.StartsWith("01234567"), "conversation aggregation");
                TokenHistorySnapshot reused = ReadLatest(root, cache, null);
                Assert(failures, reused.ReusedFiles == 0, "changed history files are rescanned");
                TokenHistorySnapshot reusedAgain = ReadLatest(root, cache, null);
                Assert(failures, reusedAgain.ReusedFiles == 2, "history cache reuse");
                return 22;
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

        private static ContextCapacitySnapshot ReadCurrentContext(string sessionsRoot)
        {
            ContextCapacitySnapshot unavailable = new ContextCapacitySnapshot();
            try
            {
                List<FileInfo> files = new List<FileInfo>();
                AddFiles(files, sessionsRoot);
                files.Sort(delegate(FileInfo left, FileInfo right) {
                    int modified = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                    return modified != 0 ? modified : StringComparer.OrdinalIgnoreCase.Compare(right.FullName, left.FullName);
                });
                string activeConversationId;
                if (TryReadActiveConversationId(out activeConversationId))
                {
                    for (int index = 0; index < files.Count; index++)
                    {
                        if (files[index].Name.IndexOf(activeConversationId, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        ContextCapacitySnapshot active;
                        DateTime activityUtc;
                        if (TryReadLatestContext(files[index], out active, out activityUtc))
                        {
                            active.SelectionSource = "codex-log";
                            return active;
                        }
                    }
                }
                int count = Math.Min(MaxContextCandidateFiles, files.Count);
                ContextCapacitySnapshot best = null;
                DateTime bestActivity = DateTime.MinValue;
                DateTime bestSample = DateTime.MinValue;
                for (int index = 0; index < count; index++)
                {
                    ContextCapacitySnapshot parsed;
                    DateTime activityUtc;
                    if (!TryReadLatestContext(files[index], out parsed, out activityUtc)) continue;
                    DateTime sampleUtc = parsed.SampleUtc;
                    if (best == null
                        || activityUtc > bestActivity
                        || (activityUtc == bestActivity && sampleUtc > bestSample))
                    {
                        best = parsed;
                        bestActivity = activityUtc;
                        bestSample = sampleUtc;
                    }
                }
                if (best != null)
                {
                    best.SelectionSource = "activity";
                    return best;
                }
                unavailable.Status = files.Count == 0 ? "empty" : "unavailable";
                return unavailable;
            }
            catch { return unavailable; }
        }

        private static bool TryReadLatestContext(FileInfo file, out ContextCapacitySnapshot snapshot, out DateTime activityUtc)
        {
            snapshot = null;
            activityUtc = DateTime.MinValue;
            try
            {
                byte[] buffer;
                int bytesRead;
                using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int length = (int)Math.Min(stream.Length, MaxContextTailBytes);
                    if (length <= 0) return false;
                    buffer = new byte[length];
                    stream.Seek(-length, SeekOrigin.End);
                    bytesRead = 0;
                    while (bytesRead < length)
                    {
                        int read = stream.Read(buffer, bytesRead, length - bytesRead);
                        if (read <= 0) break;
                        bytesRead += read;
                    }
                }

                string[] lines = Encoding.UTF8.GetString(buffer, 0, bytesRead).Split('\n');
                for (int index = lines.Length - 1; index >= 0; index--)
                {
                    string line = lines[index].TrimEnd('\r');
                    if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0
                        || line.IndexOf("\"model_context_window\"", StringComparison.Ordinal) < 0) continue;
                    ContextCapacitySnapshot parsed;
                    if (TryParseContextLine(line, out parsed))
                    {
                        if (snapshot == null)
                        {
                            snapshot = parsed;
                            snapshot.SessionId = SessionIdFromFile(file);
                        }
                        if (parsed.InputBreakdownAvailable && activityUtc == DateTime.MinValue)
                            activityUtc = parsed.SampleUtc;
                        if (snapshot != null && activityUtc != DateTime.MinValue)
                        {
                            snapshot.ActivityUtc = activityUtc;
                            return true;
                        }
                    }
                }
            }
            catch { }
            if (snapshot != null)
            {
                snapshot.ActivityUtc = activityUtc;
                return true;
            }
            return false;
        }

        private static bool TryParseContextLine(string line, out ContextCapacitySnapshot snapshot)
        {
            snapshot = null;
            try
            {
                Dictionary<string, object> root = Json.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || Convert.ToString(Value(root, "type"), CultureInfo.InvariantCulture) != "event_msg") return false;
                Dictionary<string, object> payload = Value(root, "payload") as Dictionary<string, object>;
                if (payload == null || Convert.ToString(Value(payload, "type"), CultureInfo.InvariantCulture) != "token_count") return false;
                Dictionary<string, object> info = Value(payload, "info") as Dictionary<string, object>;
                Dictionary<string, object> total = Value(info, "total_token_usage") as Dictionary<string, object>;
                Dictionary<string, object> last = Value(info, "last_token_usage") as Dictionary<string, object>;
                long capacity = LongValue(info, "model_context_window");
                long input = LongValue(last, "input_tokens");
                long rawCached = Math.Max(0, LongValue(last, "cached_input_tokens"));
                long lastTotal = Math.Max(0, LongValue(last, "total_tokens"));
                long output = Math.Max(0, LongValue(last, "output_tokens"));
                long reasoning = Math.Max(0, LongValue(last, "reasoning_output_tokens"));
                bool derivedFromCompaction = input == 0
                    && rawCached == 0
                    && lastTotal > output;
                // A compaction-boundary token_count can zero its input components
                // while last total still carries the compacted context size.
                // total_tokens is input + output; reasoning is an output subset.
                // Never use cumulative total_token_usage as context occupancy.
                if (derivedFromCompaction) input = lastTotal - output;
                if (capacity <= 0 || input < 0) return false;

                DateTime sampleUtc;
                string timestamp = Convert.ToString(Value(root, "timestamp"), CultureInfo.InvariantCulture);
                if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out sampleUtc))
                    sampleUtc = DateTime.MinValue;

                long cached = derivedFromCompaction ? 0 : Math.Max(0, Math.Min(input, rawCached));
                snapshot = new ContextCapacitySnapshot {
                    Available = true,
                    Status = "ok",
                    SampleUtc = sampleUtc,
                    CapacityTokens = capacity,
                    InputTokens = input,
                    CachedInputTokens = cached,
                    InputBreakdownAvailable = !derivedFromCompaction,
                    OutputTokens = output,
                    ReasoningOutputTokens = reasoning,
                    SessionTotalTokens = Math.Max(0, LongValue(total, "total_tokens"))
                };
                return true;
            }
            catch { return false; }
        }

        private static string SessionIdFromFile(FileInfo file)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (name.Length >= 36)
                {
                    string candidate = name.Substring(name.Length - 36);
                    Guid parsed;
                    if (Guid.TryParse(candidate, out parsed)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private static bool TryReadActiveConversationId(out string conversationId)
        {
            conversationId = null;
            try
            {
                List<FileInfo> logs = new List<FileInfo>();
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] roots = new[] {
                    Path.Combine(local, "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs"),
                    Path.Combine(local, "Codex", "Logs"),
                    Path.Combine(roaming, "Codex", "Logs")
                };
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots)
                {
                    if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                    try
                    {
                        foreach (string path in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
                        {
                            if (seen.Add(path)) logs.Add(new FileInfo(path));
                        }
                    }
                    catch { }
                }
                logs.Sort(delegate(FileInfo left, FileInfo right) {
                    return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                });
                int count = Math.Min(8, logs.Count);
                for (int index = 0; index < count; index++)
                {
                    byte[] buffer;
                    int bytesRead;
                    using (FileStream stream = new FileStream(logs[index].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int length = (int)Math.Min(stream.Length, 2 * 1024 * 1024);
                        if (length <= 0) continue;
                        buffer = new byte[length];
                        stream.Seek(-length, SeekOrigin.End);
                        bytesRead = 0;
                        while (bytesRead < length)
                        {
                            int read = stream.Read(buffer, bytesRead, length - bytesRead);
                            if (read <= 0) break;
                            bytesRead += read;
                        }
                    }
                    string[] lines = Encoding.UTF8.GetString(buffer, 0, bytesRead).Split('\n');
                    for (int lineIndex = lines.Length - 1; lineIndex >= 0; lineIndex--)
                    {
                        string parsed;
                        if (TryParseActiveConversationLine(lines[lineIndex], out parsed))
                        {
                            conversationId = parsed;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryParseActiveConversationLine(string line, out string conversationId)
        {
            conversationId = null;
            if (String.IsNullOrWhiteSpace(line)
                || line.IndexOf("thread_stream_view_activity_changed active=true", StringComparison.Ordinal) < 0
                || line.IndexOf("rendererWindowAppearance=primary", StringComparison.Ordinal) < 0
                || line.IndexOf("rendererWindowVisible=true", StringComparison.Ordinal) < 0) return false;
            Match match = Regex.Match(line,
                @"conversationId=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
                RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            conversationId = match.Groups[1].Value;
            return true;
        }

        private static ConversationTokenUsage ReadConversation(FileInfo file, long tokens)
        {
            ConversationTokenUsage result = new ConversationTokenUsage {
                SessionId = Path.GetFileNameWithoutExtension(file.Name),
                ProjectName = "Unknown project",
                StartedLocal = file.CreationTime,
                UpdatedLocal = file.LastWriteTime,
                Tokens = Math.Max(0, tokens)
            };
            try
            {
                using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, StreamBufferBytes))
                {
                    for (int index = 0; index < 24; index++)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.IndexOf("\"session_meta\"", StringComparison.Ordinal) < 0) continue;
                        Dictionary<string, object> root = Json.DeserializeObject(line) as Dictionary<string, object>;
                        Dictionary<string, object> payload = Value(root, "payload") as Dictionary<string, object>;
                        string id = Convert.ToString(Value(payload, "id"), CultureInfo.InvariantCulture);
                        string cwd = Convert.ToString(Value(payload, "cwd"), CultureInfo.InvariantCulture);
                        string timestamp = Convert.ToString(Value(root, "timestamp"), CultureInfo.InvariantCulture);
                        DateTime startedUtc;
                        if (!String.IsNullOrWhiteSpace(id)) result.SessionId = id;
                        if (!String.IsNullOrWhiteSpace(cwd))
                        {
                            result.ProjectPath = cwd;
                            result.ProjectName = ProjectDisplayName(cwd);
                        }
                        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedUtc))
                            result.StartedLocal = startedUtc.ToLocalTime();
                        break;
                    }
                }
            }
            catch { }
            if (String.IsNullOrWhiteSpace(result.SessionId)) result.SessionId = "unknown";
            return result;
        }

        private static string ProjectDisplayName(string path)
        {
            try
            {
                string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(normalized);
                if (!String.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return String.IsNullOrWhiteSpace(path) ? "Unknown project" : path;
        }

        private static HistoryCacheEntry ScanFile(FileInfo file)
        {
            HistoryCacheEntry entry = new HistoryCacheEntry();
            entry.Length = file.Length;
            entry.LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
            entry.Days = new Dictionary<string, long>(StringComparer.Ordinal);
            long previousTotal = 0;
            long previousInput = 0;
            long previousCachedInput = 0;
            long previousOutput = 0;
            long previousReasoningOutput = 0;
            string lastDay = null;

            using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes, FileOptions.SequentialScan))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, StreamBufferBytes))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) continue;
                    ParsedTokenLine parsed;
                    if (!TryParseTokenLine(line, out parsed)) continue;
                    long delta;
                    if (parsed.CumulativeTotal > 0)
                    {
                        delta = parsed.CumulativeTotal >= previousTotal
                            ? parsed.CumulativeTotal - previousTotal
                            : parsed.CumulativeTotal;
                        previousTotal = parsed.CumulativeTotal;
                    }
                    else
                    {
                        delta = parsed.IncrementalTotal;
                        previousTotal = SafeAdd(previousTotal, delta);
                    }
                    if (delta <= 0) continue;
                    string day = parsed.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    long existing;
                    entry.Days.TryGetValue(day, out existing);
                    entry.Days[day] = SafeAdd(existing, delta);
                    entry.TotalTokens = SafeAdd(entry.TotalTokens, delta);
                    long inputDelta = ComponentDelta(
                        parsed.CumulativeInput, parsed.HasCumulativeInput,
                        parsed.IncrementalInput, parsed.HasIncrementalInput,
                        ref previousInput);
                    long cachedDelta = ComponentDelta(
                        parsed.CumulativeCachedInput, parsed.HasCumulativeCachedInput,
                        parsed.IncrementalCachedInput, parsed.HasIncrementalCachedInput,
                        ref previousCachedInput);
                    long outputDelta = ComponentDelta(
                        parsed.CumulativeOutput, parsed.HasCumulativeOutput,
                        parsed.IncrementalOutput, parsed.HasIncrementalOutput,
                        ref previousOutput);
                    long reasoningDelta = ComponentDelta(
                        parsed.CumulativeReasoningOutput, parsed.HasCumulativeReasoningOutput,
                        parsed.IncrementalReasoningOutput, parsed.HasIncrementalReasoningOutput,
                        ref previousReasoningOutput);
                    entry.ContextInputTokens = SafeAdd(entry.ContextInputTokens, inputDelta);
                    entry.CachedInputTokens = SafeAdd(entry.CachedInputTokens, cachedDelta);
                    entry.OutputTokens = SafeAdd(entry.OutputTokens, outputDelta);
                    entry.ReasoningOutputTokens = SafeAdd(entry.ReasoningOutputTokens, reasoningDelta);
                    if (parsed.HasCumulativeInput || parsed.HasIncrementalInput)
                        entry.HasContextBreakdown = true;
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
            entry.CachedInputTokens = Math.Min(entry.ContextInputTokens, entry.CachedInputTokens);
            entry.ReasoningOutputTokens = Math.Min(entry.OutputTokens, entry.ReasoningOutputTokens);
            return entry;
        }

        private static long ComponentDelta(long cumulative, bool hasCumulative, long incremental, bool hasIncremental, ref long previous)
        {
            long delta = 0;
            if (hasCumulative)
            {
                long value = Math.Max(0, cumulative);
                delta = value >= previous ? value - previous : value;
                previous = value;
            }
            else if (hasIncremental)
            {
                delta = Math.Max(0, incremental);
                previous = SafeAdd(previous, delta);
            }
            return Math.Max(0, delta);
        }

        private static bool TryParseTokenLine(string line, out ParsedTokenLine parsed)
        {
            parsed = null;
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
                string timestamp = Convert.ToString(Value(root, "timestamp"), CultureInfo.InvariantCulture);
                DateTime timestampUtc;
                if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestampUtc))
                    return false;
                parsed = new ParsedTokenLine {
                    TimestampUtc = timestampUtc,
                    CumulativeTotal = LongValue(total, "total_tokens"),
                    IncrementalTotal = LongValue(last, "total_tokens"),
                    CumulativeInput = LongValue(total, "input_tokens"),
                    IncrementalInput = LongValue(last, "input_tokens"),
                    CumulativeCachedInput = LongValue(total, "cached_input_tokens"),
                    IncrementalCachedInput = LongValue(last, "cached_input_tokens"),
                    CumulativeOutput = LongValue(total, "output_tokens"),
                    IncrementalOutput = LongValue(last, "output_tokens"),
                    CumulativeReasoningOutput = LongValue(total, "reasoning_output_tokens"),
                    IncrementalReasoningOutput = LongValue(last, "reasoning_output_tokens"),
                    HasCumulativeInput = HasKey(total, "input_tokens"),
                    HasIncrementalInput = HasKey(last, "input_tokens"),
                    HasCumulativeCachedInput = HasKey(total, "cached_input_tokens"),
                    HasIncrementalCachedInput = HasKey(last, "cached_input_tokens"),
                    HasCumulativeOutput = HasKey(total, "output_tokens"),
                    HasIncrementalOutput = HasKey(last, "output_tokens"),
                    HasCumulativeReasoningOutput = HasKey(total, "reasoning_output_tokens"),
                    HasIncrementalReasoningOutput = HasKey(last, "reasoning_output_tokens")
                };
                return parsed.CumulativeTotal > 0 || parsed.IncrementalTotal > 0;
            }
            catch { return false; }
        }

        private static bool HasKey(Dictionary<string, object> source, string key)
        {
            return source != null && source.ContainsKey(key);
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
