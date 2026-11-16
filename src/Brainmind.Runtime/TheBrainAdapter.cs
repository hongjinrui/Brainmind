using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Brainmind.Runtime
{
    /// <summary>
    /// Version-pinned native adapter. All entry points run synchronously on the Xwt UI thread.
    /// It never opens a database, sets entity properties, saves an editor, or changes selection
    /// to another thought. The host owns UI dispatch and the lifetime of a model request.
    /// </summary>
    public static class TheBrainAdapter
    {
        private const string SupportedVersion = "14.0.102.0";
        private const string WindowType = "TheBrain.__x_xxxx____xx_xxx_xx._x___x__x_xxx_xxx__x__xx";
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private static readonly object EpochLock = new object();
        private static object lastWindow, lastOrganizer, lastContext, lastEditor;
        private static long bindingEpoch;
        private static readonly Dictionary<Guid, long> nativeRevisions = new Dictionary<Guid, long>();
        private static long unknownNativeRevision;
        private static Delegate nativeChangeHandler;

        private sealed class Binding
        {
            public Assembly Desktop;
            public object Window, Organizer, Context, Active, Editor, Document, Keza, Selection;
            public Guid BrainId, ThoughtId, RegionId;
            public string BrainName, ThoughtName, Text, NoteReason;
            public bool NoteAvailable, Dirty;
            public long Epoch, NativeRevision, UnknownNativeRevision;
            public Dictionary<string, object> Snapshot;
        }

        private sealed class AdapterException : Exception
        {
            public readonly string Code;
            public Dictionary<string,object> PartialResult;
            public AdapterException(string code, string message) : base(message) { Code = code; }
        }

        /// <summary>Returns current tab/brain/thought, in-memory unsaved note, adjacency, and an optimistic token.</summary>
        public static Dictionary<string, object> GetContext()
        {
            return Capture().Snapshot;
        }

        /// <summary>Reads a UUID through the current brain's native model. It never activates the thought.</summary>
        public static Dictionary<string, object> ReadThought(string id)
        {
            Binding binding = Capture();
            RequireSession(binding);
            Guid thoughtId = ParseId(id, "id");
            object thought = ReadNativeThought(binding, thoughtId, false);
            Dictionary<string, object> value = ThoughtValue(thought, binding.BrainId);
            ReadGraph(binding, thoughtId, value);
            if (thoughtId == binding.ThoughtId) value["note"] = binding.Snapshot["note"];
            value["context_token"] = binding.Snapshot["context_token"];
            EnsureSameBinding(binding);
            return value;
        }

        /// <summary>
        /// Makes a complete, read-only plan for all live ordinary thoughts outside the active
        /// thought's child closure. The native full-brain streams also include disconnected
        /// branches; visible adjacency is never treated as the whole brain.
        /// </summary>
        public static Dictionary<string, object> GetDeletionPlan(string preserveId)
        {
            return GetDeletionPlan(preserveId, false);
        }

        public static Dictionary<string, object> GetDeletionPlan(string preserveId, bool keepHome)
        {
            Binding binding = null;
            List<Dictionary<string, object>> diagnostics = new List<Dictionary<string, object>>();
            SortedDictionary<string, int> diagnosticReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
            SortedDictionary<string, int> ignoredSemanticKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int scannedThoughts = 0, scannedLinks = 0, diagnosticTotal = 0;
            try
            {
                binding = Capture();
                RequireSession(binding);
                Guid preserve = ParseId(preserveId, "preserve_id");
                if (preserve != binding.ThoughtId)
                    throw new AdapterException("validation_error", "保留子树的根必须是当前活动节点，请先激活要保留的节点。");
                RequireNormal(ReadNativeThought(binding, preserve, false));
                EnsureSameBinding(binding);
                object logic = ProtectedProperty(binding.Context, "__xxxxxxx____x___xxx");
                Guid home = ReadNativeHome(binding, logic);
                Dictionary<Guid, Dictionary<string, object>> thoughts = new Dictionary<Guid, Dictionary<string, object>>();
                Dictionary<Guid, int> liveKinds = new Dictionary<Guid, int>();
                HashSet<Guid> allIds = new HashSet<Guid>(), deletedIds = new HashSet<Guid>();
                SortedDictionary<int, int> excludedKinds = new SortedDictionary<int, int>();
                Dictionary<Guid, List<Guid>> children = new Dictionary<Guid, List<Guid>>();
                int excludedCount = 0;
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    cancellation.CancelAfter(15000);
                    StreamNativeEntities(logic, "__xxxx___x__x__xx_xx_xxx", binding.BrainId, cancellation, delegate(object thought)
                    {
                        if (++scannedThoughts > 20000)
                            throw new AdapterException("deletion_plan_limit", "脑图实体数量超过只读删除规划上限；未返回部分计划，也未执行删除。");
                        if (GuidValue(Property(thought, "BrainId")) != binding.BrainId)
                            throw new AdapterException("native_error", "全图读取返回了其他脑图的节点；未生成删除计划。");
                        Guid id = GuidValue(Property(thought, "Id"));
                        if (id == Guid.Empty || !allIds.Add(id))
                            throw new AdapterException("native_error", "全图读取返回了空或重复节点 UUID；未生成删除计划。");
                        if (OptionalProperty(thought, "DeletionDateTime") != null) { deletedIds.Add(id); return; }
                        int kind = Convert.ToInt32(Property(thought, "Kind"), CultureInfo.InvariantCulture);
                        liveKinds.Add(id, kind);
                        if (kind != 1)
                        {
                            int count;
                            excludedKinds.TryGetValue(kind, out count);
                            excludedKinds[kind] = count + 1;
                            excludedCount++;
                            return;
                        }
                        if (thoughts.Count >= 5000)
                            throw new AdapterException("deletion_plan_limit", "当前脑图超过 5000 个普通节点；未返回部分计划，也未执行删除。");
                        thoughts.Add(id, Map("id", id.ToString(), "name", StringValue(Property(thought, "Name"))));
                    });
                    if (!thoughts.ContainsKey(preserve))
                        throw new AdapterException("native_error", "全图读取未包含当前保留节点；未生成删除计划。");
                    if (keepHome && !thoughts.ContainsKey(home))
                        throw new AdapterException("invalid_home", "要求保留的 Home 不存在、已删除或不是普通节点；未生成删除计划。");
                    StreamNativeEntities(logic, "_xxx__xx__x__xxxxx__x", binding.BrainId, cancellation, delegate(object link)
                    {
                        if (++scannedLinks > 100000)
                            throw new AdapterException("deletion_plan_limit", "脑图连线数量超过只读删除规划上限；未返回部分计划，也未执行删除。");
                        if (GuidValue(Property(link, "BrainId")) != binding.BrainId)
                            throw new AdapterException("native_error", "全图读取返回了其他脑图的连线；未生成删除计划。");
                        int kind = Convert.ToInt32(Property(link, "Kind"), CultureInfo.InvariantCulture);
                        int meaning = Convert.ToInt32(Property(link, "Meaning"), CultureInfo.InvariantCulture);
                        int relation = Convert.ToInt32(Property(link, "Relation"), CultureInfo.InvariantCulture);
                        Guid a = GuidValue(Property(link, "ThoughtIdA")), b = GuidValue(Property(link, "ThoughtIdB"));
                        string ignored = DeletionLinkExclusion(kind, meaning, relation, OptionalProperty(link, "DeletionDateTime") != null);
                        if (ignored != null)
                        {
                            if (kind != 1 || meaning != 1)
                            {
                                string semantic = "kind_" + kind.ToString(CultureInfo.InvariantCulture) + "_meaning_" + meaning.ToString(CultureInfo.InvariantCulture);
                                int semanticCount;
                                ignoredSemanticKinds.TryGetValue(semantic, out semanticCount);
                                ignoredSemanticKinds[semantic] = semanticCount + 1;
                            }
                            diagnosticTotal++;
                            RecordDeletionLinkDiagnostic(diagnostics, diagnosticReasons, ignored, link, a, b, kind, meaning, relation, liveKinds, deletedIds);
                            return;
                        }
                        // Relation alone is insufficient: native type/tag assignments also
                        // have parent/child relations. Only ordinary link Kind=1/Meaning=1
                        // forms the thought hierarchy. Anomalous hierarchy endpoints are
                        // retained as traversal bridges, so their reachable ordinary children
                        // cannot accidentally become deletion candidates.
                        if (!thoughts.ContainsKey(a) || !thoughts.ContainsKey(b))
                        {
                            diagnosticTotal++;
                            RecordDeletionLinkDiagnostic(diagnostics, diagnosticReasons, "anomalous_structural_endpoint", link, a, b, kind, meaning, relation, liveKinds, deletedIds);
                        }
                        Guid parent = relation == 1 ? a : b, child = relation == 1 ? b : a;
                        List<Guid> list;
                        if (!children.TryGetValue(parent, out list)) children[parent] = list = new List<Guid>();
                        list.Add(child);
                    });
                    if (cancellation.IsCancellationRequested)
                        throw new AdapterException("deletion_plan_limit", "全图读取超时；未返回部分计划，也未执行删除。");
                }
                Dictionary<string, object> selection = SelectDeletionThoughts(thoughts, children, preserve, home, keepHome);
                List<Dictionary<string, object>> excluded = new List<Dictionary<string, object>>();
                foreach (KeyValuePair<int, int> kind in excludedKinds) excluded.Add(Map("kind", kind.Key, "count", kind.Value));
                EnsureSameBinding(binding);
                Binding current = Capture();
                if (!String.Equals((string)binding.Snapshot["context_token"], (string)current.Snapshot["context_token"], StringComparison.Ordinal))
                    throw new AdapterException("context_changed", "读取全图期间脑图、活动节点或笔记已变化；未返回部分计划，请核对后重新请求。");
                selection["success"] = true;
                selection["scope"] = "except_subtree";
                selection["preserve_id"] = preserve.ToString();
                selection["complete"] = true;
                selection["excluded_thought_kinds"] = excluded;
                selection["excluded_count"] = excludedCount;
                selection["soft_deleted_thought_count"] = deletedIds.Count;
                selection["scanned_thought_count"] = scannedThoughts;
                selection["scanned_link_count"] = scannedLinks;
                selection["link_diagnostic_total"] = diagnosticTotal;
                selection["link_diagnostics"] = diagnostics;
                selection["link_diagnostics_truncated"] = diagnosticTotal > diagnostics.Count;
                selection["link_diagnostic_counts"] = diagnosticReasons;
                selection["ignored_semantic_link_counts"] = ignoredSemanticKinds;
                selection["scope_note"] = "only live normal thoughts; type/tag/system assignments excluded; anomalous structural paths conservatively protected; diagnostic samples limited to 100 without truncating the plan";
                selection["context"] = current.Snapshot;
                return selection;
            }
            catch (Exception error)
            {
                error = Unwrap(error);
                AdapterException refusal = error as AdapterException;
                Dictionary<string, object> result = Map("success", false, "scope", "except_subtree", "complete", false,
                    "code", refusal == null ? "native_error" : refusal.Code, "error", error.Message,
                    "scanned_thought_count", scannedThoughts, "scanned_link_count", scannedLinks,
                    "link_diagnostic_total", diagnosticTotal, "link_diagnostics", diagnostics,
                    "link_diagnostics_truncated", diagnosticTotal > diagnostics.Count, "link_diagnostic_counts", diagnosticReasons,
                    "ignored_semantic_link_counts", ignoredSemanticKinds);
                try { result["context"] = Capture().Snapshot; }
                catch { result["context"] = binding == null ? null : binding.Snapshot; }
                return result;
            }
        }

        private static string DeletionLinkExclusion(int kind, int meaning, int relation, bool deleted)
        {
            return deleted ? "soft_deleted_link" : kind != 1 ? "nonstructural_link_kind"
                : meaning != 1 ? "nonstructural_link_meaning" : relation == 3 ? "jump_link"
                : relation != 1 && relation != 2 ? "nonhierarchical_relation" : null;
        }

        private static void RecordDeletionLinkDiagnostic(List<Dictionary<string, object>> samples, SortedDictionary<string, int> counts,
            string reason, object link, Guid a, Guid b, int kind, int meaning, int relation, Dictionary<Guid, int> liveKinds, HashSet<Guid> deletedIds)
        {
            int count;
            counts.TryGetValue(reason, out count);
            counts[reason] = count + 1;
            int replacement = -1;
            if (samples.Count >= 100)
            {
                // Prefer anomalous structural edges over routine type/tag samples, so
                // a large set of assignments cannot hide the useful diagnostic IDs.
                if (reason == "anomalous_structural_endpoint")
                    for (int i = samples.Count - 1; i >= 0; i--)
                        if (!String.Equals((string)samples[i]["reason"], reason, StringComparison.Ordinal)) { replacement = i; break; }
                if (replacement < 0) return;
            }
            int kindA, kindB;
            Dictionary<string, object> sample = Map("id", GuidValue(Property(link, "Id")).ToString(), "a", a.ToString(), "b", b.ToString(),
                "kind", kind, "meaning", meaning, "relation", relation, "reason", reason,
                "a_state", liveKinds.TryGetValue(a, out kindA) ? "live_kind_" + kindA.ToString(CultureInfo.InvariantCulture) : deletedIds.Contains(a) ? "soft_deleted" : "missing",
                "b_state", liveKinds.TryGetValue(b, out kindB) ? "live_kind_" + kindB.ToString(CultureInfo.InvariantCulture) : deletedIds.Contains(b) ? "soft_deleted" : "missing");
            if (replacement >= 0) samples[replacement] = sample;
            else samples.Add(sample);
        }

        private static Dictionary<string, object> SelectDeletionThoughts(Dictionary<Guid, Dictionary<string, object>> thoughts,
            Dictionary<Guid, List<Guid>> children, Guid preserve, Guid home, bool keepHome)
        {
            HashSet<Guid> closure = DeletionDescendants(children, preserve, null);
            HashSet<Guid> ordinaryClosure = DeletionDescendants(children, preserve, new HashSet<Guid>(thoughts.Keys));
            bool extraHome = keepHome && !closure.Contains(home);
            List<Dictionary<string, object>> preserved = new List<Dictionary<string, object>>(), candidates = new List<Dictionary<string, object>>(),
                conservative = new List<Dictionary<string, object>>();
            int subtreeCount = 0;
            foreach (KeyValuePair<Guid, Dictionary<string, object>> thought in thoughts)
            {
                bool inSubtree = closure.Contains(thought.Key);
                if (inSubtree) subtreeCount++;
                if (inSubtree && !ordinaryClosure.Contains(thought.Key)) conservative.Add(thought.Value);
                (inSubtree || (keepHome && thought.Key == home) ? preserved : candidates).Add(thought.Value);
            }
            Comparison<Dictionary<string, object>> byId = delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                { return StringComparer.Ordinal.Compare((string)a["id"], (string)b["id"]); };
            preserved.Sort(byId); candidates.Sort(byId); conservative.Sort(byId);
            return Map("preserved_thoughts", preserved, "delete_candidates", candidates, "preserved_count", preserved.Count,
                "candidate_count", candidates.Count, "subtree_preserved_count", subtreeCount, "extra_preserved_count", extraHome ? 1 : 0,
                "keep_home", keepHome, "home_id", home.ToString(), "home_name", thoughts.ContainsKey(home) ? thoughts[home]["name"] : null,
                "moves_home", thoughts.ContainsKey(home) && !closure.Contains(home) && !keepHome,
                "conservative_preserved_count", conservative.Count, "conservative_preserved_thoughts", conservative,
                "conservative_preservation_warning", conservative.Count > 0 ? "存在特殊、软删除或缺失节点组成的普通结构桥接路径；已保守保留其可达普通后代，未把这些节点列为删除候选。" : null);
        }

        private static HashSet<Guid> DeletionDescendants(Dictionary<Guid, List<Guid>> children, Guid root, HashSet<Guid> allowed)
        {
            HashSet<Guid> found = new HashSet<Guid>();
            Queue<Guid> pending = new Queue<Guid>();
            found.Add(root); pending.Enqueue(root);
            while (pending.Count > 0)
            {
                List<Guid> list;
                if (!children.TryGetValue(pending.Dequeue(), out list)) continue;
                foreach (Guid child in list)
                    if ((allowed == null || allowed.Contains(child)) && found.Add(child)) pending.Enqueue(child);
            }
            return found;
        }

        private static void StreamNativeEntities(object logic, string name, Guid brain, CancellationTokenSource cancellation, Action<object> collect)
        {
            MethodInfo stream = Method(logic, name, 4);
            ParameterInfo[] parameters = stream.GetParameters();
            Type action = parameters[2].ParameterType;
            if (stream.ReturnType != typeof(void) || parameters[0].ParameterType != typeof(Guid)
                || parameters[1].ParameterType != typeof(Guid) || !action.IsGenericType
                || action.GetGenericTypeDefinition() != typeof(Action<>)
                || parameters[3].ParameterType != typeof(CancellationTokenSource))
                throw new AdapterException("native_error", "当前版本的全图读取接口签名不匹配，未生成删除计划。");
            MethodInfo bridge = typeof(TheBrainAdapter).GetMethod("CreateCollector", BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(action.GetGenericArguments()[0]);
            object callback = bridge.Invoke(null, new object[] { collect });
            // This build's synchronous native stream paginates by Id in groups of 1000.
            // Guid.Empty starts at the beginning, including disconnected components.
            Invoke(stream, logic, new object[] { brain, Guid.Empty, callback, cancellation });
            if (cancellation.IsCancellationRequested)
                throw new AdapterException("deletion_plan_limit", "全图读取超时；未返回部分计划，也未执行删除。");
        }

        private static Action<T> CreateCollector<T>(Action<object> collect)
        {
            return delegate(T item) { collect(item); };
        }

        // Host-only final navigation: the caller must prove this UUID is a new
        // root returned by its completed create_map request. Never expose as a model tool.
        internal static Dictionary<string, object> FocusCreatedThought(string id, string expectedContextToken)
        {
            Binding binding = Capture();
            RequireSession(binding);
            if (String.IsNullOrEmpty(expectedContextToken)
                || !String.Equals((string)binding.Snapshot["context_token"], expectedContextToken, StringComparison.Ordinal))
                throw new AdapterException("context_changed", "当前脑图或笔记已变化，未自动切换到新分支。");
            if (!binding.NoteAvailable)
                throw new AdapterException("note_unavailable", "当前笔记尚未绑定，未自动切换到新分支。");
            Guid thought = ParseId(id, "id");
            RequireNormal(ReadNativeThought(binding, thought, false));
            EnsureSameBinding(binding);
            if (thought != binding.ThoughtId)
                Call(binding.Context, "___xxxx__x_xx_x_xxxx_x", new object[] { thought, true, null });
            return Capture().Snapshot;
        }

        /// <summary>
        /// Limited writes: create_thought(parent_id,name), rename_thought(id,name),
        /// add_link(a,b,relation=parent|jump), replace_note(text,expected_text optional).
        /// Rejected requests return success=false and a stable machine-readable code.
        /// A context_changed result must end the model's write turn; do not refresh and retry it.
        /// </summary>
        public static Dictionary<string, object> Execute(string operation, JsonElement args, string expectedContextToken)
        {
            // Thread/version failures are programming or deployment errors, not tool-level refusals.
            RequireThreadAndVersion();
            Binding binding = null;
            bool mutationAttempted = false;
            try
            {
                binding = Capture();
                RequireSession(binding);
                string token = (string)binding.Snapshot["context_token"];
                if (string.IsNullOrEmpty(expectedContextToken) || !string.Equals(token, expectedContextToken, StringComparison.Ordinal))
                    throw new AdapterException("context_changed", "当前脑图、活动节点、邻接关系或笔记已变化。已停止本次写入，请核对后重新发送指令。");
                if (!binding.NoteAvailable)
                    throw new AdapterException("note_unavailable", "当前节点的笔记编辑器尚未绑定完成，未执行写入。");
                if (args.ValueKind != JsonValueKind.Object)
                    throw new AdapterException("validation_error", "操作参数必须是 JSON 对象。");
                RequireNormal(ReadNativeThought(binding, binding.ThoughtId, false));
                EnsureSameBinding(binding);

                Dictionary<string, object> result;
                if (operation == "create_thought")
                    result = CreateThought(binding, args, ref mutationAttempted);
                else if (operation == "rename_thought")
                    result = RenameThought(binding, args, ref mutationAttempted);
                else if (operation == "add_link")
                    result = AddLink(binding, args, ref mutationAttempted);
                else if (operation == "replace_note")
                    result = ReplaceNote(binding, args, ref mutationAttempted);
                else if (operation == "delete_thoughts")
                    result = DeleteThoughts(binding, args, ref mutationAttempted);
                else
                    throw new AdapterException("unsupported_operation", "不支持的操作：" + operation);

                Dictionary<string, object> response = Map("success", true, "operation", operation, "result", result,
                    "context", Capture().Snapshot);
                response["mutation_attempted"] = mutationAttempted;
                return response;
            }
            catch (Exception error)
            {
                error = Unwrap(error);
                AdapterException refusal = error as AdapterException;
                Dictionary<string, object> response = Map("success", false, "operation", operation,
                    "code", refusal == null ? "native_error" : refusal.Code, "error", error.Message,
                    "mutation_attempted", mutationAttempted);
                if(refusal != null && refusal.PartialResult != null) response["result"] = refusal.PartialResult;
                else if(operation == "delete_thoughts") response["result"] = Map("deleted_count",0,"deleted_ids",new List<string>(),
                    "deleted_links",0,"deleted_link_ids",new List<string>(),"unverified_mutation",mutationAttempted,
                    "native_trash",true,"no_cascade",true);
                // A native operation can fail after committing. Always expose this fact and a fresh
                // readback; the controller must not blindly retry a failed mutation.
                try { response["context"] = Capture().Snapshot; }
                catch { response["context"] = binding == null ? null : binding.Snapshot; }
                return response;
            }
        }

        private static Dictionary<string, object> CreateThought(Binding b, JsonElement args, ref bool attempted)
        {
            Guid parent = IdArgument(args, "parent_id");
            string name = NameArgument(args);
            // The native UI entry point imports URLs and file paths. This extension deliberately
            // accepts names only, so that a model tool cannot trigger attachment/import workflows.
            if (NativeWouldImport(name) || Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9+.-]*:") || name.StartsWith("\\\\", StringComparison.Ordinal)
                || name.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                throw new AdapterException("validation_error", "创建名称不能是网址、文件路径或 URI。请使用普通节点名称。");
            RequireNormal(ReadNativeThought(b, parent, false));
            MethodInfo method = Method(b.Context, "_xx_____x_x_x_xx_xxxx__", 8);
            ParameterInfo[] parameters = method.GetParameters();
            object[] parametersValue = new object[] { parent, Enum.ToObject(parameters[1].ParameterType, 1), name,
                Enum.ToObject(parameters[3].ParameterType, 1), null, false, false, false };
            EnsureSameBinding(b);
            attempted = true;
            object nativeResult = Invoke(method, b.Context, parametersValue);
            CheckNativeResult(nativeResult);
            IList thoughts = Property(nativeResult, "Thoughts") as IList;
            if (thoughts == null || thoughts.Count != 1)
                throw new AdapterException("readback_mismatch", "原生创建返回的节点数量不是 1；请检查当前脑图，不要自动重试。");
            Guid createdId = GuidValue(Property(thoughts[0], "Id"));
            object created = ReadNativeThought(b, createdId, true);
            RequireNormal(created);
            RefreshCurrentGraph(b);
            Dictionary<string, object> value = ThoughtValue(created, b.BrainId);
            value["linked_parent_id"] = parent.ToString();
            value["native_transaction"] = true;
            return value;
        }

        private static Dictionary<string, object> RenameThought(Binding b, JsonElement args, ref bool attempted)
        {
            Guid id = IdArgument(args, "id");
            string name = NameArgument(args);
            object existing = ReadNativeThought(b, id, false);
            RequireNormal(existing);
            string previous = StringValue(Property(existing, "Name"));
            string expected;
            if (OptionalString(args, "expected_name", out expected) && !string.Equals(previous, expected, StringComparison.Ordinal))
                throw new AdapterException("context_changed", "待改名节点的名称已变化，未执行改名。");
            if (string.Equals(previous, name, StringComparison.Ordinal))
                return Map("id", id.ToString(), "name", name, "no_op", true);
            EnsureSameBinding(b);
            attempted = true;
            object nativeResult = Call(b.Context, "__xxx__xx_x______x___x__", new object[] { name, id });
            CheckNativeResult(nativeResult);
            object renamed = Property(nativeResult, "Thought");
            if (renamed == null) renamed = ReadNativeThought(b, id, true);
            RefreshCurrentGraph(b);
            Dictionary<string, object> value = ThoughtValue(renamed, b.BrainId);
            value["previous_name"] = previous;
            value["native_transaction"] = true;
            return value;
        }

        private static Dictionary<string, object> AddLink(Binding b, JsonElement args, ref bool attempted)
        {
            Guid a = IdArgument(args, "a"), other = IdArgument(args, "b");
            if (a == other) throw new AdapterException("validation_error", "不能创建节点到自身的关系。");
            string relationName = StringArgument(args, "relation");
            int relation = relationName == "parent" ? 1 : relationName == "jump" ? 3 : 0;
            if (relation == 0) throw new AdapterException("validation_error", "relation 只能是 parent 或 jump。");
            RequireNormal(ReadNativeThought(b, a, false));
            RequireNormal(ReadNativeThought(b, other, false));
            Dictionary<string, object> graph = new Dictionary<string, object>();
            ReadGraph(b, a, graph);
            List<Dictionary<string, object>> links = (List<Dictionary<string, object>>)graph["links"];
            foreach (Dictionary<string, object> link in links)
            {
                Guid x = Guid.Parse((string)link["a"]), y = Guid.Parse((string)link["b"]);
                if (!((x == a && y == other) || (x == other && y == a))) continue;
                int current = (int)link["relation_value"];
                bool same = relation == 3 ? current == 3 : ((current == 1 && x == a && y == other) || (current == 2 && x == other && y == a));
                if (same) return Map("id", link["id"], "a", a.ToString(), "b", other.ToString(), "relation", relationName, "no_op", true);
                throw new AdapterException("existing_relation", "这两个节点已经有关联；未改变或合并原有关系。");
            }
            MethodInfo method = Method(b.Context, "_xxx_xxxx__x__xxx_xxxxx", 3);
            EnsureSameBinding(b);
            attempted = true;
            object nativeResult = Invoke(method, b.Context, new object[] { a, other, Enum.ToObject(method.GetParameters()[2].ParameterType, relation) });
            CheckNativeResult(nativeResult);
            RefreshCurrentGraph(b);
            graph.Clear();
            ReadGraph(b, a, graph);
            foreach (Dictionary<string, object> link in (List<Dictionary<string, object>>)graph["links"])
            {
                Guid x = Guid.Parse((string)link["a"]), y = Guid.Parse((string)link["b"]);
                if ((x == a && y == other) || (x == other && y == a))
                    return Map("id", link["id"], "a", a.ToString(), "b", other.ToString(), "relation", relationName, "native_transaction", true);
            }
            throw new AdapterException("readback_mismatch", "原生连线调用返回后没有读取到目标关系，请检查脑图，不要自动重试。");
        }

        private static Dictionary<string,object> DeleteThoughts(Binding b, JsonElement args, ref bool attempted)
        {
            // A whole explicit set goes through the same native transaction
            // used by the organizer. No recursive traversal is performed.
            Guid preserve;
            List<Guid> ids = ParseDeletionArguments(args,out preserve);
            if(preserve != b.ThoughtId)
                throw new AdapterException("validation_error","preserve_id 必须是当前活动根节点，不能指定其他保留节点。");
            if(ids.Contains(preserve))
                throw new AdapterException("validation_error","删除列表不能包含需要保留的当前根节点。");
            // This is the read-only permission check used by the normal UI.
            // Its message-producing wrappers must never be called by a tool.
            if(!Convert.ToBoolean(Call(b.Context,"_x__x__xxx_x___x__x___x",new object[0]),CultureInfo.InvariantCulture))
                throw new AdapterException("permission_denied","当前脑图没有原生写入权限，未执行删除。");
            Dictionary<string,Dictionary<string,object>> oldLinks = new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            Dictionary<Guid,object> oldAttachments = new Dictionary<Guid,object>();
            foreach(Guid id in ids)
            {
                object target;
                try { target = ReadNativeThought(b,id,false); }
                catch(AdapterException) { throw new AdapterException("unknown_thought","删除列表包含当前脑图中不存在或不可读取的节点；未执行删除。"); }
                RequireNormal(target);
                RequireDeletionAttachmentsReady(b,id,2,oldAttachments);
                Dictionary<string,object> graph = new Dictionary<string,object>();
                ReadGraph(b,id,graph);
                foreach(Dictionary<string,object> link in (List<Dictionary<string,object>>)graph["links"])
                    if(Convert.ToString(link["a"]) == id.ToString() || Convert.ToString(link["b"]) == id.ToString())
                        oldLinks[Convert.ToString(link["id"])] = link;
            }
            foreach(string linkId in oldLinks.Keys) RequireDeletionAttachmentsReady(b,Guid.Parse(linkId),3,oldAttachments);
            // The native getter has a stable, build-pinned not-found result.
            // Compare against it rather than treating an arbitrary read error
            // (permission/storage failure) as evidence of successful deletion.
            object missing = Call(b.Context,"__x___xx_x_xxx_xx_x_xx",new object[]{Guid.Empty});
            string missingSignature = NativeErrorsSignature(missing);
            if(String.IsNullOrEmpty(missingSignature) || OptionalProperty(missing,"Thought") != null)
                throw new AdapterException("native_error","未能核验原生节点缺失回执，未执行删除。");
            object logic = ProtectedProperty(b.Context,"__xxxxxxx____x___xxx");
            Guid previousHome = ReadNativeHome(b,logic), currentHome = previousHome;
            bool homeChanged = false;
            bool migrationAttempted=false,homeVerified=true;
            Action migrateHome=delegate
            {
            if(ids.Contains(previousHome))
            {
                // The UI protects its Home thought. Move Home to the explicitly retained
                // active root through the native business service before deleting the old one.
                // These are two native transactions; undoing deletion keeps a valid Home.
                EnsureSameBinding(b);
                RequireNormal(ReadNativeThought(b,preserve,false));
                migrationAttempted = true;
                try
                {
                    object homeReceipt = Call(logic,"___x_xx____x__xx____x__x",new object[]{b.BrainId,preserve});
                    CheckNativeResult(homeReceipt);
                    if(!Convert.ToBoolean(Call(homeReceipt,"_xxx___x_x__x_x__x_x__x_",new object[0]),CultureInfo.InvariantCulture))
                        throw new AdapterException("native_error","原生 Home 迁移未返回成功，未删除节点。");
                    currentHome = ReadNativeHome(b,logic);
                    homeChanged = currentHome != previousHome;
                    if(currentHome != preserve)
                        throw new AdapterException("readback_mismatch","未能核验 Home 已迁移至保留节点，未删除节点。");
                    RefreshNativeHome(b,preserve);
                    EnsureSameBinding(b);
                }
                catch(Exception error)
                {
                    homeVerified = false;
                    try
                    {
                        currentHome = ReadNativeHome(b,logic);
                        homeChanged = currentHome != previousHome;
                        homeVerified = true;
                        RefreshNativeHome(b,currentHome);
                    }
                    catch { }
                    AdapterException refusal = new AdapterException("native_error","Home 迁移没有完整核验成功，未删除任何节点。" + Unwrap(error).Message);
                    refusal.PartialResult = Map("deleted_count",0,"deleted_ids",new List<string>(),"deleted_links",0,
                        "deleted_link_ids",new List<string>(),"preserved_id",preserve.ToString(),"requested_count",ids.Count,
                        "home_changed",homeChanged,"previous_home_id",previousHome.ToString(),
                        "home_id",homeVerified ? (object)currentHome.ToString() : null,"home_verified",homeVerified,
                        "modified_count",homeChanged ? 1 : 0,"home_migration_separate_transaction",true,
                        "native_trash",true,"no_cascade",true,"unverified_mutation",!homeVerified);
                    throw refusal;
                }
            }
            };
            Exception mutationError = null;
            Dictionary<string,object> transaction = null;
            try
            {
                EnsureSameBinding(b);
                transaction = NativeDeletionTransaction.Execute(b.Context,logic,b.BrainId,ids,oldAttachments,migrateHome);
                object nativeAttempted;
                attempted=migrationAttempted || (transaction.TryGetValue("mutation_attempted",out nativeAttempted)
                    && Convert.ToBoolean(nativeAttempted,CultureInfo.InvariantCulture));
                object succeeded;
                if(!transaction.TryGetValue("success",out succeeded) || !Convert.ToBoolean(succeeded,CultureInfo.InvariantCulture))
                    throw new AdapterException(transaction.ContainsKey("code") ? Convert.ToString(transaction["code"]) : "native_error",
                        transaction.ContainsKey("error") ? Convert.ToString(transaction["error"]) : "原生批量删除未返回成功，已停止后续操作。");
            }
            catch(Exception error) { mutationError = Unwrap(error); }

            List<string> deleted = new List<string>();
            List<string> unverified = new List<string>();
            List<string> surviving = new List<string>();
            foreach(Guid id in ids)
            {
                try
                {
                    object lookup = Call(b.Context,"__x___xx_x_xxx_xx_x_xx",new object[]{id});
                    object thought = OptionalProperty(lookup,"Thought");
                    if(thought != null)
                    {
                        if(OptionalProperty(thought,"DeletionDateTime") != null) deleted.Add(id.ToString());
                        else surviving.Add(id.ToString());
                    }
                    else if(NativeErrorsSignature(lookup) == missingSignature) deleted.Add(id.ToString());
                    else unverified.Add(id.ToString());
                }
                catch { unverified.Add(id.ToString()); }
            }
            HashSet<string> deletedSet = new HashSet<string>(deleted,StringComparer.Ordinal);
            List<string> removedLinks = new List<string>();
            foreach(KeyValuePair<string,Dictionary<string,object>> link in oldLinks)
                if(deletedSet.Contains(Convert.ToString(link.Value["a"])) || deletedSet.Contains(Convert.ToString(link.Value["b"])))
                    removedLinks.Add(link.Key);
            removedLinks.Sort(StringComparer.Ordinal);
            HashSet<Guid> remainingAttachments=new HashSet<Guid>();
            HashSet<string> checkedSources=new HashSet<string>(StringComparer.Ordinal);
            HashSet<Guid> unverifiedAttachments=new HashSet<Guid>();
            foreach(object attachment in oldAttachments.Values)
            {
                Guid source=GuidValue(Property(attachment,"SourceId"));
                int sourceKind=Convert.ToInt32(Property(attachment,"SourceType"),CultureInfo.InvariantCulture);
                string sourceKey=source.ToString()+":"+sourceKind.ToString(CultureInfo.InvariantCulture);
                if(!checkedSources.Add(sourceKey)) continue;
                try
                {
                    MethodInfo list=Method(b.Context,"_x_x__xx___x_xxxxxxx",2);
                    object lookup=Invoke(list,b.Context,new object[]{source,Enum.ToObject(list.GetParameters()[1].ParameterType,sourceKind)});
                    CheckNativeResult(lookup);
                    IEnumerable actual=Property(Property(lookup,"Data"),"Attachments") as IEnumerable;
                    if(actual == null) throw new AdapterException("native_error","删除后附件列表没有回执。");
                    foreach(object item in actual) remainingAttachments.Add(GuidValue(Property(item,"Id")));
                }
                catch
                {
                    bool sourceDeleted=sourceKind == 2 ? deletedSet.Contains(source.ToString()) : removedLinks.Contains(source.ToString());
                    if(!sourceDeleted)
                        foreach(KeyValuePair<Guid,object> original in oldAttachments)
                            if(GuidValue(Property(original.Value,"SourceId")) == source) unverifiedAttachments.Add(original.Key);
                }
            }
            List<string> deletedAttachments=new List<string>(),unverifiedAttachmentIds=new List<string>();
            foreach(Guid id in oldAttachments.Keys)
                if(unverifiedAttachments.Contains(id)) unverifiedAttachmentIds.Add(id.ToString());
                else if(!remainingAttachments.Contains(id)) deletedAttachments.Add(id.ToString());
            deletedAttachments.Sort(StringComparer.Ordinal);
            unverifiedAttachmentIds.Sort(StringComparer.Ordinal);
            Dictionary<string,object> result = Map("deleted_count",deleted.Count,"deleted_ids",deleted,
                "deleted_links",removedLinks.Count,"deleted_link_ids",removedLinks,"preserved_id",preserve.ToString(),
                "requested_count",ids.Count,"remaining_ids",surviving,"unverified_ids",unverified,
                "native_transaction",true,"native_trash",true,"no_cascade",true,
                "home_changed",homeChanged,"previous_home_id",previousHome.ToString(),"home_id",currentHome.ToString(),
                "home_verified",homeVerified,"modified_count",homeChanged ? 1 : 0,
                "home_migration_separate_transaction",homeChanged,"native_transaction_count",homeChanged ? 2 : 1,
                "attachments_before",oldAttachments.Count,"deleted_attachments",deletedAttachments.Count,
                "deleted_attachment_ids",deletedAttachments,"unverified_attachment_ids",unverifiedAttachmentIds,
                "unverified_mutation",unverified.Count > 0 || unverifiedAttachments.Count > 0 || mutationError != null);
            if(transaction != null)
                foreach(KeyValuePair<string,object> field in transaction)
                    if(field.Key != "success" && field.Key != "code" && field.Key != "error") result[field.Key]=field.Value;
            try
            {
                EnsureSameBinding(b);
                RequireNormal(ReadNativeThought(b,preserve,false));
                RefreshCurrentGraph(b);
                if(homeChanged)
                {
                    homeVerified=false;
                    result["home_verified"] = false;
                    Guid verifiedHome = ReadNativeHome(b,logic);
                    result["home_id"] = verifiedHome.ToString();
                    homeVerified=verifiedHome == preserve;
                    result["home_verified"] = homeVerified;
                    if(verifiedHome != preserve)
                        throw new AdapterException("readback_mismatch","删除后 Home 未保持在保留节点，请核对已完成部分。");
                }
            }
            catch(Exception error) { if(mutationError == null) mutationError = Unwrap(error); }
            if(mutationError != null || deleted.Count != ids.Count || unverifiedAttachments.Count > 0)
            {
                AdapterException nativeFailure=mutationError as AdapterException;
                AdapterException refusal = new AdapterException(nativeFailure == null ? "native_error" : nativeFailure.Code,"原生删除没有完整核验成功；已确认删除 " + deleted.Count
                    + " 个节点，已停止后续操作，请核对已完成部分。不会自动重试。"
                    + (mutationError == null ? "" : " 原因：" + mutationError.Message));
                object helperUnknown;
                bool transactionUnknown=transaction == null ? attempted
                    : (!transaction.TryGetValue("unverified_mutation",out helperUnknown) || Convert.ToBoolean(helperUnknown,CultureInfo.InvariantCulture));
                result["unverified_mutation"] = transactionUnknown || unverified.Count > 0 || unverifiedAttachments.Count > 0 || !homeVerified;
                refusal.PartialResult = result;
                throw refusal;
            }
            return result;
        }

        private static Guid ReadNativeHome(Binding b,object logic)
        {
            object settingsResult = Call(logic,"__x___x__xxxxxx_xx__x_x",new object[]{b.BrainId});
            CheckNativeResult(settingsResult);
            if(!Convert.ToBoolean(Call(settingsResult,"_xxx___x_x__x_x__x_x__x_",new object[0]),CultureInfo.InvariantCulture))
                throw new AdapterException("native_error","无法读取当前脑图的原生 Home 设置，未继续删除。");
            return GuidValue(Property(Property(settingsResult,"BrainSettings"),"_____x_xx_x__xxx_xx__xxx"));
        }

        private static void RefreshNativeHome(Binding b,Guid expected)
        {
            object view = Property(b.Context,"_x_x_xxx__xx__xx__x_xxx_");
            // Reload settings and their metadata cache through the client's native refresh.
            // This keeps Navigate.Home and reopening the brain consistent without setting entities.
            Call(view,"_xxx_x_x__xxxxxxxx__x__x",new object[0]);
            Guid viewHome = GuidValue(Property(Property(view,"_x__x_____xx_xx__xxxxx"),"_____x_xx_x__xxx_xx__xxx"));
            Guid metadataHome = GuidValue(Property(Property(view,"___x_x_x_____x______x__x"),"__x___x__xxx__x_xx__x_x"));
            if(viewHome != expected || metadataHome != expected)
                throw new AdapterException("readback_mismatch","原生 Home 设置与当前会话缓存不一致，未继续删除。");
        }

        private static List<Guid> ParseDeletionArguments(JsonElement args,out Guid preserve)
        {
            HashSet<string> required = new HashSet<string>(new string[]{"ids","preserve_id"},StringComparer.Ordinal);
            foreach(JsonProperty field in args.EnumerateObject())
                if(!required.Remove(field.Name)) throw new AdapterException("validation_error","删除参数包含未知或重复字段。");
            if(required.Count != 0) throw new AdapterException("validation_error","删除参数必须包含 ids 和 preserve_id。");
            preserve = IdArgument(args,"preserve_id");
            JsonElement array = args.GetProperty("ids");
            if(array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < 1 || array.GetArrayLength() > 40)
                throw new AdapterException("validation_error","一次删除必须明确指定 1 到 40 个节点 UUID。");
            List<Guid> result = new List<Guid>();
            HashSet<Guid> seen = new HashSet<Guid>();
            foreach(JsonElement item in array.EnumerateArray())
            {
                if(item.ValueKind != JsonValueKind.String) throw new AdapterException("validation_error","ids 只能包含 UUID 字符串。");
                Guid id = ParseId(item.GetString(),"ids");
                if(!seen.Add(id)) throw new AdapterException("validation_error","删除列表不能包含重复 UUID。");
                result.Add(id);
            }
            return result;
        }

        private static string NativeErrorsSignature(object result)
        {
            IEnumerable errors = OptionalProperty(result,"Errors") as IEnumerable;
            if(errors == null) return null;
            List<string> values = new List<string>();
            foreach(object error in errors) values.Add(StringValue(error));
            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }

        private static void RequireDeletionAttachmentsReady(Binding b,Guid source,int sourceKind,Dictionary<Guid,object> collected)
        {
            MethodInfo list = Method(b.Context,"_x_x__xx___x_xxxxxxx",2);
            object lookup = Invoke(list,b.Context,new object[]{source,Enum.ToObject(list.GetParameters()[1].ParameterType,sourceKind)});
            CheckNativeResult(lookup);
            object data = Property(lookup,"Data");
            IEnumerable attachments = data == null ? null : Property(data,"Attachments") as IEnumerable;
            if(attachments == null) throw new AdapterException("native_error","未能核验待删除节点或关系的附件，未执行删除。");
            foreach(object attachment in attachments)
            {
                Guid attachmentId=GuidValue(Property(attachment,"Id"));
                if(attachmentId == Guid.Empty || GuidValue(Property(attachment,"BrainId")) != b.BrainId
                    || GuidValue(Property(attachment,"SourceId")) != source
                    || Convert.ToInt32(Property(attachment,"SourceType"),CultureInfo.InvariantCulture) != sourceKind)
                    throw new AdapterException("native_error","待删除附件的原生身份或来源不一致，未执行删除。");
                collected[attachmentId]=attachment;
                string kind = Convert.ToString(Property(attachment,"Type"));
                // Exact file/directory kinds checked by OrganizerContext's
                // normal deletion wrapper. Inline notes and external reference
                // records follow the native transaction's own normal handling.
                if(kind != "______xxxxx____x_x_____" && kind != "__x___x_xxxx_xxxx_x___") continue;
                object resolver = ProtectedProperty(b.Context,"_x___xx_xx___x_xxxxxx__x");
                string path = Convert.ToString(Call(resolver,"__x__xxxx___x_x__x_x_x__",new object[]{attachment}));
                object ready = Invoke(ProtectedMethod(b.Context,"_x__x_xxx_xxxx_xxx__x_x",3),b.Context,new object[]{path,false,false});
                if(!String.Equals(Convert.ToString(ready),"Ready",StringComparison.Ordinal))
                    throw new AdapterException("attachment_not_ready","待删除节点或关系包含被占用、只读或不可处理的文件附件（"
                        + Convert.ToString(ready) + "）；未删除任何节点。请先在 TheBrain 中处理该附件状态。");
            }
        }

        private static MethodInfo ProtectedMethod(object value,string name,int count)
        {
            for(Type type = value.GetType(); type != null; type = type.BaseType)
                foreach(MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if(method.Name == name && method.GetParameters().Length == count) return method;
            throw new MissingMethodException(value.GetType().FullName,name);
        }

        private static object ProtectedProperty(object value,string name)
        {
            for(Type type = value.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(name,BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if(property != null) return property.GetValue(value,null);
            }
            throw new MissingMemberException(value.GetType().FullName,name);
        }

        private static Dictionary<string, object> ReplaceNote(Binding b, JsonElement args, ref bool attempted)
        {
            string text = NormalizeNote(StringArgument(args, "text"));
            if (text.Length > 1000000) throw new AdapterException("validation_error", "单次笔记长度不能超过 1,000,000 字符。");
            // Keza embeds private-use document sentinels in its backing text. Never accept these
            // control markers from the external model as editor input.
            foreach (char c in text)
                if ((c >= '\uf400' && c <= '\uf40f') || c == '\uf50a' || c == '\0')
                    throw new AdapterException("validation_error", "笔记包含不允许的编辑器控制字符。");
            string expected;
            if (OptionalString(args, "expected_text", out expected) && !string.Equals(NormalizeNote(b.Text), NormalizeNote(expected), StringComparison.Ordinal))
                throw new AdapterException("context_changed", "笔记内容与请求所依据的内容不同，未执行替换。");
            if (string.Equals(NormalizeNote(b.Text), text, StringComparison.Ordinal))
                return Map("id", b.ThoughtId.ToString(), "text", b.Text, "dirty", b.Dirty, "no_op", true);
            object[] bounds = new object[] { b.RegionId, false, null, null };
            Call(b.Document, "___xxxxxxx____xxxx_x_x_x", bounds);
            object start = bounds[2], end = bounds[3];
            if (start == null || end == null) throw new AdapterException("note_unavailable", "未读取到当前笔记的编辑范围。");
            EnsureSameBinding(b);
            string label = "Brainmind AI note edit";
            Call(b.Document, "______x__x_xx__xx__xx_", new object[] { b.Selection, label, false });
            try
            {
                attempted = true;
                // Same command used by the native AI suggestion accept action. Native Keza owns
                // selection, changed flags, notifications and undo data. This is not a raw setter.
                Call(b.Keza, "_xx_x_____xxx_xx__x__xx_", new object[] { text, start, end, false, false });
            }
            finally
            {
                object afterSelection = Property(b.Keza, "_x_x__xx_x__xxxx_xxx___x");
                Call(b.Document, "__xx____xxx___x_xx_x_x", new object[] { afterSelection, label });
            }
            EnsureSameBinding(b);
            string actual = StringValue(Call(b.Document, "_xxxxx_x_x_x_xxx__x_x", new object[] { b.RegionId }));
            bool dirty = Convert.ToBoolean(Call(b.Document, "_xxxx___x__xxxx_xxx_x_", new object[] { b.RegionId }), CultureInfo.InvariantCulture);
            if (!string.Equals(NormalizeNote(actual), text, StringComparison.Ordinal))
                throw new AdapterException("readback_mismatch", "编辑器读回文本与请求不同。原生编辑可能已经发生，请检查笔记，不要自动重试。");
            return Map("id", b.ThoughtId.ToString(), "text", actual, "dirty", dirty,
                "native_undo_group", label, "persistence", "native_editor_autosave");
        }

        private static Binding Capture()
        {
            Assembly desktop = RequireThreadAndVersion();
            Binding b = new Binding();
            b.Desktop = desktop;
            b.Snapshot = Map("state", "no_active_window", "application_file_version", SupportedVersion,
                "brain_id", null, "brain_name", null, "active_thought_id", null, "active_thought_name", null,
                "note", Map("available", false), "neighbors", new List<Dictionary<string, object>>(),
                "links", new List<Dictionary<string, object>>(), "context_token", null);
            Type windowType = desktop.GetType(WindowType, true);
            b.Window = StaticProperty(windowType, "_x_xxx____xxxx____xxx_");
            if (b.Window == null) return b;
            b.Organizer = Property(b.Window, "___x___xx_xxx___x_x_x__x");
            if (b.Organizer == null) { b.Snapshot["state"] = "no_active_brain"; return b; }
            b.Context = Property(b.Organizer, "_x__x_xx__x__xx_x_xxx_");
            if (b.Context == null) { b.Snapshot["state"] = "no_active_brain"; return b; }
            b.BrainId = GuidValue(Property(b.Context, "__x_xxx__xxx_xx_x__x___"));
            object brain = Property(b.Context, "__x__x_xxxxxx___xxx___x");
            b.BrainName = brain == null ? null : StringValue(Property(brain, "_xxxx_x_x_xx_xxx_x_xx_xx"));
            b.Active = Property(b.Context, "_x_xxx__xx_x_xx__xx__x__");
            b.ThoughtId = b.Active == null ? Guid.Empty : GuidValue(Property(b.Active, "Id"));
            b.ThoughtName = b.Active == null ? null : StringValue(Property(b.Active, "Name"));
            b.Snapshot["state"] = b.BrainId == Guid.Empty ? "no_active_brain" : b.ThoughtId == Guid.Empty ? "no_active_thought" : "ready";
            b.Snapshot["brain_id"] = b.BrainId == Guid.Empty ? null : b.BrainId.ToString();
            b.Snapshot["brain_name"] = b.BrainName;
            b.Snapshot["active_thought_id"] = b.ThoughtId == Guid.Empty ? null : b.ThoughtId.ToString();
            b.Snapshot["active_thought_name"] = b.ThoughtName;
            if (b.BrainId == Guid.Empty || b.ThoughtId == Guid.Empty)
            {
                ((Dictionary<string, object>)b.Snapshot["note"])["reason"] = "no_active_session";
                return b;
            }
            lock (EpochLock)
            {
                nativeRevisions.TryGetValue(b.BrainId, out b.NativeRevision);
                b.UnknownNativeRevision = unknownNativeRevision;
            }
            if (b.BrainId != Guid.Empty && b.ThoughtId != Guid.Empty)
            {
                // This native read populates its cache; it does not activate or save any thought.
                ReadGraph(b, b.ThoughtId, b.Snapshot);
                object latest = ReadNativeThought(b, b.ThoughtId, false);
                b.ThoughtName = StringValue(Property(latest, "Name"));
                b.Snapshot["active_thought_name"] = b.ThoughtName;
                b.Snapshot["active_thought"] = ThoughtValue(latest, b.BrainId);
            }
            Dictionary<string, object> note = (Dictionary<string, object>)b.Snapshot["note"];
            b.Editor = Property(b.Organizer, "___x___x_xx____xxx_xxx");
            if (b.Editor == null) b.NoteReason = "editor_not_ready";
            else
            {
                object editorContext = Field(b.Editor, "__xxx_xxxxxx_x__x_xxxx_x");
                Guid editorBrainId = GuidValue(Property(editorContext, "__x_xxx__xxx_xx_x__x___"));
                Guid source = GuidValue(Property(b.Editor, "___xx_x_x__x__x_____x___"));
                int sourceType = Convert.ToInt32(Property(b.Editor, "_xxxx__xxx_xx__x__xxx_"), CultureInfo.InvariantCulture);
                b.RegionId = GuidValue(Property(b.Editor, "________________xx_x_x_"));
                if (editorBrainId != b.BrainId || source != b.ThoughtId || sourceType != 2 || b.ThoughtId == Guid.Empty)
                    b.NoteReason = "editor_source_not_active_thought";
                else
                {
                    b.Document = Property(b.Editor, "_x___xx__xxxxxx_xxxxxx");
                    b.Keza = Property(b.Editor, "___xxx_xxx_xxx_x_xx_x_x_");
                    b.Selection = b.Keza == null ? null : Property(b.Keza, "_x_x__xx_x__xxxx_xxx___x");
                    if (b.Document == null || b.Keza == null || b.Selection == null || b.RegionId == Guid.Empty)
                        b.NoteReason = "document_not_ready";
                    else
                    {
                        bool dirtyBefore = Convert.ToBoolean(Call(b.Document, "_xxxx___x__xxxx_xxx_x_", new object[] { b.RegionId }), CultureInfo.InvariantCulture);
                        b.Text = StringValue(Call(b.Document, "_xxxxx_x_x_x_xxx__x_x", new object[] { b.RegionId }));
                        b.Dirty = Convert.ToBoolean(Call(b.Document, "_xxxx___x__xxxx_xxx_x_", new object[] { b.RegionId }), CultureInfo.InvariantCulture);
                        if (dirtyBefore != b.Dirty) b.NoteReason = "note_changed_during_read";
                        else
                        {
                            b.NoteAvailable = true;
                            note["available"] = true;
                            note["text"] = b.Text;
                            note["dirty"] = b.Dirty;
                            note["origin"] = "Keza in-memory document region";
                            note["source_id"] = source.ToString();
                            note["document_region_id"] = b.RegionId.ToString();
                        }
                    }
                }
            }
            if (!b.NoteAvailable) note["reason"] = b.NoteReason;
            lock (EpochLock)
            {
                if (!Object.ReferenceEquals(lastWindow, b.Window) || !Object.ReferenceEquals(lastOrganizer, b.Organizer)
                    || !Object.ReferenceEquals(lastContext, b.Context) || !Object.ReferenceEquals(lastEditor, b.Editor))
                {
                    bindingEpoch++;
                    lastWindow = b.Window; lastOrganizer = b.Organizer; lastContext = b.Context; lastEditor = b.Editor;
                }
                b.Epoch = bindingEpoch;
            }
            EnsureSameBinding(b);
            lock (EpochLock)
            {
                long current;
                nativeRevisions.TryGetValue(b.BrainId, out current);
                if (current != b.NativeRevision || unknownNativeRevision != b.UnknownNativeRevision)
                    throw new AdapterException("context_changed", "原生模型在读取期间发生变化，请重新读取当前上下文。");
            }
            b.Snapshot["native_model_revision"] = b.NativeRevision;
            b.Snapshot["context_token"] = Token(b);
            return b;
        }

        private static void ReadGraph(Binding b, Guid id, Dictionary<string, object> output)
        {
            object nativeResult = Call(b.Context, "_xx_x_x__x_______x__x__", new object[] { id, false, false });
            CheckNativeResult(nativeResult);
            object data = Property(nativeResult, "Data");
            if (data == null) throw new AdapterException("native_error", "原生模型未返回节点邻接信息。");
            object center = Property(data, "_x___xxx__xx___x_x__xx__");
            if (center == null || GuidValue(Property(center, "Id")) != id)
                throw new AdapterException("unknown_thought", "当前脑图中不存在这个节点 UUID。");
            List<Dictionary<string, object>> neighbors = new List<Dictionary<string, object>>();
            HashSet<Guid> seen = new HashSet<Guid>();
            IEnumerable nativeThoughts = Property(data, "_x_____xxx___xxx_xx__x_x") as IEnumerable;
            if (nativeThoughts != null)
                foreach (object thought in nativeThoughts)
                {
                    Guid neighborId = GuidValue(Property(thought, "Id"));
                    if (neighborId == id || !seen.Add(neighborId)) continue;
                    neighbors.Add(ThoughtValue(thought, b.BrainId));
                }
            neighbors.Sort(delegate(Dictionary<string, object> x, Dictionary<string, object> y) { return StringComparer.Ordinal.Compare((string)x["id"], (string)y["id"]); });
            List<Dictionary<string, object>> links = new List<Dictionary<string, object>>();
            IEnumerable nativeLinks = Property(data, "__xx_x___xxxxx__x__xx_x_") as IEnumerable;
            if (nativeLinks != null)
                foreach (object link in nativeLinks)
                {
                    Guid a = GuidValue(Property(link, "ThoughtIdA")), other = GuidValue(Property(link, "ThoughtIdB"));
                    int relation = Convert.ToInt32(Property(link, "Relation"), CultureInfo.InvariantCulture);
                    links.Add(Map("id", GuidValue(Property(link, "Id")).ToString(), "a", a.ToString(), "b", other.ToString(),
                        "relation", relation == 1 ? "parent" : relation == 2 ? "child" : relation == 3 ? "jump" : "special",
                        "relation_value", relation));
                }
            links.Sort(delegate(Dictionary<string, object> x, Dictionary<string, object> y) { return StringComparer.Ordinal.Compare((string)x["id"], (string)y["id"]); });
            output["neighbors"] = neighbors;
            output["links"] = links;
        }

        private static object ReadNativeThought(Binding b, Guid id, bool verify)
        {
            // This context is bound to one brain. No global entity lookup is used, so a UUID from
            // another brain cannot be resolved here even if that brain is open in another tab.
            object result = Call(b.Context, "__x___xx_x_xxx_xx_x_xx", new object[] { id });
            CheckNativeResult(result);
            object thought = Property(result, "Thought");
            if (thought == null || GuidValue(Property(thought, "Id")) != id)
                throw new AdapterException("unknown_thought", "当前脑图中不存在这个节点 UUID。");
            if (verify) EnsureSameBinding(b);
            return thought;
        }

        private static void RefreshCurrentGraph(Binding b)
        {
            EnsureSameBinding(b);
            // CoreTransaction has already published its native invalidation. Re-read through the
            // organizer's standard cache-refresh path with refreshActive=true. This updates the
            // active view snapshot without activating another thought or reloading/saving its note.
            object result = Call(b.Context, "_xx_x_x__x_______x__x__", new object[] { b.ThoughtId, true, false });
            CheckNativeResult(result);
        }

        private static Dictionary<string, object> ThoughtValue(object thought, Guid brain)
        {
            return Map("id", GuidValue(Property(thought, "Id")).ToString(), "brain_id", brain.ToString(),
                "name", StringValue(Property(thought, "Name")), "kind", Convert.ToInt32(Property(thought, "Kind"), CultureInfo.InvariantCulture),
                "label", StringValue(Property(thought, "Label")), "modified", OptionalProperty(thought, "ModificationDateTime"),
                "links_modified", OptionalProperty(thought, "LinksModificationDateTime"));
        }

        private static void RequireNormal(object thought)
        {
            if (Convert.ToInt32(Property(thought, "Kind"), CultureInfo.InvariantCulture) != 1)
                throw new AdapterException("unsupported_thought_kind", "当前扩展只编辑普通节点，不能编辑类型、标签、事件或系统节点。");
            if (OptionalProperty(thought, "DeletionDateTime") != null)
                throw new AdapterException("unsupported_thought_kind", "不能编辑已删除的节点。");
        }

        private static void RequireSession(Binding b)
        {
            if (b.Context == null || b.BrainId == Guid.Empty || b.ThoughtId == Guid.Empty)
                throw new AdapterException("no_active_session", "请先打开脑图并激活一个普通节点。");
        }

        private static void EnsureSameBinding(Binding b)
        {
            object window = StaticProperty(b.Desktop.GetType(WindowType, true), "_x_xxx____xxxx____xxx_");
            object organizer = window == null ? null : Property(window, "___x___xx_xxx___x_x_x__x");
            object context = organizer == null ? null : Property(organizer, "_x__x_xx__x__xx_x_xxx_");
            if (!Object.ReferenceEquals(window, b.Window) || !Object.ReferenceEquals(organizer, b.Organizer)
                || !Object.ReferenceEquals(context, b.Context) || context == null
                || GuidValue(Property(context, "__x_xxx__xxx_xx_x__x___")) != b.BrainId)
                throw new AdapterException("context_changed", "当前脑图会话已切换，未继续执行。");
            object active = Property(context, "_x_xxx__xx_x_xx__xx__x__");
            if (active == null || GuidValue(Property(active, "Id")) != b.ThoughtId)
                throw new AdapterException("context_changed", "活动节点已切换，未继续执行。");
            object editor = Property(organizer, "___x___x_xx____xxx_xxx");
            if (b.Editor != null && !Object.ReferenceEquals(editor, b.Editor))
                throw new AdapterException("context_changed", "当前笔记编辑器已切换，未继续执行。");
            if (b.NoteAvailable)
            {
                object editorContext = Field(editor, "__xxx_xxxxxx_x__x_xxxx_x");
                if (!Object.ReferenceEquals(Property(editor, "_x___xx__xxxxxx_xxxxxx"), b.Document)
                    || GuidValue(Property(editor, "___xx_x_x__x__x_____x___")) != b.ThoughtId
                    || GuidValue(Property(editor, "________________xx_x_x_")) != b.RegionId
                    || GuidValue(Property(editorContext, "__x_xxx__xxx_xx_x__x___")) != b.BrainId
                    || Convert.ToInt32(Property(editor, "_xxxx__xxx_xx__x__xxx_"), CultureInfo.InvariantCulture) != 2)
                    throw new AdapterException("context_changed", "笔记绑定已改变，未继续执行。");
            }
        }

        private static string Token(Binding b)
        {
            StringBuilder material = new StringBuilder();
            Append(material, b.Epoch.ToString(CultureInfo.InvariantCulture));
            Append(material, b.NativeRevision.ToString(CultureInfo.InvariantCulture));
            Append(material, b.UnknownNativeRevision.ToString(CultureInfo.InvariantCulture));
            Append(material, Identity(b.Window)); Append(material, Identity(b.Context)); Append(material, Identity(b.Editor)); Append(material, Identity(b.Document));
            Append(material, b.BrainId.ToString()); Append(material, b.ThoughtId.ToString()); Append(material, b.ThoughtName);
            Append(material, b.NoteAvailable ? NormalizeNote(b.Text) : "!unavailable:" + b.NoteReason);
            object active;
            if (b.Snapshot.TryGetValue("active_thought", out active)) Append(material, JsonSerializer.Serialize(TokenThought(active)));
            Append(material, JsonSerializer.Serialize(TokenThoughts(b.Snapshot["neighbors"])));
            Append(material, JsonSerializer.Serialize(b.Snapshot["links"]));
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(material.ToString()));
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }

        // Native autosave/cache refresh can update persistence timestamps after
        // a successful operation without changing what the user or model read.
        // Keep those timestamps in the public diagnostic snapshot, but not its
        // optimistic content token. Names, labels, kinds, identities and all
        // other thought fields remain protected, as do note text, edges and the
        // brain-wide native graph revision above.
        private static object TokenThought(object value)
        {
            IDictionary<string, object> thought = value as IDictionary<string, object>;
            if (thought == null) return value;
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> item in thought)
                if (item.Key != "modified" && item.Key != "links_modified") result[item.Key] = item.Value;
            return result;
        }

        private static object TokenThoughts(object value)
        {
            IEnumerable thoughts = value as IEnumerable;
            if (thoughts == null) return value;
            List<object> result = new List<object>();
            foreach (object thought in thoughts) result.Add(TokenThought(thought));
            return result;
        }

        private static void Append(StringBuilder value, string part)
        {
            part = part ?? "";
            value.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append('|');
        }

        private static string Identity(object value) { return value == null ? "null" : RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture); }

        private static Assembly RequireThreadAndVersion()
        {
            Assembly desktop = Loaded("TheBrainDesktop"), xwt = Loaded("Xwt");
            if (desktop == null || xwt == null) throw new InvalidOperationException("TheBrain UI 尚未就绪。");
            string version = FileVersionInfo.GetVersionInfo(desktop.Location).FileVersion;
            if (version != SupportedVersion) throw new InvalidOperationException("只支持 TheBrain " + SupportedVersion + "，当前版本是 " + version + "。");
            if (Convert.ToBoolean(StaticProperty(xwt.GetType("Xwt.Application", true), "InvokeRequired"), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("TheBrainAdapter 必须在 Xwt UI 线程调用。");
            SubscribeNativeChanges();
            return desktop;
        }

        private static void SubscribeNativeChanges()
        {
            lock (EpochLock)
            {
                if (nativeChangeHandler != null) return;
                Assembly core = Loaded("TheBrainCore");
                if (core == null) throw new InvalidOperationException("TheBrain 原生模型尚未就绪。");
                Type publisher = core.GetType("TheBrain._xxx_____xx_x_x_xx__x.__xx_x_xx__x_xx___x__x__", true);
                EventInfo changed = publisher.GetEvent("_xxxxx__xx_xx__x_x__xx", BindingFlags.Public | BindingFlags.Static);
                if (changed == null) throw new MissingMemberException(publisher.FullName, "native invalidation event");
                Type eventArgs = changed.EventHandlerType.GetGenericArguments()[0];
                MethodInfo callback = typeof(TheBrainAdapter).GetMethod("NativeChanged", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(eventArgs);
                Delegate handler = Delegate.CreateDelegate(changed.EventHandlerType, callback);
                changed.AddEventHandler(null, handler);
                nativeChangeHandler = handler;
            }
        }

        // CoreTransaction publishes this same event to the native organizer and model caches.
        // Track all graph mutations in the brain, including UUIDs outside the current neighborhood.
        // The named EvictorDto.IsNotesOnlyChange mapping was verified in this exact build; a pure
        // autosave does not invalidate a request when its in-memory note text is still identical.
        private static void NativeChanged<T>(object sender, T arguments) where T : EventArgs
        {
            try
            {
                object eviction = Property(arguments, "_x___x_xxxxxx_x___xxx_xx");
                if (Convert.ToBoolean(Property(eviction, "___x_xx__x_xx_x_xx__x_x"), CultureInfo.InvariantCulture)) return;
                Guid brain = GuidValue(Property(eviction, "___x__xx__xx_x_xx_x_x_x_"));
                lock (EpochLock)
                {
                    long revision;
                    nativeRevisions.TryGetValue(brain, out revision);
                    nativeRevisions[brain] = revision + 1;
                }
            }
            catch
            {
                // Never throw into the application's event publisher. A changed event we cannot
                // classify invalidates every pending request, instead of accidentally allowing it.
                lock (EpochLock) { unknownNativeRevision++; }
            }
        }

        private static Assembly Loaded(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == name) return assembly;
            return null;
        }

        private static void CheckNativeResult(object value)
        {
            if (value == null) throw new AdapterException("native_error", "原生业务方法未返回结果。");
            IEnumerable errors = OptionalProperty(value, "Errors") as IEnumerable;
            if (errors == null) return;
            List<string> messages = new List<string>();
            foreach (object error in errors) messages.Add(StringValue(error));
            if (messages.Count != 0) throw new AdapterException("native_error", string.Join("; ", messages.ToArray()));
        }

        private static Guid IdArgument(JsonElement args, string name) { return ParseId(StringArgument(args, name), name); }
        private static Guid ParseId(string value, string name)
        {
            Guid id;
            if (!Guid.TryParse(value, out id) || id == Guid.Empty) throw new AdapterException("validation_error", name + " 必须是有效的非空 UUID。");
            return id;
        }

        private static string NameArgument(JsonElement args)
        {
            string value = StringArgument(args, "name").Trim();
            if (value.Length == 0 || value.Length > 1024) throw new AdapterException("validation_error", "节点名称长度必须为 1 到 1024 字符。");
            foreach (char c in value)
                if (char.IsControl(c) || (c >= '\uf400' && c <= '\uf40f') || c == '\uf50a') throw new AdapterException("validation_error", "节点名称不能包含换行或控制字符。");
            return value;
        }

        private static bool NativeWouldImport(string name)
        {
            Assembly sys = Loaded("TheBrain.Sys");
            if (sys == null) throw new AdapterException("native_error", "TheBrain 名称检查器尚未就绪。");
            Type classifier = sys.GetType("TheBrain.__x___x___xx__x_x_xxxxx._x_xxxxxx_x___x_xxx__x_", true);
            MethodInfo method = classifier.GetMethod("__x_xxx_x__xxx_x_x__x_x", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string), typeof(bool) }, null);
            if (method == null) throw new MissingMethodException(classifier.FullName, "name import classifier");
            return Convert.ToBoolean(Invoke(method, null, new object[] { name, false }), CultureInfo.InvariantCulture);
        }

        private static string StringArgument(JsonElement args, string name)
        {
            string value;
            if (!OptionalString(args, name, out value)) throw new AdapterException("validation_error", "缺少字符串参数：" + name);
            return value;
        }

        private static bool OptionalString(JsonElement args, string name, out string value)
        {
            JsonElement property;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out property))
            {
                if (property.ValueKind != JsonValueKind.String) throw new AdapterException("validation_error", name + " 必须是字符串。");
                value = property.GetString();
                return true;
            }
            value = null;
            return false;
        }

        private static string NormalizeNote(string value) { return (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n'); }
        private static Guid GuidValue(object value) { return value is Guid ? (Guid)value : Guid.Parse(StringValue(value)); }
        private static string StringValue(object value) { return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static object StaticProperty(Type type, string name) { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static).GetValue(null, null); }
        private static object Property(object value, string name)
        {
            if (value == null) throw new InvalidOperationException("Cannot read " + name + " from null.");
            PropertyInfo property = value.GetType().GetProperty(name, PublicInstance);
            if (property == null) throw new MissingMemberException(value.GetType().FullName, name);
            return property.GetValue(value, null);
        }

        private static object OptionalProperty(object value, string name)
        {
            if (value == null) return null;
            PropertyInfo property = value.GetType().GetProperty(name, PublicInstance);
            return property == null ? null : property.GetValue(value, null);
        }

        private static object Field(object value, string name)
        {
            if (value == null) return null;
            FieldInfo field = value.GetType().GetField(name, PublicInstance);
            if (field == null) throw new MissingFieldException(value.GetType().FullName, name);
            return field.GetValue(value);
        }

        private static MethodInfo Method(object value, string name, int count)
        {
            MethodInfo match = null;
            foreach (MethodInfo method in value.GetType().GetMethods(PublicInstance))
                if (method.Name == name && method.GetParameters().Length == count)
                {
                    if (match != null) throw new AmbiguousMatchException(name);
                    match = method;
                }
            if (match == null) throw new MissingMethodException(value.GetType().FullName, name);
            return match;
        }

        private static object Call(object target, string name, object[] args) { return Invoke(Method(target, name, args.Length), target, args); }
        private static object Invoke(MethodInfo method, object target, object[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) { throw Unwrap(error); }
        }

        private static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }

        private static Dictionary<string, object> Map(params object[] pairs)
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) value[(string)pairs[i]] = pairs[i + 1];
            return value;
        }
    }
}
