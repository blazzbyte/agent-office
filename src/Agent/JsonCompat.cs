using System.Collections;

namespace AgentOffice.Agent
{
/// <summary>JavaScriptSerializer array-shape compat (measured on net48): Deserialize&lt;object&gt.</summary>
    internal static class JsonCompat
    {
/// <summary>Accepts ArrayList or object[]; null for anything else.</summary>
        internal static ArrayList ToArrayList(object raw)
        {
            var list = raw as ArrayList;
            if (list != null) return list;
            var array = raw as object[];
            return array != null ? new ArrayList(array) : null;
        }
    }
}
