using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexMonitor
{
    public sealed class UsageSnapshot
    {
        public bool Available;
        public bool PrimaryAvailable;
        public bool SecondaryAvailable;
        public double PrimaryUsed;
        public double SecondaryUsed;
        public long PrimaryReset;
        public long SecondaryReset;
        public DateTime SampleUtc;
        public string Plan = "--";
        public string Status = "unavailable";
        public string Message = "Quota data is unavailable.";

        public UsageSnapshot Clone()
        {
            return (UsageSnapshot)MemberwiseClone();
        }
    }

    internal sealed class ParsedWindow
    {
        internal double Remaining;
        internal long Reset;
        internal long WindowSeconds;
    }

    internal sealed class AuthState
    {
        internal string AccessToken;
        internal string AccountId;
    }

    public static class QuotaServiceReader
    {
        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        private const int MaxAuthBytes = 256 * 1024;
        private const int MaxResponseBytes = 1024 * 1024;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes };

        public static UsageSnapshot ReadLatest()
        {
            AuthState auth;
            string authError;
            if (!TryLoadAuth(out auth, out authError)) return Failure("signed_out", authError);

            try
            {
                return ParseUsageJson(DownloadUsage(auth));
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                return response == null
                    ? Failure("unavailable", "Network unavailable. Retrying automatically.")
                    : HttpFailure((int)response.StatusCode);
            }
            catch
            {
                return Failure("unavailable", "Quota service is temporarily unavailable.");
            }
        }

        public static string ReadUsageJson()
        {
            return Serialize(ReadLatest());
        }

        public static string ReadSanitizedSchemaJson()
        {
            AuthState auth;
            string authError;
            if (!TryLoadAuth(out auth, out authError)) return Json.Serialize(new Dictionary<string, object> { { "error", authError } });
            try
            {
                object root = Json.DeserializeObject(DownloadUsage(auth));
                List<string> paths = new List<string>();
                WalkSchema(root, "$", paths, 0);
                Dictionary<string, object> output = new Dictionary<string, object>();
                output["paths"] = paths.ToArray();
                output["count"] = paths.Count;
                return Json.Serialize(output);
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                return Json.Serialize(new Dictionary<string, object> { { "error", response == null ? "network" : "http " + (int)response.StatusCode } });
            }
            catch { return Json.Serialize(new Dictionary<string, object> { { "error", "schema unavailable" } }); }
        }

        public static string RunSelfTestJson()
        {
            List<string> failures = new List<string>();
            string fixture = "{\"plan_type\":\"pro\",\"rate_limit\":{\"primary_window\":{\"used_percent\":26,\"reset_at\":1738300000,\"limit_window_seconds\":18000},\"secondary_window\":{\"remaining_ratio\":0.6,\"resetsAt\":\"2026-07-21T00:00:00Z\",\"windowSeconds\":604800}}}";
            UsageSnapshot parsed = ParseUsageJson(fixture);
            Assert(failures, parsed.Available, "fixture available");
            Assert(failures, Math.Abs((100 - parsed.PrimaryUsed) - 74) < 0.001, "used percent becomes remaining");
            Assert(failures, Math.Abs((100 - parsed.SecondaryUsed) - 60) < 0.001, "remaining ratio scaling");
            Assert(failures, parsed.Plan == "PRO", "plan normalization");
            Assert(failures, parsed.PrimaryReset == 1738300000, "epoch reset parsing");
            Assert(failures, parsed.SecondaryReset > 0, "ISO reset parsing");

            string arrays = "{\"rateLimit\":{\"windows\":[{\"name\":\"weekly\",\"remainingPercent\":88,\"windowSeconds\":604800},{\"name\":\"primary\",\"remainingPercent\":51,\"windowSeconds\":18000}]}}";
            UsageSnapshot arrayParsed = ParseUsageJson(arrays);
            Assert(failures, Math.Abs((100 - arrayParsed.PrimaryUsed) - 51) < 0.001, "array primary selection");
            Assert(failures, Math.Abs((100 - arrayParsed.SecondaryUsed) - 88) < 0.001, "array weekly selection");

            string proLite = "{\"plan_type\":\"prolite\",\"rate_limit\":{\"primary_window\":{\"used_percent\":49,\"reset_at\":1784956095,\"limit_window_seconds\":604800},\"secondary_window\":null}}";
            UsageSnapshot proLiteParsed = ParseUsageJson(proLite);
            Assert(failures, proLiteParsed.Available, "weekly-only fixture available");
            Assert(failures, !proLiteParsed.PrimaryAvailable, "weekly window is never mislabeled as 5-hour");
            Assert(failures, proLiteParsed.SecondaryAvailable, "weekly window selected by duration despite primary name");
            Assert(failures, Math.Abs((100 - proLiteParsed.SecondaryUsed) - 51) < 0.001, "weekly-only remaining percent");
            int historyTests = TokenHistoryReader.AppendSelfTests(failures);
            int uiTests = UiSelfTest.AppendSelfTests(failures);

            Dictionary<string, object> output = new Dictionary<string, object>();
            output["passed"] = failures.Count == 0;
            output["failures"] = failures.ToArray();
            output["tests"] = 13 + historyTests + uiTests;
            return Json.Serialize(output);
        }

        internal static UsageSnapshot ParseUsageJson(string raw)
        {
            Dictionary<string, object> root;
            try { root = AsDictionary(Json.DeserializeObject(raw)); }
            catch { return Failure("unavailable", "Quota response format has changed."); }
            if (root == null) return Failure("unavailable", "Quota response format has changed.");

            Dictionary<string, object> rateLimit = DictionaryValue(root, new[] { "rate_limit", "rateLimit" }) ?? root;
            ParsedWindow primary = ParseWindow(FindWindow(rateLimit, new[] {
                "primary_window", "primaryWindow", "short_window", "shortWindow",
                "five_hour_window", "fiveHourWindow", "5h", "primary"
            }, 18000));
            ParsedWindow weekly = ParseWindow(FindWindow(rateLimit, new[] {
                "secondary_window", "secondaryWindow", "weekly_window", "weeklyWindow",
                "week_window", "weekWindow", "weekly", "secondary"
            }, 604800));

            if (primary == null && weekly == null)
                return Failure("unavailable", "Quota response does not contain a recognized quota window.");

            UsageSnapshot snapshot = new UsageSnapshot();
            snapshot.Available = true;
            snapshot.PrimaryAvailable = primary != null;
            snapshot.SecondaryAvailable = weekly != null;
            snapshot.PrimaryUsed = primary == null ? 0 : Clamp(100 - primary.Remaining);
            snapshot.SecondaryUsed = weekly == null ? 0 : Clamp(100 - weekly.Remaining);
            snapshot.PrimaryReset = primary == null ? 0 : primary.Reset;
            snapshot.SecondaryReset = weekly == null ? 0 : weekly.Reset;
            snapshot.SampleUtc = DateTime.UtcNow;
            snapshot.Plan = (StringValue(root, new[] { "plan_type", "planType" }) ?? "--").ToUpperInvariant();
            snapshot.Status = "ok";
            snapshot.Message = null;
            return snapshot;
        }

        private static bool TryLoadAuth(out AuthState auth, out string error)
        {
            auth = null;
            error = null;
            try
            {
                string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(codexHome))
                    codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string path = Path.Combine(codexHome, "auth.json");
                FileInfo info = new FileInfo(path);
                if (!info.Exists) { error = "Please sign in to Codex Desktop first."; return false; }
                if (info.Length <= 0 || info.Length > MaxAuthBytes) { error = "Codex login data is unavailable."; return false; }
                Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) { error = "Codex login format has changed."; return false; }
                Dictionary<string, object> tokens = DictionaryValue(root, new[] { "tokens" }) ?? root;
                string accessToken = StringValue(tokens, new[] { "access_token", "accessToken" });
                if (String.IsNullOrWhiteSpace(accessToken)) { error = "Codex login expired. Please sign in again."; return false; }
                string accountId = StringValue(tokens, new[] { "account_id", "accountId" }) ?? AccountIdFromJwt(accessToken);
                auth = new AuthState { AccessToken = accessToken, AccountId = accountId };
                return true;
            }
            catch
            {
                error = "Codex login format has changed.";
                return false;
            }
        }

        private static string AccountIdFromJwt(string token)
        {
            try
            {
                string[] parts = token.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                while (payload.Length % 4 != 0) payload += "=";
                Dictionary<string, object> values = AsDictionary(Json.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(payload))));
                return values == null ? null : StringValue(values, new[] {
                    "https://api.openai.com/auth.chatgpt_account_id", "chatgpt_account_id"
                });
            }
            catch { return null; }
        }

        private static string ReadLimited(HttpWebResponse response)
        {
            if (response.ContentLength > MaxResponseBytes) throw new InvalidDataException();
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + read > MaxResponseBytes) throw new InvalidDataException();
                    memory.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        private static string DownloadUsage(AuthState auth)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(UsageUrl);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "CodexQuotaOrb/0.2";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + auth.AccessToken;
            request.Headers["originator"] = "Codex Desktop";
            request.Headers["OAI-Product-Sku"] = "CODEX";
            if (!String.IsNullOrWhiteSpace(auth.AccountId)) request.Headers["ChatGPT-Account-Id"] = auth.AccountId;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) throw new WebException("quota http status", null, WebExceptionStatus.ProtocolError, response);
                return ReadLimited(response);
            }
        }

        private static void WalkSchema(object value, string path, List<string> output, int depth)
        {
            if (output.Count >= 240 || depth > 8) return;
            Dictionary<string, object> dictionary = AsDictionary(value);
            if (dictionary != null)
            {
                foreach (KeyValuePair<string, object> item in dictionary)
                {
                    string next = path + "." + item.Key;
                    if (item.Value == null) output.Add(next + "=null");
                    else if (AsDictionary(item.Value) != null || (item.Value is IEnumerable && !(item.Value is string)))
                    { output.Add(next + "=<" + item.Value.GetType().Name + ">"); WalkSchema(item.Value, next, output, depth + 1); }
                    else output.Add(next + "=" + SanitizedValue(item.Key, item.Value));
                    if (output.Count >= 240) return;
                }
                return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null && !(value is string))
            {
                int index = 0;
                foreach (object item in list)
                {
                    WalkSchema(item, path + "[" + index + "]", output, depth + 1);
                    index++;
                    if (output.Count >= 240) return;
                }
            }
        }

        private static string SanitizedValue(string key, object value)
        {
            string lower = key.ToLowerInvariant();
            bool safe = lower.Contains("plan") || lower.Contains("limit") || lower.Contains("window") || lower.Contains("reset")
                || lower.Contains("remaining") || lower.Contains("used") || lower.Contains("percent") || lower.Contains("seconds")
                || lower == "name" || lower == "type" || lower == "id";
            if (!safe) return "<" + value.GetType().Name + ">";
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return text.Length > 80 ? text.Substring(0, 80) : text;
        }

        private static UsageSnapshot HttpFailure(int status)
        {
            if (status == 401 || status == 403) return Failure("signed_out", "Codex login expired. Please sign in again.");
            if (status == 429) return Failure("unavailable", "Quota service is rate limited. Retrying automatically.");
            return Failure("unavailable", "Quota service is temporarily unavailable.");
        }

        private static UsageSnapshot Failure(string status, string message)
        {
            return new UsageSnapshot { Status = status, Message = message, SampleUtc = DateTime.UtcNow };
        }

        private static ParsedWindow ParseWindow(Dictionary<string, object> values)
        {
            if (values == null) return null;
            double amount;
            string key;
            double remaining;
            if (NumberWithKey(values, new[] {
                "remaining_percent", "remainingPercent", "remaining_pct", "remainingPct",
                "remaining_ratio", "remainingRatio", "remaining"
            }, out key, out amount))
            {
                remaining = ScaleRatio(key, amount) ? amount * 100 : amount;
            }
            else if (NumberWithKey(values, new[] {
                "used_percent", "usedPercent", "used_pct", "usedPct",
                "used_ratio", "usedRatio", "utilization", "used"
            }, out key, out amount))
            {
                double used = ScaleRatio(key, amount) ? amount * 100 : amount;
                remaining = 100 - used;
            }
            else return null;

            return new ParsedWindow {
                Remaining = Clamp(remaining),
                Reset = Timestamp(values, new[] { "reset_at", "resetAt", "resets_at", "resetsAt", "reset_time", "resetTime" }),
                WindowSeconds = Integer(values, new[] {
                    "limit_window_seconds", "limitWindowSeconds", "window_seconds", "windowSeconds",
                    "duration_seconds", "durationSeconds", "period_seconds", "periodSeconds"
                })
            };
        }

        private static Dictionary<string, object> FindWindow(Dictionary<string, object> rateLimit, string[] names, long expectedSeconds)
        {
            if (rateLimit == null) return null;

            // Duration is authoritative. Some plans currently place a seven-day quota
            // in a field named primary_window, so choosing by field name first would
            // incorrectly report it as the five-hour quota.
            foreach (KeyValuePair<string, object> pair in rateLimit)
            {
                Dictionary<string, object> direct = AsDictionary(pair.Value);
                ParsedWindow parsed = ParseWindow(direct);
                if (parsed != null && expectedSeconds > 0 && Math.Abs(parsed.WindowSeconds - expectedSeconds) <= 60)
                    return direct;
            }

            foreach (string collectionName in new[] { "windows", "limit_windows", "limitWindows", "limits", "buckets" })
            {
                object raw;
                if (!rateLimit.TryGetValue(collectionName, out raw)) continue;
                IEnumerable items = raw as IEnumerable;
                if (items == null || raw is string) continue;
                foreach (object item in items)
                {
                    Dictionary<string, object> candidate = AsDictionary(item);
                    ParsedWindow window = ParseWindow(candidate);
                    if (window == null) continue;
                    bool durationMatch = expectedSeconds > 0 && Math.Abs(window.WindowSeconds - expectedSeconds) <= 60;
                    if (durationMatch) return candidate;
                }
            }

            // Conservative fallback for older responses that omit duration entirely.
            // A known, conflicting duration is never overridden by a semantic name.
            foreach (string name in names)
            {
                object raw;
                if (!rateLimit.TryGetValue(name, out raw)) continue;
                Dictionary<string, object> direct = AsDictionary(raw);
                ParsedWindow parsed = ParseWindow(direct);
                if (parsed != null && parsed.WindowSeconds <= 0) return direct;
            }

            foreach (string collectionName in new[] { "windows", "limit_windows", "limitWindows", "limits", "buckets" })
            {
                object raw;
                if (!rateLimit.TryGetValue(collectionName, out raw)) continue;
                IEnumerable items = raw as IEnumerable;
                if (items == null || raw is string) continue;
                foreach (object item in items)
                {
                    Dictionary<string, object> candidate = AsDictionary(item);
                    ParsedWindow window = ParseWindow(candidate);
                    if (window == null || window.WindowSeconds > 0) continue;
                    string itemName = StringValue(candidate, new[] { "name", "type", "id", "window", "label" });
                    if (MatchesAnyName(itemName, names)) return candidate;
                }
            }
            return null;
        }

        private static bool MatchesAnyName(string value, string[] names)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            foreach (string name in names)
                if (value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool NumberWithKey(Dictionary<string, object> values, string[] keys, out string key, out double result)
        {
            foreach (string candidate in keys)
            {
                object raw;
                if (!values.TryGetValue(candidate, out raw) || raw == null) continue;
                if (Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                { key = candidate; return true; }
            }
            key = null; result = 0; return false;
        }

        private static bool ScaleRatio(string key, double value)
        {
            return key.IndexOf("ratio", StringComparison.OrdinalIgnoreCase) >= 0
                || key.Equals("utilization", StringComparison.OrdinalIgnoreCase)
                || (key.IndexOf("percent", StringComparison.OrdinalIgnoreCase) < 0
                    && key.IndexOf("pct", StringComparison.OrdinalIgnoreCase) < 0 && value <= 1.0);
        }

        private static long Timestamp(Dictionary<string, object> values, string[] keys)
        {
            foreach (string key in keys)
            {
                object raw;
                if (!values.TryGetValue(key, out raw) || raw == null) continue;
                long seconds;
                if (Int64.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out seconds)) return seconds;
                DateTime parsed;
                if (DateTime.TryParse(Convert.ToString(raw), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                    return (long)(parsed - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            }
            return 0;
        }

        private static long Integer(Dictionary<string, object> values, string[] keys)
        {
            foreach (string key in keys)
            {
                object raw;
                long result;
                if (values.TryGetValue(key, out raw) && raw != null && Int64.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out result)) return result;
            }
            return 0;
        }

        private static Dictionary<string, object> DictionaryValue(Dictionary<string, object> values, string[] keys)
        {
            foreach (string key in keys)
            {
                object raw;
                if (values.TryGetValue(key, out raw))
                {
                    Dictionary<string, object> result = AsDictionary(raw);
                    if (result != null) return result;
                }
            }
            return null;
        }

        private static string StringValue(Dictionary<string, object> values, string[] keys)
        {
            if (values == null) return null;
            foreach (string key in keys)
            {
                object raw;
                if (values.TryGetValue(key, out raw) && raw != null)
                {
                    string result = Convert.ToString(raw);
                    if (!String.IsNullOrWhiteSpace(result)) return result;
                }
            }
            return null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static double Clamp(double value) { return Math.Max(0, Math.Min(100, value)); }

        private static string Serialize(UsageSnapshot snapshot)
        {
            Dictionary<string, object> output = new Dictionary<string, object>();
            output["available"] = snapshot.Available;
            output["plan"] = snapshot.Plan;
            output["primary_remaining_percent"] = snapshot.PrimaryAvailable ? 100 - snapshot.PrimaryUsed : (object)null;
            output["secondary_remaining_percent"] = snapshot.SecondaryAvailable ? 100 - snapshot.SecondaryUsed : (object)null;
            output["primary_resets_at"] = snapshot.PrimaryReset;
            output["secondary_resets_at"] = snapshot.SecondaryReset;
            output["sample_utc"] = snapshot.SampleUtc.ToString("o");
            output["status"] = snapshot.Status;
            output["message"] = snapshot.Message;
            return Json.Serialize(output);
        }

        private static void Assert(List<string> failures, bool condition, string name)
        {
            if (!condition) failures.Add(name);
        }
    }
}
