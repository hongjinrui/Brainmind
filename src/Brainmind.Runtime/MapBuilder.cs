using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Brainmind.Runtime
{
    public static class MapBuilder
    {
        // The host dispatches EACH callback separately to the UI thread. It must
        // not wrap this whole method in one UI callback or retry a failed step.
        public static async Task<Dictionary<string, object>> BuildAsync(JsonElement args, string initialToken,
            Func<string, JsonElement, string, Task<Dictionary<string, object>>> executeNative,
            CancellationToken token, Action<string> progress)
        {
            BuildState state = new BuildState();
            state.Token = initialToken;
            state.Progress = progress;
            if (token.IsCancellationRequested) return state.Fail("canceled", "创建已取消。", null, false);
            if (String.IsNullOrWhiteSpace(initialToken)) return state.Fail("context_changed", "缺少本轮上下文 token，未创建任何节点。", null, false);
            try { state.Plan = MapPlan.Parse(args); }
            catch (Exception error) { return state.Fail("validation_error", error.Message, null, false); }
            if (executeNative == null) return state.Fail("validation_error", "原生操作回调不可用。", null, false);

            foreach (MapPlanNode node in state.Plan.Nodes)
            {
                Dictionary<string, object> step = Map("index", state.CompletedSteps + 1, "operation", "create_thought", "key", node.Key);
                if (token.IsCancellationRequested) return state.Fail("canceled", "创建已取消，已完成的节点保留。", step, false);
                string parentId = node.ParentKey == null ? state.Plan.ParentId.ToString() : state.Ids[node.ParentKey];
                Dictionary<string, object> receipt;
                try
                {
                    receipt = await executeNative("create_thought", Arguments(Map("parent_id", parentId, "name", node.Name)), state.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return state.Fail("canceled", "创建已取消；最后一次原生调用没有回执，请检查已创建的分支。", step, true);
                }
                catch (Exception error)
                {
                    return state.Fail("native_error", "原生创建没有完成回执：" + error.Message, step, true);
                }
                Dictionary<string, object> refusal = state.Refusal(receipt, step);
                if (refusal != null) return refusal;
                Dictionary<string, object> result = Nested(receipt, "result");
                Guid id;
                string idValue = Value(result, "id");
                if (!Guid.TryParse(idValue, out id) || id == Guid.Empty || id == state.Plan.ParentId || state.SeenIds.Contains(id.ToString()))
                    return state.Fail("native_error", "原生创建回执没有可确认的唯一新节点 UUID，请检查当前分支，不要重试整批。", step, true);
                idValue = id.ToString();
                state.SeenIds.Add(idValue);
                state.Ids.Add(node.Key, idValue);
                state.Mapping.Add(Map("key", node.Key, "id", idValue, "name", Value(result, "name") ?? node.Name,
                    "parent_key", node.ParentKey, "parent_id", parentId));
                state.Relations.Add(Map("a", parentId, "b", idValue, "a_key", node.ParentKey, "b_key", node.Key, "relation", "parent"));
                state.CreatedLinks++;
                state.CompletedSteps++;
                state.MutationAttempted = true;
                if (node.Key == state.Plan.RootKey) state.RootId = idValue;
                if (!state.AcceptToken(receipt))
                    return state.Fail("native_error", "节点已创建，但原生回执缺少下一步上下文 token；已停止后续创建。", step, false);
                Report(progress, "正在建立新分支：已创建 " + state.Ids.Count + "/" + state.Plan.Nodes.Count + " 个节点。");
            }

            foreach (MapPlanLink link in state.Plan.Links)
            {
                Dictionary<string, object> step = Map("index", state.CompletedSteps + 1, "operation", "add_link",
                    "a_key", link.AKey, "b_key", link.BKey);
                if (token.IsCancellationRequested) return state.Fail("canceled", "创建已取消，已完成的节点与关系保留。", step, false);
                Dictionary<string, object> receipt;
                try
                {
                    receipt = await executeNative("add_link", Arguments(Map("a", state.Ids[link.AKey], "b", state.Ids[link.BKey], "relation", "jump")), state.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return state.Fail("canceled", "创建已取消；最后一次关系调用没有回执，请检查当前分支。", step, true);
                }
                catch (Exception error)
                {
                    return state.Fail("native_error", "原生关系调用没有完成回执：" + error.Message, step, true);
                }
                Dictionary<string, object> refusal = state.Refusal(receipt, step);
                if (refusal != null) return refusal;
                Dictionary<string, object> result = Nested(receipt, "result");
                bool noOp = Flag(result, "no_op", false);
                state.Relations.Add(Map("id", Value(result, "id"), "a", state.Ids[link.AKey], "b", state.Ids[link.BKey],
                    "a_key", link.AKey, "b_key", link.BKey, "relation", "jump", "no_op", noOp));
                if (!noOp) { state.CreatedLinks++; state.MutationAttempted = true; }
                state.CompletedExtraLinks++;
                state.CompletedSteps++;
                if (!state.AcceptToken(receipt))
                    return state.Fail("native_error", "关系已确认，但原生回执缺少下一步上下文 token；已停止后续操作。", step, false);
                Report(progress, "正在建立新分支：已确认 " + state.CompletedExtraLinks + "/" + state.Plan.Links.Count + " 条跳转关系。");
            }
            if (token.IsCancellationRequested) return state.Fail("canceled", "请求已取消；本批已完成的节点和关系保留。", null, false);
            Report(progress, "新分支已建立：" + state.Ids.Count + " 个节点，" + state.CreatedLinks + " 条关系。");
            return state.Response(true, null, null, null, false);
        }

        private sealed class BuildState
        {
            internal MapPlan Plan;
            internal string Token, RootId;
            internal Action<string> Progress;
            internal Dictionary<string, object> Context;
            internal readonly Dictionary<string, string> Ids = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly HashSet<string> SeenIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<Dictionary<string, object>> Mapping = new List<Dictionary<string, object>>();
            internal readonly List<Dictionary<string, object>> Relations = new List<Dictionary<string, object>>();
            internal int CompletedSteps, CompletedExtraLinks, CreatedLinks;
            internal bool MutationAttempted;

            internal bool AcceptToken(Dictionary<string, object> receipt)
            {
                Dictionary<string, object> context = Nested(receipt, "context");
                if (context != null) Context = context;
                string next = Value(context, "context_token");
                if (String.IsNullOrWhiteSpace(next)) return false;
                Token = next;
                return true;
            }
            internal Dictionary<string, object> Refusal(Dictionary<string, object> receipt, Dictionary<string, object> step)
            {
                if (receipt == null) return Fail("native_error", "原生调用没有返回回执，请检查当前分支。", step, true);
                Dictionary<string, object> context = Nested(receipt, "context");
                if (context != null) Context = context;
                bool attempted = Flag(receipt, "mutation_attempted", false);
                MutationAttempted = MutationAttempted || attempted;
                if (Flag(receipt, "success", false)) return null;
                return Fail(Value(receipt, "code") ?? "native_error", Value(receipt, "error") ?? "原生操作被拒绝，已停止后续创建。", step,
                    attempted || !receipt.ContainsKey("mutation_attempted"));
            }
            internal Dictionary<string, object> Fail(string code, string message, object failedStep, bool unverified)
            {
                MutationAttempted = MutationAttempted || unverified;
                Report(Progress, "创建已停止：已确认创建 " + Ids.Count + " 个节点；已完成的操作不会自动回滚。");
                return Response(false, code, message, failedStep, unverified);
            }
            internal Dictionary<string, object> Response(bool success, string code, string error, object failedStep, bool unverified)
            {
                List<string> pending = new List<string>();
                if (Plan != null) foreach (MapPlanNode node in Plan.Nodes) if (!Ids.ContainsKey(node.Key)) pending.Add(node.Key);
                bool partial = !success && (Ids.Count > 0 || unverified);
                string status = success ? "completed" : code == "canceled" ? "canceled" : partial ? "partial" : MutationAttempted ? "failed" : "rejected";
                Dictionary<string, object> result = Map("root_id", RootId, "root_key", Plan == null ? null : Plan.RootKey,
                    "parent_id", Plan == null ? null : Plan.ParentId.ToString(), "ids", Ids, "mapping", Mapping, "thoughts", Mapping,
                    "links", Relations, "created_count", Ids.Count, "created_nodes", Ids.Count, "created_links", CreatedLinks,
                    "link_count", CreatedLinks, "atomic", false, "status", status, "partial", partial, "failed_step", failedStep,
                    "pending_keys", pending, "pending_extra_links", Plan == null ? 0 : Plan.Links.Count - CompletedExtraLinks,
                    "unverified_mutation", unverified, "completed_steps", CompletedSteps,
                    "planned_nodes", Plan == null ? 0 : Plan.Nodes.Count, "planned_extra_links", Plan == null ? 0 : Plan.Links.Count);
                Dictionary<string, object> response = Map("success", success, "operation", "create_map", "result", result,
                    "context", Context, "mutation_attempted", MutationAttempted);
                if (!success) { response["code"] = code; response["error"] = error; }
                return response;
            }
        }
        private static void Report(Action<string> progress, string message) { if (progress != null) { try { progress(message); } catch { } } }
        private static JsonElement Arguments(Dictionary<string, object> values)
        {
            using (JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(values))) return json.RootElement.Clone();
        }
        private static Dictionary<string, object> Nested(Dictionary<string, object> value, string key)
        { object result; return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null; }
        private static string Value(Dictionary<string, object> value, string key)
        { object result; return value != null && value.TryGetValue(key, out result) ? Convert.ToString(result, CultureInfo.InvariantCulture) : null; }
        private static bool Flag(Dictionary<string, object> value, string key, bool fallback)
        { object result; return value != null && value.TryGetValue(key, out result) && result is bool ? (bool)result : fallback; }
        private static Dictionary<string, object> Map(params object[] pairs)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
            return result;
        }
    }
}
