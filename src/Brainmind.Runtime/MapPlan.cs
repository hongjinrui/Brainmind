using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brainmind.Runtime
{
    public sealed class MapPlanNode
    {
        public string Key { get; private set; }
        public string Name { get; private set; }
        public string ParentKey { get; private set; }
        public int Depth { get; internal set; }
        internal int Order;
        internal MapPlanNode(string key, string name, string parent, int order)
        { Key = key; Name = name; ParentKey = parent; Order = order; }
    }

    public sealed class MapPlanLink
    {
        public string AKey { get; private set; }
        public string BKey { get; private set; }
        public string Relation { get { return "jump"; } }
        internal MapPlanLink(string a, string b) { AKey = a; BKey = b; }
    }

    // Pure validation: no application assemblies, filesystem access or model calls.
    public sealed class MapPlan
    {
        public const int MaxNodes = 40;
        public const int MaxDepth = 8;
        public const int MaxExtraLinks = 60;
        public Guid ParentId { get; private set; }
        public string RootKey { get; private set; }
        public IList<MapPlanNode> Nodes { get; private set; }
        public IList<MapPlanLink> Links { get; private set; }

        public static MapPlan Parse(JsonElement args)
        {
            Dictionary<string, JsonElement> fields = Fields(args, new string[] { "parent_id", "nodes", "links" }, "create_map");
            string parent = RequiredString(fields, "parent_id", "create_map");
            Guid parentId;
            if (!Guid.TryParse(parent, out parentId) || parentId == Guid.Empty)
                Fail("parent_id 必须是当前脑图中普通节点的有效 UUID。");
            JsonElement nodesJson;
            if (!fields.TryGetValue("nodes", out nodesJson) || nodesJson.ValueKind != JsonValueKind.Array
                || nodesJson.GetArrayLength() < 1 || nodesJson.GetArrayLength() > MaxNodes)
                Fail("nodes 必须包含 1 到 " + MaxNodes + " 个新节点。");
            List<MapPlanNode> nodes = new List<MapPlanNode>();
            Dictionary<string, MapPlanNode> byKey = new Dictionary<string, MapPlanNode>(StringComparer.Ordinal);
            string root = null;
            int rootCount = 0;
            foreach (JsonElement element in nodesJson.EnumerateArray())
            {
                Dictionary<string, JsonElement> node = Fields(element, new string[] { "key", "name", "parent_key" }, "nodes");
                string key = Key(RequiredString(node, "key", "nodes"));
                string name = Name(RequiredString(node, "name", "nodes"));
                string parentKey = null;
                JsonElement parentJson;
                if (node.TryGetValue("parent_key", out parentJson) && parentJson.ValueKind != JsonValueKind.Null)
                {
                    if (parentJson.ValueKind != JsonValueKind.String) Fail("parent_key 必须是临时 key、null 或省略。");
                    parentKey = Key(parentJson.GetString());
                }
                if (byKey.ContainsKey(key)) Fail("节点临时 key 重复：" + key);
                MapPlanNode item = new MapPlanNode(key, name, parentKey, nodes.Count);
                nodes.Add(item); byKey.Add(key, item);
                if (parentKey == null) { root = key; rootCount++; }
            }
            if (rootCount != 1) Fail("必须恰好有一个未指定 parent_key 的新根节点；其他节点需连到这个新根。");
            foreach (MapPlanNode node in nodes)
            {
                if (node.ParentKey == node.Key) Fail("节点不能以自身为父节点：" + node.Key);
                if (node.ParentKey != null && !byKey.ContainsKey(node.ParentKey)) Fail("parent_key 引用了不存在的新节点：" + node.ParentKey);
            }
            Dictionary<string, int> states = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MapPlanNode node in nodes) Depth(node, byKey, states);
            nodes.Sort(delegate(MapPlanNode a, MapPlanNode b) { int depth = a.Depth.CompareTo(b.Depth); return depth != 0 ? depth : a.Order.CompareTo(b.Order); });

            HashSet<string> treeEdges = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapPlanNode node in nodes) if (node.ParentKey != null) treeEdges.Add(Pair(node.Key, node.ParentKey));
            List<MapPlanLink> links = new List<MapPlanLink>();
            HashSet<string> extraEdges = new HashSet<string>(StringComparer.Ordinal);
            JsonElement linksJson;
            if (fields.TryGetValue("links", out linksJson))
            {
                if (linksJson.ValueKind != JsonValueKind.Array || linksJson.GetArrayLength() > MaxExtraLinks)
                    Fail("links 必须是数组，最多 " + MaxExtraLinks + " 条额外跳转关系。");
                foreach (JsonElement element in linksJson.EnumerateArray())
                {
                    Dictionary<string, JsonElement> link = Fields(element, new string[] { "a_key", "b_key", "relation" }, "links");
                    string a = Key(RequiredString(link, "a_key", "links"));
                    string b = Key(RequiredString(link, "b_key", "links"));
                    if (RequiredString(link, "relation", "links") != "jump") Fail("额外关系只支持 jump。");
                    if (!byKey.ContainsKey(a) || !byKey.ContainsKey(b)) Fail("跳转关系只能引用本批新节点的临时 key。");
                    if (a == b) Fail("不能创建节点到自身的跳转。");
                    string pair = Pair(a, b);
                    if (treeEdges.Contains(pair)) Fail("跳转关系与本批父子关系冲突。");
                    if (!extraEdges.Add(pair)) Fail("跳转关系重复（包括两端互换的重复）。");
                    links.Add(new MapPlanLink(a, b));
                }
            }
            return new MapPlan { ParentId = parentId, RootKey = root, Nodes = nodes.AsReadOnly(), Links = links.AsReadOnly() };
        }

        private static int Depth(MapPlanNode node, Dictionary<string, MapPlanNode> nodes, Dictionary<string, int> states)
        {
            int state;
            if (states.TryGetValue(node.Key, out state))
            {
                if (state == 1) Fail("父子关系中存在环或无法连接到新根的节点。");
                return node.Depth;
            }
            states[node.Key] = 1;
            node.Depth = node.ParentKey == null ? 1 : Depth(nodes[node.ParentKey], nodes, states) + 1;
            if (node.Depth > MaxDepth) Fail("新分支最多 " + MaxDepth + " 层（新根计为第 1 层）。");
            states[node.Key] = 2;
            return node.Depth;
        }

        private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed, string label)
        {
            if (value.ValueKind != JsonValueKind.Object) Fail(label + " 必须是 JSON 对象。");
            HashSet<string> names = new HashSet<string>(allowed, StringComparer.Ordinal);
            Dictionary<string, JsonElement> fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Contains(property.Name)) Fail(label + " 包含不支持的字段：" + property.Name);
                if (fields.ContainsKey(property.Name)) Fail(label + " 包含重复字段：" + property.Name);
                fields.Add(property.Name, property.Value);
            }
            return fields;
        }
        private static string RequiredString(Dictionary<string, JsonElement> fields, string name, string label)
        {
            JsonElement value;
            if (!fields.TryGetValue(name, out value) || value.ValueKind != JsonValueKind.String) Fail(label + " 缺少字符串字段：" + name);
            return value.GetString();
        }
        private static string Key(string value)
        {
            if (value == null || !Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant))
                Fail("临时 key 必须是 1 到 64 个英文字母、数字、下划线或短横线。");
            return value;
        }
        private static string Name(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length < 1 || value.Length > 1024) Fail("节点名称长度必须为 1 到 1024 字符。");
            foreach (char c in value)
                if (Char.IsControl(c) || (c >= '\uf400' && c <= '\uf40f') || c == '\uf50a') Fail("节点名称不能包含换行或控制字符。");
            if (Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)
                || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                Fail("新节点名称不能是 URI、网址或文件导入路径。");
            return value;
        }
        private static string Pair(string a, string b) { return StringComparer.Ordinal.Compare(a, b) < 0 ? a + "\0" + b : b + "\0" + a; }
        private static void Fail(string message) { throw new ArgumentException(message); }
    }
}
