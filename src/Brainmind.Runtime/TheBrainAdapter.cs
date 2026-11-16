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
