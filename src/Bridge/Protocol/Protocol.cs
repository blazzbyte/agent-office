using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace AgentOffice.Bridge
{
/// <summary>Wire protocol shared with the web add-in.</summary>
    internal static class Protocol
    {
        public const string BridgeName = "agent-office";
        public const string BridgeVersion = "1.0.0";
        public const int ProtocolVersion = 1;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 8 * 1024 * 1024,
        };

        public static string Serialize(object value)
        {
            return Json.Serialize(value);
        }

        // ------------------------------------------------------------------
        // Request parsing
        // ------------------------------------------------------------------

        public static bool TryParseRequest(string body, out BridgeRequest request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrWhiteSpace(body))
            {
                error = "Empty request body";
                return false;
            }

            Dictionary<string, object> raw;
            try
            {
                raw = Json.Deserialize<Dictionary<string, object>>(body);
            }
            catch (Exception ex)
            {
                error = "Invalid JSON: " + ex.Message;
                return false;
            }

            if (raw == null)
            {
                error = "Invalid JSON payload";
                return false;
            }

            var req = new BridgeRequest();

            object tmp;
            string str;
            if (raw.TryGetValue("id", out tmp) && (str = tmp as string) != null) req.Id = str;
            if (raw.TryGetValue("action", out tmp) && (str = tmp as string) != null) req.Action = str;
            if (raw.TryGetValue("workbookHint", out tmp) && (str = tmp as string) != null) req.WorkbookHint = str;

            Dictionary<string, object> prm;
            if (raw.TryGetValue("params", out tmp) && (prm = tmp as Dictionary<string, object>) != null)
            {
                req.Params = prm;
            }

            if (string.IsNullOrEmpty(req.Action))
            {
                error = "Missing required field 'action'";
                return false;
            }

            request = req;
            return true;
        }

        // ------------------------------------------------------------------
        // StepResult builders
        // ------------------------------------------------------------------

        public static Dictionary<string, object> Ok(string id, string summary, IDictionary<string, object> data = null)
        {
            var result = new Dictionary<string, object>
            {
                ["id"] = id ?? string.Empty,
                ["status"] = "success",
                ["summary"] = summary,
            };
            if (data != null) result["data"] = data;
            return result;
        }

        public static Dictionary<string, object> Fail(
            string id,
            string summary,
            string code = null,
            IDictionary<string, object> extra = null)
        {
            var result = new Dictionary<string, object>
            {
                ["id"] = id ?? string.Empty,
                ["status"] = "error",
                ["summary"] = summary,
            };

            var data = new Dictionary<string, object>();
            if (code != null) data["code"] = code;
            if (extra != null)
            {
                foreach (var kv in extra) data[kv.Key] = kv.Value;
            }
            if (data.Count > 0) result["data"] = data;
            return result;
        }

        // ------------------------------------------------------------------
        // Param helpers
        // ------------------------------------------------------------------

        public static string GetString(IDictionary<string, object> parameters, string key)
        {
            if (parameters == null) return null;
            object value;
            if (!parameters.TryGetValue(key, out value)) return null;
            return value as string ?? (value != null ? Convert.ToString(value) : null);
        }

/// <summary>Numeric param — JSON numbers arrive as int/double; strings are parsed (invariant first).</summary>
        public static double? GetDouble(IDictionary<string, object> parameters, string key)
        {
            if (parameters == null) return null;
            object value;
            if (!parameters.TryGetValue(key, out value) || value == null) return null;

            if (value is double) return (double)value;
            if (value is int) return (double)(int)value;
            if (value is long) return (double)(long)value;
            if (value is decimal) return (double)(decimal)value;

            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return null;
            double d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out d)) return d;
            return null;
        }

/// <summary>Boolean param — accepts real booleans and the usual string forms ("true", "1", "yes").</summary>
        public static bool GetBool(IDictionary<string, object> parameters, string key)
        {
            if (parameters == null) return false;
            object value;
            if (!parameters.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            var s = (value as string ?? Convert.ToString(value)).Trim();
            return s == "1" ||
                s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

/// <summary>String-array param — JSON arrays arrive as ArrayList; a bare string is a one-element list.</summary>
        public static List<string> GetStringList(IDictionary<string, object> parameters, string key)
        {
            if (parameters == null) return null;
            object value;
            if (!parameters.TryGetValue(key, out value) || value == null) return null;

            var single = value as string;
            if (single != null)
            {
                return string.IsNullOrWhiteSpace(single) ? null : new List<string> { single.Trim() };
            }

            var items = value as System.Collections.IEnumerable;
            if (items == null) return null;
            var result = new List<string>();
            foreach (var item in items)
            {
                var s = item as string ?? (item != null ? Convert.ToString(item) : null);
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
            return result.Count > 0 ? result : null;
        }
    }

/// <summary>Parsed /bridge/v1/execute request.</summary>
    internal sealed class BridgeRequest
    {
        public string Id;
        public string Action;
        public Dictionary<string, object> Params = new Dictionary<string, object>();
        public string WorkbookHint;
    }

/// <summary>Exception carrying a protocol error code (see plan §6).</summary>
    internal sealed class BridgeException : Exception
    {
        public string Code { get; private set; }

        public BridgeException(string code, string message)
            : base(message)
        {
            Code = code;
        }
    }
}
