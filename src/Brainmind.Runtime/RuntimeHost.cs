using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Brainmind.Runtime
{
    internal static class UiDispatch
    {
        internal static Type ApplicationType;
        internal static Task<T> Invoke<T>(Func<T> callback)
        {
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action action = delegate
            {
                try { completion.SetResult(callback()); }
                catch (Exception error) { completion.SetException(error); }
            };
            if (!(bool)ApplicationType.GetProperty("InvokeRequired").GetValue(null, null)) action();
            else ApplicationType.GetMethod("Invoke", new Type[] { typeof(Action), typeof(bool) }).Invoke(null, new object[] { action, true });
            return completion.Task;
        }
        internal static void Post(Action callback)
        {
            Invoke<object>(delegate { callback(); return null; });
        }
    }

    internal static class RuntimeHost
    {
        internal const string Version = "0.3.0";
        private const string DesktopNs = "TheBrain.__x_xxxx____xx_xxx_xx.";
        internal static readonly string SessionDirectory = Environment.GetEnvironmentVariable("BRAINMIND_SESSION_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brainmind", "sessions");
        internal static readonly string ConfigPath = Environment.GetEnvironmentVariable("BRAINMIND_CONFIG_PATH") ?? ModelConfig.DefaultPath;
        internal static readonly int ProcessId = Process.GetCurrentProcess().Id;
        internal static readonly string PipeName = "Brainmind-" + ProcessId;
        private static readonly Dictionary<object, WindowConversation> windows = new Dictionary<object, WindowConversation>();
        private static Type windowType;
        private static bool serverStarted;

        internal static void Start()
        {
            try
            {
                Directory.CreateDirectory(SessionDirectory);
                while (true)
                {
                    try
                    {
                        Assembly desktop = FindLoaded("TheBrainDesktop");
                        Assembly xwt = FindLoaded("Xwt");
                        if (desktop != null && xwt != null)
                        {
                            if (FileVersionInfo.GetVersionInfo(desktop.Location).FileVersion != "14.0.102.0")
                                throw new InvalidOperationException("This Brainmind build requires TheBrain 14.0.102.0.");
                            Type bootstrap = desktop.GetType(DesktopNs + "__xx_x___xx_xxx_x____x", true);
                            if ((bool)GetStatic(bootstrap, "____xxxxxxxxx_x__xxxxx"))
                            {
                                UiDispatch.ApplicationType = xwt.GetType("Xwt.Application", true);
                                windowType = desktop.GetType(DesktopNs + "_x___x__x_xxx_xxx__x__xx", true);
                                UiDispatch.Invoke<object>(delegate { ScanWindows(); return null; }).GetAwaiter().GetResult();
                                if (!serverStarted)
                                {
                                    serverStarted = true;
                                    PipeServer.Start();
                                }
                                PublishSession();
                            }
                        }
                    }
                    catch (Exception error) { Log("startup_or_refresh_error", Unwrap(error).Message); }
                    Thread.Sleep(1000);
                }
            }
            catch (Exception error) { Log("runtime_stopped", Unwrap(error).Message); }
        }

        private static void ScanWindows()
        {
            IEnumerable open = GetStatic(windowType, "___xx__xxx_x____xx___x_") as IEnumerable;
            if (open == null) return;
            List<object> alive = new List<object>();
            foreach (object window in open)
            {
                alive.Add(window);
                WindowConversation conversation;
                if (!windows.TryGetValue(window, out conversation))
                {
                    conversation = new WindowConversation(window);
                    windows.Add(window, conversation);
                    WindowConversation captured = conversation;
                    try
                    {
                        conversation.Panel = ChatPanel.Attach(window,
                            delegate(string prompt) { return captured.SendAsync(prompt, false); },
                            delegate { captured.Cancel(); }, LoadSettings, SaveSettings);
                        conversation.Panel.ConfigureAttachments(captured.PrepareAttachmentsAsync, captured.RemoveAttachment);
                        conversation.Panel.ConfigureMapAction(delegate(string prompt) { return captured.SendAsync(prompt, false, true); });
                        Log("panel_attached", "A native chat panel was attached.");
                    }
                    catch (Exception error)
                    {
                        windows.Remove(window);
                        Log("panel_attach_error", Unwrap(error).Message);
                        continue;
                    }
                }
                if (conversation.Panel != null)
                {
                    try
                    {
                        if (Object.ReferenceEquals(window, CurrentWindow()))
                        {
                            Dictionary<string, object> context = TheBrainAdapter.GetContext();
                            conversation.Panel.UpdateContext(SessionLabel(context));
                        }
                    }
                    catch { conversation.Panel.UpdateContext("请先打开脑图并选中节点"); }
                }
            }
            List<object> removed = new List<object>();
            foreach (object known in windows.Keys) if (!alive.Contains(known)) removed.Add(known);
            foreach (object closed in removed)
            {
                windows[closed].Close();
                windows.Remove(closed);
            }
        }

        internal static async Task<object> HandleRequest(JsonElement request)
        {
            string operation = request.GetProperty("operation").GetString();
            if (operation == "ping")
                return new { success = true, protocol_version = 1, runtime_version = Version, process_id = ProcessId, target_version = "14.0.102.0" };
            if (operation == "get_context")
                return await UiDispatch.Invoke<object>(delegate { return TheBrainAdapter.GetContext(); }).ConfigureAwait(false);
            if (operation == "read_thought")
            {
                string id = request.GetProperty("id").GetString();
                return await UiDispatch.Invoke<object>(delegate { return TheBrainAdapter.ReadThought(id); }).ConfigureAwait(false);
            }
            if (operation == "attachments")
            {
                List<string> paths = new List<string>();
                foreach (JsonElement path in request.GetProperty("paths").EnumerateArray()) paths.Add(path.GetString());
                Task<ChatFileInfo[]> pending = await UiDispatch.Invoke<Task<ChatFileInfo[]>>(delegate
                {
                    return CurrentConversation().Panel.AddFilesAsync(paths.ToArray());
                }).ConfigureAwait(false);
                ChatFileInfo[] result = await pending.ConfigureAwait(false);
                return new { success = true, files = result };
            }
            if (operation == "list_attachments" || operation == "remove_attachment" || operation == "clear_attachments" || operation == "read_attachment")
            {
                return await UiDispatch.Invoke<object>(delegate
                {
                    WindowConversation current = CurrentConversation();
                    if (operation == "remove_attachment") current.Panel.RemoveFile(request.GetProperty("id").GetString());
                    if (operation == "clear_attachments") current.Panel.ClearFiles();
                    List<AttachmentSnapshot> snapshots = current.SelectedAttachments();
                    if (operation == "read_attachment")
                        return AttachmentContext.ReadPages(snapshots, request.GetProperty("attachment_id").GetString(), request.GetProperty("page_start").GetInt32(), request.GetProperty("page_end").GetInt32());
                    return new { success = true, attachments = AttachmentContext.Describe(snapshots) };
                }).ConfigureAwait(false);
            }
            if (operation == "panel")
            {
                JsonElement panelAction;
                string action = request.TryGetProperty("action", out panelAction) ? panelAction.GetString() : "status";
                return await UiDispatch.Invoke<object>(delegate
                {
                    ScanWindows();
                    object owner = CurrentWindow();
                    WindowConversation item;
                    if (owner == null || !windows.TryGetValue(owner, out item) || item.Panel == null)
                        throw new InvalidOperationException("当前窗口尚未连接聊天面板。");
                    if (action == "show") item.Panel.Show();
                    else if (action == "hide") item.Panel.Detach();
                    else if (action == "collapse") item.Panel.SetCollapsed(true);
                    else if (action == "expand") { item.Panel.Show(); item.Panel.SetCollapsed(false); }
                    else if (action != "status") throw new InvalidOperationException("Unsupported panel action.");
                    object content = owner.GetType().GetProperty("Content").GetValue(owner, null);
                    return new { attached = item.Panel.IsAttached, collapsed = item.Panel.IsCollapsed, busy = item.Panel.IsBusy,
                        window_content_type = content == null ? null : content.GetType().FullName, menu_entry = item.Panel.HasMenuEntry,
                        embedded_in_original_window = item.Panel.IsAttached && content is Xwt.HPaned };
                }).ConfigureAwait(false);
            }
            if (operation == "get_settings")
            {
                ModelConfig config = ModelConfig.Load(ConfigPath);
                return new { base_url = config.BaseUrl, model = config.Model, api_key_configured = !String.IsNullOrEmpty(config.ApiKey), timeout_seconds = config.TimeoutSeconds };
            }
            if (operation == "set_settings")
            {
                JsonElement settings = request.GetProperty("settings");
                ModelConfig config = ModelConfig.Load(ConfigPath);
                JsonElement value;
                if (settings.TryGetProperty("base_url", out value)) config.BaseUrl = value.GetString();
                if (settings.TryGetProperty("model", out value)) config.Model = value.GetString();
                if (settings.TryGetProperty("api_key", out value)) config.ApiKey = value.GetString();
                if (settings.TryGetProperty("timeout_seconds", out value)) config.TimeoutSeconds = value.GetInt32();
                config.Save(ConfigPath);
                await UiDispatch.Invoke<object>(delegate
                {
                    foreach (WindowConversation conversation in windows.Values)
                        if (conversation.Panel != null) conversation.Panel.ReloadConfiguration();
                    return null;
                }).ConfigureAwait(false);
                return new { success = true, api_key_configured = !String.IsNullOrEmpty(config.ApiKey) };
            }
            if (operation == "get_diagnostics")
                return await UiDispatch.Invoke<object>(delegate { return CurrentConversation().Diagnostics(); }).ConfigureAwait(false);
            if (operation == "chat" || operation == "create_map_from_sources")
            {
                JsonElement promptValue;
                string prompt = request.TryGetProperty("text", out promptValue) ? promptValue.GetString() : "";
                Task<string> pending = await UiDispatch.Invoke<Task<string>>(delegate
                {
                    object owner = CurrentWindow();
                    if (owner == null) throw new InvalidOperationException("请先打开脑图窗口。");
                    ScanWindows();
                    if (windows[owner].Panel == null) throw new InvalidOperationException("聊天面板尚未连接。");
                    return operation == "create_map_from_sources" ? windows[owner].Panel.SubmitMapAsync(prompt) : windows[owner].Panel.SubmitAsync(prompt);
                }).ConfigureAwait(false);
                string reply = await pending.ConfigureAwait(false);
                return new { success = true, reply = reply };
            }
            if (operation == "cancel")
            {
                await UiDispatch.Invoke<object>(delegate
                {
                    WindowConversation c;
                    object owner = CurrentWindow();
                    if (owner != null && windows.TryGetValue(owner, out c))
                    {
                        if (c.Panel != null) c.Panel.RequestCancel();
                        else c.Cancel();
                    }
                    return null;
                }).ConfigureAwait(false);
                return new { success = true };
            }
            if (operation == "execute")
            {
                string tool = request.GetProperty("tool").GetString();
                JsonElement arguments = request.GetProperty("arguments").Clone();
                string token = request.GetProperty("expected_context_token").GetString();
                return await UiDispatch.Invoke<object>(delegate { return TheBrainAdapter.Execute(tool, arguments, token); }).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Unsupported Brainmind operation.");
        }

        private static Dictionary<string, string> LoadSettings()
        {
            ModelConfig config = ModelConfig.Load(ConfigPath);
            return new Dictionary<string, string> { { "base_url", config.BaseUrl ?? "" }, { "model", config.Model ?? "" }, { "api_key", config.ApiKey ?? "" } };
        }

        private static WindowConversation CurrentConversation()
        {
            ScanWindows();
            object owner = CurrentWindow();
            WindowConversation conversation;
            if (owner == null || !windows.TryGetValue(owner, out conversation) || conversation.Panel == null)
                throw new InvalidOperationException("当前窗口尚未连接聊天面板。");
            return conversation;
        }

        private static void SaveSettings(Dictionary<string, string> values)
        {
            ModelConfig config = ModelConfig.Load(ConfigPath);
            config.BaseUrl = values["base_url"];
            config.Model = values["model"];
            config.ApiKey = values["api_key"];
            config.Save(ConfigPath);
        }

        internal static object CurrentWindow()
        {
            return windowType == null ? null : GetStatic(windowType, "_x_xxx____xxxx____xxx_");
        }

        internal static string SessionLabel(Dictionary<string, object> context)
        {
            object brain;
            object thought;
            if (context.TryGetValue("brain_name", out brain) && context.TryGetValue("active_thought_name", out thought))
                return Convert.ToString(brain) + " · " + Convert.ToString(thought);
            return "当前脑图已连接";
        }

        private static void PublishSession()
        {
            AtomicJson(Path.Combine(SessionDirectory, ProcessId + ".json"), new
            {
                protocol_version = 1, runtime_version = Version, process_id = ProcessId,
                pipe_name = PipeName, target_version = "14.0.102.0", state = "ready",
                heartbeat_utc = DateTime.UtcNow.ToString("o"), model_config_path = ConfigPath
            });
        }

        internal static void AtomicJson(string path, object value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }

        internal static void Log(string kind, string message)
        {
            try
            {
                Directory.CreateDirectory(SessionDirectory);
                File.AppendAllText(Path.Combine(SessionDirectory, "runtime." + ProcessId + ".jsonl"), JsonSerializer.Serialize(new { utc = DateTime.UtcNow.ToString("o"), kind = kind, message = message }) + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static Assembly FindLoaded(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) if (assembly.GetName().Name == name) return assembly;
            return null;
        }
        private static object GetStatic(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null, null);
        }
        private static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }

        private sealed class WindowConversation
        {
            internal readonly object Owner;
            internal ChatPanel Panel;
            private readonly object gate = new object();
            private CancellationTokenSource cancellation;
            private ModelClient client;
            private string clientConfig;
            private readonly Dictionary<string,AttachmentSnapshot> attachments = new Dictionary<string,AttachmentSnapshot>(StringComparer.Ordinal);
            private readonly SemaphoreSlim prepareGate = new SemaphoreSlim(1,1);
            private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            private bool closed;
            private Dictionary<string,object> lastRun = new Dictionary<string,object> { {"state","idle"} };
            internal object Diagnostics() { lock(gate) { return new Dictionary<string,object>(lastRun); } }

            internal WindowConversation(object owner) { Owner = owner; }
            internal void Cancel() { lock (gate) { if (cancellation != null) cancellation.Cancel(); } }

            internal void Close()
            {
                Cancel();
                lock (gate) { closed = true; attachments.Clear(); lifetime.Cancel(); }
                if (client != null) { client.Dispose(); client = null; }
            }

            internal void RemoveAttachment(string id)
            {
                lock (gate) { if (id != null) attachments.Remove(id); }
            }

            internal List<AttachmentSnapshot> SelectedAttachments()
            {
                string[] ids = Panel == null ? new string[0] : Panel.GetReadyAttachmentIds();
                List<AttachmentSnapshot> selected = new List<AttachmentSnapshot>();
                lock (gate)
                {
                    foreach (string id in ids)
                    {
                        AttachmentSnapshot item;
                        if (!attachments.TryGetValue(id,out item)) throw new InvalidOperationException("已选附件快照不存在，请移除后重新选择。");
                        selected.Add(item);
                    }
                    return AttachmentContext.CloneSnapshots(selected);
                }
            }

            internal async Task<ChatFileInfo[]> PrepareAttachmentsAsync(string[] paths, CancellationToken token)
            {
                if (paths == null) paths = new string[0];
                if (paths.Length > 5)
                {
                    List<ChatFileInfo> rejected = new List<ChatFileInfo>();
                    foreach (string path in paths) rejected.Add(FileError(path,"每次最多选择5个文件。"));
                    return rejected.ToArray();
                }
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token))
                {
                    await prepareGate.WaitAsync(linked.Token).ConfigureAwait(false);
                    List<string> created = new List<string>();
                    try
                    {
                        List<ChatFileInfo> result = new List<ChatFileInfo>();
                        foreach (string path in paths)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            try
                            {
                                AttachmentSnapshot parsed = await AttachmentReader.ReadAsync(path,linked.Token).ConfigureAwait(false);
                                linked.Token.ThrowIfCancellationRequested();
                                AttachmentSnapshot chosen = parsed;
                                lock (gate)
                                {
                                    if (closed) throw new OperationCanceledException(linked.Token);
                                    long total = 0;
                                    foreach (AttachmentSnapshot existing in attachments.Values)
                                    {
                                        total += existing.SizeBytes;
                                        if (existing.Sha256 == parsed.Sha256) chosen = existing;
                                    }
                                    if (Object.ReferenceEquals(chosen,parsed))
                                    {
                                        if (attachments.Count >= 5) throw new InvalidOperationException("最多保留5个附件，请先移除一些文件。");
                                        if (parsed.SizeBytes < 0 || total + parsed.SizeBytes > 40L*1024*1024) throw new InvalidOperationException("附件原始大小合计不能超过40MiB，请先移除一些文件。");
                                        attachments.Add(parsed.Id,parsed);
                                        created.Add(parsed.Id);
                                    }
                                }
                                result.Add(new ChatFileInfo { Id=chosen.Id, Name=chosen.FileName, SizeBytes=chosen.SizeBytes,
                                    State="ready", Message=(chosen.Kind=="pdf" ? "PDF共"+chosen.TotalPages+"页，已读取"+chosen.Pages.Count+"页。 "
                                        : "文本共"+chosen.TotalPages+"段，已读取"+chosen.Pages.Count+"段。 ")+(chosen.Notice ?? "") });
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                // AttachmentReader deliberately exposes only
                                // bounded, path-free user-facing errors.
                                string message = error is InvalidOperationException
                                    ? error.Message : "读取失败：文件格式不支持、文件损坏或加密，或超过读取限制。请检查后重选。";
                                result.Add(FileError(path,message));
                            }
                        }
                        return result.ToArray();
                    }
                    catch (OperationCanceledException)
                    {
                        lock (gate) { foreach (string id in created) attachments.Remove(id); }
                        throw;
                    }
                    finally { prepareGate.Release(); }
                }
            }

            private static ChatFileInfo FileError(string path,string message)
            {
                string name;
                try { name = Path.GetFileName(path ?? ""); } catch { name="文件"; }
                return new ChatFileInfo { Id=Guid.NewGuid().ToString("N"),Name=name,SizeBytes=0,State="error",Message=message };
            }

            internal async Task<string> SendAsync(string prompt, bool display, bool requireMap = false)
            {
                if (String.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("请输入要讨论或编辑的内容。");
                CancellationTokenSource cancel;
                lock (gate)
                {
                    if (cancellation != null) throw new InvalidOperationException("上一条请求仍在进行，可先取消。");
                    cancellation = new CancellationTokenSource(); cancel = cancellation;
                }
                string runId = Guid.NewGuid().ToString("N");
                string started = DateTime.UtcNow.ToString("o");
                Stopwatch elapsed = Stopwatch.StartNew();
                int requests = 0, tools = 0, createdNodes = 0, createdLinks = 0;
                string failure = "", rootId = null;
                bool mapAttempted = false, mapSucceeded = false;
                Action<string> publish = delegate(string state)
                {
                    lock(gate) { lastRun = new Dictionary<string,object> {
                        {"state",state}, {"run_id",runId}, {"mode",requireMap ? "create_map" : "chat"},
                        {"started_utc",started}, {"elapsed_ms",elapsed.ElapsedMilliseconds},
                        {"model_requests",requests}, {"tool_calls",tools}, {"created_nodes",createdNodes},
                        {"created_links",createdLinks}, {"error_type",failure} }; }
                };
                publish("running"); Log("run_started",runId + ":" + (requireMap ? "create_map" : "chat"));
                try
                {
                    List<AttachmentSnapshot> selected = null;
                    Dictionary<string,object> context = await UiDispatch.Invoke<Dictionary<string,object>>(delegate
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        if (!Object.ReferenceEquals(Owner,CurrentWindow())) throw new ToolCallAbortedException("请先激活此脑图窗口后再发送。");
                        selected = SelectedAttachments();
                        return TheBrainAdapter.GetContext();
                    }).ConfigureAwait(false);
                    if (requireMap && selected.Count == 0) throw new InvalidOperationException("请先添加并读完要生成脑图的资料。");
                    if (!context.ContainsKey("context_token") || String.IsNullOrEmpty(Convert.ToString(context["context_token"])))
                        throw new InvalidOperationException("请先打开脑图并选中节点，等待笔记加载后再发送。");
                    string expected = Convert.ToString(context["context_token"]);
                    Dictionary<string,object> previousNativeContext = context;
                    ModelConfig config = ModelConfig.Load(ConfigPath);
                    string identity = JsonSerializer.Serialize(new { config.BaseUrl,config.Model,config.ApiKey,config.TimeoutSeconds });
                    if (client == null || clientConfig != identity)
                    {
                        if(client != null) client.Dispose();
                        client = new ModelClient(config); clientConfig = identity;
                    }
                    Func<string,JsonElement,string,Task<Dictionary<string,object>>> native = delegate(string operation,JsonElement arguments,string token)
                    {
                        return UiDispatch.Invoke<Dictionary<string,object>>(delegate
                        {
                            cancel.Token.ThrowIfCancellationRequested();
                            if (!Object.ReferenceEquals(Owner,CurrentWindow())) throw new ToolCallAbortedException("当前脑图窗口已切换，已停止后续操作。");
                            Dictionary<string,object> response = TheBrainAdapter.Execute(operation,arguments,token);
                            object next;
                            Dictionary<string,object> captured = response.TryGetValue("context",out next) ? next as Dictionary<string,object> : null;
                            object flag;
                            if(response.TryGetValue("success",out flag) && Convert.ToBoolean(flag)) previousNativeContext = captured;
                            else if(captured != null) Log("context_conflict_fields",ContextDifferences(previousNativeContext,captured));
                            return response;
                        });
                    };
                    Func<string,JsonElement,Task<object>> execute = async delegate(string tool,JsonElement args)
                    {
                        cancel.Token.ThrowIfCancellationRequested(); tools++;
                        Dictionary<string,object> result;
                        if(tool == "create_map")
                        {
                            if(mapAttempted) throw new ToolCallAbortedException("本次请求已尝试创建脑图，不会自动重复创建。");
                            mapAttempted = true;
                            if(requireMap && MapPlan.Parse(args).ParentId != Guid.Parse(Convert.ToString(context["active_thought_id"])))
                                throw new ToolCallAbortedException("生成脑图的目标必须是本次发送时的活动节点，未开始创建。");
                            result = await MapBuilder.BuildAsync(args,expected,native,cancel.Token,
                                delegate(string progress) { UiDispatch.Post(delegate { if(Panel != null) Panel.SetStatus(progress); }); }).ConfigureAwait(false);
                            createdNodes += MapCount(result,"created_nodes"); createdLinks += MapCount(result,"created_links");
                            object mapValue; Dictionary<string,object> map;
                            if(result.TryGetValue("result",out mapValue) && (map=mapValue as Dictionary<string,object>) != null && map.ContainsKey("root_id"))
                                rootId = Convert.ToString(map["root_id"]);
                        }
                        else
                        {
                            result = await UiDispatch.Invoke<Dictionary<string,object>>(delegate
                            {
                                cancel.Token.ThrowIfCancellationRequested();
                                if(!Object.ReferenceEquals(Owner,CurrentWindow())) throw new ToolCallAbortedException("当前脑图窗口已切换，已停止后续操作。");
                                if(tool == "get_context" || tool == "read_thought" || tool == "read_attachment")
                                {
                                    Dictionary<string,object> fresh = TheBrainAdapter.GetContext();
                                    if(Convert.ToString(fresh["context_token"]) != expected) throw new ToolCallAbortedException("脑图或笔记已变化，已停止后续操作。");
                                    if(tool == "get_context") return fresh;
                                    if(tool == "read_attachment") return AttachmentContext.ReadPages(selected,args.GetProperty("attachment_id").GetString(),args.GetProperty("page_start").GetInt32(),args.GetProperty("page_end").GetInt32());
                                    return TheBrainAdapter.ReadThought(args.GetProperty("id").GetString());
                                }
                                return TheBrainAdapter.Execute(tool,args,expected);
                            }).ConfigureAwait(false);
                            object okValue;
                            if(result.TryGetValue("success",out okValue) && Convert.ToBoolean(okValue))
                            {
                                if(tool == "create_thought") createdNodes++;
                                if(tool == "add_link") createdLinks++;
                            }
                        }
                        publish("running");
                        object code;
                        if(result.TryGetValue("code",out code) && Convert.ToString(code) == "context_changed")
                            throw new ToolCallAbortedException("脑图或笔记已变化，已停止后续操作；已经完成的节点会保留。",result);
                        object success; bool ok = !result.TryGetValue("success",out success) || Convert.ToBoolean(success);
                        if(tool == "create_map") mapSucceeded = ok;
                        object updated;
                        Dictionary<string,object> freshContext = result.TryGetValue("context",out updated) ? updated as Dictionary<string,object> : null;
                        if(ok && freshContext != null && freshContext.ContainsKey("context_token")) expected = Convert.ToString(freshContext["context_token"]);
                        UiDispatch.Post(delegate { if(Panel != null) Panel.AppendMessage("操作",FriendlyTool(tool) + (ok ? "：完成" : "：已停止，请核对已完成部分")); });
                        Log("tool",runId + ":" + tool + (ok ? ":ok" : ":refused"));
                        return result;
                    };
                    Task<string> responseTask = client.RunAsync(prompt,context,selected,new ModelRunOptions { RequireMapCreation=requireMap },execute,
                        delegate(string progress) {
                            if(progress.StartsWith("Requesting model",StringComparison.Ordinal)) { requests++; publish("running"); Log("model_request",runId + ":" + requests); }
                            UiDispatch.Post(delegate { if(Panel != null) Panel.SetStatus(progress); });
                        },cancel.Token);
                    // A map may already exist even if the final model summary
                    // fails. Wait without propagating that failure, perform the
                    // guarded native navigation, then report the original error.
                    await Task.WhenAny(responseTask).ConfigureAwait(false);
                    if(mapSucceeded && createdNodes > 0 && !String.IsNullOrEmpty(rootId))
                    {
                        bool focused = await UiDispatch.Invoke<bool>(delegate
                        {
                            if(cancel.IsCancellationRequested || !Object.ReferenceEquals(Owner,CurrentWindow())) return false;
                            try { TheBrainAdapter.FocusCreatedThought(rootId,expected); return true; }
                            catch { return false; }
                        }).ConfigureAwait(false);
                        UiDispatch.Post(delegate { if(Panel != null) Panel.AppendMessage("结果",focused ? "已定位到新脑图的根节点。" : "新分支已创建；当前会话有变化，保留你的当前位置。"); });
                    }
                    string answer = await responseTask.ConfigureAwait(false);
                    publish("completed");
                    if(display) UiDispatch.Post(delegate { if(Panel != null) Panel.AppendMessage("AI",answer); });
                    return answer;
                }
                catch(OperationCanceledException)
                {
                    failure="canceled"; publish("canceled"); throw;
                }
                catch(Exception error)
                {
                    failure=error.GetType().Name; publish("failed"); throw;
                }
                finally
                {
                    Log("run_finished",runId + ":" + (failure.Length == 0 ? "completed" : failure) + ":requests="+requests+":tools="+tools+":created="+createdNodes+":ms="+elapsed.ElapsedMilliseconds);
                    if(failure.Length > 0) UiDispatch.Post(delegate { if(Panel != null) Panel.AppendMessage("结果",createdNodes > 0 ? "本次已创建 "+createdNodes+" 个节点，已完成部分没有回滚，请先核对再继续。" : "本次未创建任何节点。"); });
                    lock(gate) { cancellation=null; }
                    cancel.Dispose();
                    UiDispatch.Post(delegate { if(Panel != null) Panel.SetBusy(false); });
                }
            }

            private static int MapCount(Dictionary<string,object> response,string key)
            {
                object raw; if(!response.TryGetValue("result",out raw)) return 0;
                Dictionary<string,object> result=raw as Dictionary<string,object>;
                return result != null && result.ContainsKey(key) ? Convert.ToInt32(result[key]) : 0;
            }

            private static string ContextDifferences(Dictionary<string,object> before,Dictionary<string,object> after)
            {
                List<string> differences = new List<string>();
                if(before == null) return "previous_context_unavailable";
                foreach(string key in after.Keys)
                    if(!before.ContainsKey(key) || JsonSerializer.Serialize(before[key]) != JsonSerializer.Serialize(after[key])) differences.Add(key);
                return String.Join(",",differences.ToArray());
            }

            private static string FriendlyTool(string name)
            {
                if (name == "create_map") return "生成脑图";
                if (name == "create_thought") return "创建节点";
                if (name == "rename_thought") return "修改节点名称";
                if (name == "add_link") return "添加关系";
                if (name == "replace_note") return "更新笔记";
                if (name == "read_attachment") return "读取附件资料";
                return "读取当前内容";
            }
        }
    }
}
