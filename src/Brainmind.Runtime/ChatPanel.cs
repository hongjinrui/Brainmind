using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xwt;

namespace Brainmind.Runtime
{
    public sealed class ChatFileInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long SizeBytes { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
    }

    /// <summary>A native, removable chat pane in the original TheBrain window.</summary>
    public sealed class ChatPanel
    {
        private static readonly Dictionary<Window, ChatPanel> Panels = new Dictionary<Window, ChatPanel>();
        private readonly Window window;
        private readonly Func<string, Task<string>> send;
        private readonly Action cancel;
        private readonly Func<Dictionary<string, string>> loadConfig;
        private readonly Action<Dictionary<string, string>> saveConfig;
        private readonly StringBuilder transcript = new StringBuilder();
        private readonly VBox panel;
        private readonly ScrollView viewport;
        private readonly VBox body;
        private readonly Label title;
        private readonly Label context;
        private readonly Label status;
        private readonly ChatTextEntry history;
        private readonly ChatTextEntry input;
        private readonly TextEntry endpoint;
        private readonly TextEntry model;
        private readonly PasswordEntry apiKey;
        private readonly Button sendButton;
        private readonly Button createMapButton;
        private readonly Button cancelButton;
        private readonly Button collapseButton;
        private readonly Button saveButton;
        private readonly Expander settings;
        private readonly Button addFilesButton;
        private readonly Expander fileList;
        private readonly VBox fileRows;
        private readonly List<Button> fileRemoveButtons = new List<Button>();
        private readonly List<ChatFileInfo> files = new List<ChatFileInfo>();
        private Func<string[], CancellationToken, Task<ChatFileInfo[]>> prepareFiles;
        private Func<string, Task<string>> createMap;
        private Action<string> removeFile;
        private CancellationTokenSource preparation;
        private bool preparing;
        private int preparationGeneration;
        private Menu menuContainer;
        private MenuItem menuRoot;
        private HPaned split;
        private Widget originalContent;
        private bool attached;
        private bool windowClosed;
        private bool busy;
        private bool collapsed;
        private bool cancelRequested;
        private double expandedPanelWidth = 370;
        private bool constrainingWidth;

        private ChatPanel(Window owner, Func<string, Task<string>> sendMessage, Action cancelMessage,
            Func<Dictionary<string, string>> readConfiguration, Action<Dictionary<string, string>> writeConfiguration)
        {
            RequireUiThread();
            if (sendMessage == null) throw new ArgumentNullException("sendMessage");
            window = owner;
            send = sendMessage;
            cancel = cancelMessage;
            loadConfig = readConfiguration;
            saveConfig = writeConfiguration;

            panel = new VBox();
            panel.Name = "Brainmind_ChatPanel";
            panel.Spacing = 8;
            panel.Margin = 10;
            // The outer viewport owns the pane's width and minimum height.
            // Let this content wrap inside the available scrolling area.
            panel.MinWidth = 0;
            panel.ExpandHorizontal = true;
            panel.ExpandVertical = true;
            HBox header = new HBox();
            title = new Label("Brainmind AI");
            title.Name = "Brainmind_Title";
            title.TextAlignment = Alignment.Start;
            header.PackStart(title, true);
            collapseButton = new Button("收起");
            collapseButton.Name = "Brainmind_Collapse";
            collapseButton.Clicked += delegate { SetCollapsed(!collapsed); };
            header.PackEnd(collapseButton);
            Button closeButton = new Button("关闭");
            closeButton.Name = "Brainmind_Close";
            closeButton.TooltipText = "隐藏聊天面板，可从 Brainmind 菜单重新打开";
            closeButton.Clicked += delegate { Detach(); };
            header.PackEnd(closeButton);
            panel.PackStart(header);

            body = new VBox();
            body.Spacing = 8;
            body.ExpandHorizontal = true;
            body.ExpandVertical = true;
            context = new Label("等待打开脑图");
            context.Name = "Brainmind_Context";
            context.Wrap = WrapMode.Word;
            context.TextAlignment = Alignment.Start;
            body.PackStart(context);

            settings = new Expander();
            settings.Name = "Brainmind_Settings";
            settings.Label = "模型设置";
            VBox configuration = new VBox();
            configuration.Spacing = 5;
            configuration.PackStart(new Label("API 地址"));
            endpoint = new TextEntry();
            endpoint.Name = "Brainmind_Endpoint";
            endpoint.PlaceholderText = "https://服务地址/v1";
            configuration.PackStart(endpoint);
            configuration.PackStart(new Label("模型"));
            model = new TextEntry();
            model.Name = "Brainmind_Model";
            model.PlaceholderText = "模型名称";
            configuration.PackStart(model);
            configuration.PackStart(new Label("API 密钥"));
            apiKey = new PasswordEntry();
            apiKey.Name = "Brainmind_ApiKey";
            apiKey.PlaceholderText = "仅保存在本机";
            configuration.PackStart(apiKey);
            saveButton = new Button("保存设置");
            saveButton.Name = "Brainmind_SaveSettings";
            saveButton.Clicked += delegate { SaveConfiguration(); };
            configuration.PackStart(saveButton);
            settings.Content = configuration;
            body.PackStart(settings);

            history = new ChatTextEntry();
            history.Name = "Brainmind_History";
            history.MultiLine = true;
            history.ReadOnly = true;
            history.MinHeight = 64;
            history.HeightRequest = 120;
            history.ExpandVertical = true;
            history.ExpandHorizontal = true;
            history.TooltipText = "对话记录";
            body.PackStart(history, true);

            addFilesButton = new Button("添加文件");
            addFilesButton.Name = "Brainmind_AddFiles";
            addFilesButton.TooltipText = "选择 PDF、TXT、Markdown 或 CSV，供当前聊天阅读";
            addFilesButton.Sensitive = false;
            addFilesButton.Clicked += delegate { ChooseFiles(); };
            body.PackStart(addFilesButton);
            fileRows = new VBox();
            fileRows.Name = "Brainmind_FileRows";
            fileRows.Spacing = 5;
            fileRows.WidthRequest = 325;
            ScrollView fileScroll = new ScrollView(fileRows);
            fileScroll.Name = "Brainmind_FileScroll";
            fileScroll.WidthRequest = 330;
            fileScroll.HeightRequest = 170;
            fileScroll.MinHeight = 100;
            fileScroll.HorizontalScrollPolicy = ScrollPolicy.Never;
            fileScroll.VerticalScrollPolicy = ScrollPolicy.Automatic;
            fileList = new Expander();
            fileList.Name = "Brainmind_Files";
            fileList.Label = "聊天文件 (0/5)";
            fileList.Content = fileScroll;
            fileList.Visible = false;
            body.PackStart(fileList);

            input = new ChatTextEntry();
            input.Name = "Brainmind_Input";
            input.MultiLine = true;
            input.HeightRequest = 92;
            input.MinHeight = 64;
            input.PlaceholderText = "输入要求；Ctrl+Enter 发送，Enter 换行";
            input.ExpandHorizontal = true;
            input.KeyPressed += OnInputKeyPressed;
            body.PackStart(input);

            HBox actions = new HBox();
            sendButton = new Button("发送");
            sendButton.Name = "Brainmind_Send";
            sendButton.Clicked += delegate { BeginSendFromInput(); };
            actions.PackStart(sendButton, true);
            createMapButton = new Button("生成脑图");
            createMapButton.Name = "Brainmind_CreateMap";
            createMapButton.TooltipText = "依据已选资料归纳结构，在当前节点下实际创建脑图分支；输入框可补充要求";
            createMapButton.Sensitive = false;
            createMapButton.Clicked += delegate { BeginMapFromInput(); };
            actions.PackStart(createMapButton);
            cancelButton = new Button("取消");
            cancelButton.Name = "Brainmind_Cancel";
            cancelButton.Sensitive = false;
            cancelButton.Clicked += delegate { RequestCancel(); };
            actions.PackEnd(cancelButton);
            body.PackStart(actions);
            status = new Label("就绪");
            status.Name = "Brainmind_Status";
            status.Wrap = WrapMode.Word;
            status.TextAlignment = Alignment.Start;
            body.PackStart(status);
            panel.PackStart(body, true);
            viewport = new ScrollView(panel);
            viewport.Name = "Brainmind_ChatViewport";
            viewport.WidthRequest = expandedPanelWidth;
            // WPF treats a natural height of zero as unspecified. One logical
            // pixel prevents the content's height becoming the host minimum.
            viewport.HeightRequest = 1;
            viewport.MinHeight = 0;
            viewport.MinWidth = 330;
            viewport.ExpandHorizontal = true;
            viewport.ExpandVertical = true;
            viewport.BorderVisible = false;
            viewport.HorizontalScrollPolicy = ScrollPolicy.Never;
            viewport.VerticalScrollPolicy = ScrollPolicy.Automatic;
            viewport.BoundsChanged += delegate
            {
                ConstrainPanelWidth();
                if (windowClosed || collapsed || viewport.Size.Height <= 0) return;
                double height = Math.Max(80, Math.Min(240, viewport.Size.Height * 0.28));
                if (Math.Abs(history.HeightRequest - height) >= 1) history.HeightRequest = height;
            };
            viewport.VisibleRectChanged += delegate { ConstrainPanelWidth(); };
            ConstrainPanelWidth();
            try { ReloadConfiguration(); }
            catch (Exception error)
            {
                settings.Expanded = true;
                status.Text = "设置读取失败：" + error.Message;
            }
            window.Closed += OnWindowClosed;
        }

        public static ChatPanel Attach(object owner, Func<string, Task<string>> sendMessage, Action cancelMessage,
            Func<Dictionary<string, string>> readConfiguration, Action<Dictionary<string, string>> writeConfiguration)
        {
            RequireUiThread();
            Window nativeWindow = owner as Window;
            if (nativeWindow == null) throw new ArgumentException("A native Xwt window is required", "owner");
            ChatPanel existing;
            if (Panels.TryGetValue(nativeWindow, out existing)) return existing;
            ChatPanel created = new ChatPanel(nativeWindow, sendMessage, cancelMessage, readConfiguration, writeConfiguration);
            try { created.Show(); }
            catch { nativeWindow.Closed -= created.OnWindowClosed; throw; }
            Panels.Add(nativeWindow, created);
            return created;
        }

        public static ChatPanel Find(object owner)
        {
            RequireUiThread();
            Window nativeWindow = owner as Window;
            ChatPanel value;
            return nativeWindow != null && Panels.TryGetValue(nativeWindow, out value) ? value : null;
        }

        public bool IsAttached { get { return attached && !windowClosed; } }
        public bool IsBusy { get { return busy || preparing; } }
        public bool IsCollapsed { get { return collapsed; } }
        public bool HasMenuEntry { get { return menuContainer != null && menuRoot != null && menuContainer.Items.Contains(menuRoot); } }

        public void ConfigureAttachments(Func<string[], CancellationToken, Task<ChatFileInfo[]>> prepare, Action<string> remove)
        {
            UiResult<object>(delegate
            {
                if (preparing) throw new InvalidOperationException("文件正在读取，不能更换读取器");
                prepareFiles = prepare;
                removeFile = remove;
                RefreshAvailability();
                return null;
            });
        }

        public string[] GetReadyAttachmentIds()
        {
            return UiResult<string[]>(delegate
            {
                if (preparing) throw new InvalidOperationException("文件正在读取，请等待完成后发送");
                List<string> ids = new List<string>();
                foreach (ChatFileInfo file in files)
                    if (file.State == "ready" && !String.IsNullOrEmpty(file.Id) && !ids.Contains(file.Id)) ids.Add(file.Id);
                return ids.ToArray();
            });
        }

        public Task<ChatFileInfo[]> AddFilesAsync(string[] paths)
        {
            TaskCompletionSource<ChatFileInfo[]> completion = new TaskCompletionSource<ChatFileInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            string[] selected = paths == null ? new string[0] : (string[])paths.Clone();
            Ui(delegate { StartPreparingFiles(selected, completion); });
            return completion.Task;
        }

        public void RemoveFile(string id)
        {
            UiResult<object>(delegate
            {
                if (busy || preparing) throw new InvalidOperationException("请先取消或等待当前请求完成，再移除文件");
                for (int index = files.Count - 1; index >= 0; index--)
                {
                    if (files[index].Id != id) continue;
                    if (files[index].State == "reading") throw new InvalidOperationException("文件正在读取，请先取消读取");
                    if (removeFile != null) removeFile(files[index].Id);
                    files.RemoveAt(index);
                }
                RenderFiles();
                return null;
            });
        }

        public void ClearFiles()
        {
            UiResult<object>(delegate
            {
                if (busy) throw new InvalidOperationException("请先取消或等待当前请求完成，再清空文件");
                ClearFilesCore(true);
                return null;
            });
        }

        private void ClearFilesCore(bool render)
        {
            preparationGeneration++;
            if (preparation != null) preparation.Cancel();
            foreach (ChatFileInfo file in files)
                if (file.State != "reading" && removeFile != null) removeFile(file.Id);
            files.Clear();
            if (render && !windowClosed) RenderFiles();
        }

        private void ChooseFiles()
        {
            if (busy || preparing || windowClosed) return;
            try
            {
                using (OpenFileDialog dialog = new OpenFileDialog("添加聊天文件"))
                {
                    dialog.Multiselect = true;
                    dialog.Filters.Add(new FileDialogFilter("支持的文件", "*.pdf", "*.txt", "*.md", "*.csv"));
                    dialog.Filters.Add(new FileDialogFilter("PDF", "*.pdf"));
                    dialog.Filters.Add(new FileDialogFilter("文本与表格", "*.txt", "*.md", "*.csv"));
                    if (dialog.Run(window))
                        AddFilesAsync(dialog.FileNames).ContinueWith(delegate(Task<ChatFileInfo[]> failed)
                        {
                            failed.Exception.Handle(delegate(Exception error) { return true; });
                        }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception error) { status.Text = "文件未添加：" + error.Message; }
        }

        private async void StartPreparingFiles(string[] paths, TaskCompletionSource<ChatFileInfo[]> completion)
        {
            if (windowClosed || busy || preparing || prepareFiles == null || files.Count + paths.Length > 5)
            {
                string message = windowClosed ? "聊天窗口已关闭" : busy || preparing ? "请等待当前请求完成后再添加文件" :
                    prepareFiles == null ? "文件读取功能尚未就绪" : "最多选择5个聊天文件，请先移除一些文件";
                if (!windowClosed) status.Text = message;
                completion.TrySetException(new InvalidOperationException(message));
                return;
            }
            if (paths.Length == 0) { completion.TrySetResult(new ChatFileInfo[0]); return; }
            List<string> pending = new List<string>();
            foreach (string path in paths)
            {
                string id = "reading-" + Guid.NewGuid().ToString("N");
                pending.Add(id);
                string name;
                try { name = Path.GetFileName(path ?? ""); } catch { name = "文件"; }
                files.Add(new ChatFileInfo { Id = id, Name = name, State = "reading", Message = "正在读取…" });
            }
            preparing = true;
            cancelRequested = false;
            CancellationTokenSource source = new CancellationTokenSource();
            preparation = source;
            int generation = ++preparationGeneration;
            Func<string[], CancellationToken, Task<ChatFileInfo[]>> prepare = prepareFiles;
            RenderFiles();
            fileList.Expanded = true;
            status.Text = "正在读取所选文件…";
            ChatFileInfo[] prepared = null;
            Exception failure = null;
            bool canceled = false;
            try
            {
                // No file metadata, bytes or PDF parsing is read on the UI thread.
                prepared = await Task.Run(delegate { return prepare(paths, source.Token); }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception error) { failure = error; }
            Ui(delegate
            {
                bool discarded = source.IsCancellationRequested || generation != preparationGeneration || windowClosed;
                foreach (string id in pending) files.RemoveAll(delegate(ChatFileInfo file) { return file.Id == id; });
                if (discarded || canceled)
                {
                    ReleaseUnselectedFiles(prepared);
                    canceled = true;
                }
                else if (failure == null)
                {
                    List<ChatFileInfo> accepted = new List<ChatFileInfo>();
                    long total = 0;
                    foreach (ChatFileInfo existing in files) if (existing.State == "ready") total += existing.SizeBytes;
                    foreach (ChatFileInfo returned in prepared ?? new ChatFileInfo[0])
                    {
                        if (returned == null) continue;
                        ChatFileInfo file = CopyFile(returned);
                        if (String.IsNullOrEmpty(file.Id)) file.Id = Guid.NewGuid().ToString("N");
                        bool duplicate = files.Exists(delegate(ChatFileInfo existing) { return existing.Id == file.Id; });
                        if (duplicate) { accepted.Add(file); continue; }
                        if (file.State != "ready") file.State = "error";
                        if (file.State == "ready" && (file.SizeBytes < 0 || total + file.SizeBytes > 40L * 1024 * 1024))
                        {
                            if (removeFile != null) removeFile(file.Id);
                            file.State = "error";
                            file.Message = "文件原始大小合计不能超过40MiB";
                        }
                        if (files.Count < 5)
                        {
                            files.Add(file);
                            accepted.Add(CopyFile(file));
                            if (file.State == "ready") total += file.SizeBytes;
                        }
                        else if (file.State == "ready" && removeFile != null) removeFile(file.Id);
                    }
                    prepared = accepted.ToArray();
                }
                preparing = false;
                preparation = null;
                source.Dispose();
                if (!windowClosed)
                {
                    RenderFiles();
                    status.Text = canceled ? "文件读取已取消" : failure != null ? "文件读取失败：" + failure.Message : "文件已处理，可继续提问";
                }
                if (canceled) completion.TrySetCanceled();
                else if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult(prepared ?? new ChatFileInfo[0]);
            });
        }

        private void ReleaseUnselectedFiles(ChatFileInfo[] returned)
        {
            foreach (ChatFileInfo file in returned ?? new ChatFileInfo[0])
            {
                if (file == null || file.State != "ready" || files.Exists(delegate(ChatFileInfo selected) { return selected.State == "ready" && selected.Id == file.Id; })) continue;
                try { if (removeFile != null) removeFile(file.Id); } catch { }
            }
        }

        private static ChatFileInfo CopyFile(ChatFileInfo file)
        {
            return new ChatFileInfo { Id = file.Id, Name = file.Name, SizeBytes = file.SizeBytes, State = file.State, Message = file.Message };
        }

        private void RenderFiles()
        {
            if (windowClosed) return;
            List<Widget> previous = new List<Widget>(fileRows.Children);
            fileRows.Clear();
            fileRemoveButtons.Clear();
            foreach (Widget widget in previous) widget.Dispose();
            fileList.Visible = files.Count > 0;
            fileList.Label = "聊天文件 (" + files.Count + "/5)";
            foreach (ChatFileInfo file in files)
            {
                HBox row = new HBox();
                row.Spacing = 6;
                row.WidthRequest = 325;
                VBox details = new VBox();
                details.Spacing = 2;
                details.WidthRequest = 255;
                string size = file.SizeBytes > 0 ? " · " + (file.SizeBytes / 1024.0 / 1024.0).ToString("0.##") + " MiB" : "";
                Label name = new Label((file.Name ?? "文件") + size);
                name.Wrap = WrapMode.Word;
                name.WidthRequest = 255;
                details.PackStart(name);
                string state = file.State == "ready" ? "就绪" : file.State == "reading" ? "读取中" : "未读取";
                Label description = new Label(state + (String.IsNullOrEmpty(file.Message) ? "" : " · " + file.Message));
                description.Wrap = WrapMode.Word;
                description.WidthRequest = 255;
                details.PackStart(description);
                row.PackStart(details, true);
                Button remove = new Button("移除");
                remove.WidthRequest = 54;
                remove.MinWidth = 54;
                fileRemoveButtons.Add(remove);
                string id = file.Id;
                remove.Sensitive = !busy && !preparing;
                remove.Clicked += delegate
                {
                    try { RemoveFile(id); } catch (Exception error) { status.Text = "文件未移除：" + error.Message; }
                };
                row.PackEnd(remove);
                fileRows.PackStart(row);
            }
            if (files.Count > 0)
            {
                Label hint = new Label("用于当前聊天；发送后保留，可随时移除。");
                hint.Wrap = WrapMode.Word;
                hint.WidthRequest = 315;
                fileRows.PackStart(hint);
            }
            RefreshAvailability();
        }

        /// <summary>Explicitly reopen a pane hidden by its Close button.</summary>
        public void Show()
        {
            RequireUiThread();
            if (windowClosed) throw new InvalidOperationException("The host window is closed");
            if (attached) { EnsureMenu(); return; }
            originalContent = window.Content;
            if (originalContent == null) throw new InvalidOperationException("The host window has no content yet");
            Canvas placeholder = new Canvas();
            HPaned candidate = new HPaned();
            candidate.Name = "Brainmind_WindowSplit";
            try
            {
                // This Xwt version cannot assign Window.Content=null. A temporary
                // child releases the original native parent before reparenting it.
                window.Content = placeholder;
                candidate.Panel1.Content = originalContent;
                candidate.Panel2.Content = viewport;
                candidate.Panel1.Resize = true;
                candidate.Panel1.Shrink = false;
                candidate.Panel2.Resize = false;
                candidate.Panel2.Shrink = false;
                window.Content = candidate;
                split = candidate;
                attached = true;
                split.Position = Math.Max(0, PaneContentWidth() - expandedPanelWidth);
                if (collapsed) SetCollapsed(true);
                history.EnableNativeScrolling();
                input.EnableNativeScrolling();
                EnsureMenu();
            }
            catch
            {
                if (candidate.Panel1.Content == originalContent) candidate.Panel1.Content = null;
                if (candidate.Panel2.Content == viewport) candidate.Panel2.Content = null;
                if (window.Content == candidate || window.Content == placeholder) window.Content = originalContent;
                candidate.Dispose();
                throw;
            }
            finally
            {
                placeholder.Dispose();
            }
        }

        /// <summary>Remove only the added pane and put back the exact original root.</summary>
        public void Detach()
        {
            RequireUiThread();
            if (!attached || windowClosed) return;
            if (busy) RequestCancel();
            HPaned previous = split;
            Canvas placeholder = new Canvas();
            try
            {
                if (window.Content == previous)
                {
                    window.Content = placeholder;
                    previous.Panel1.Content = null;
                    previous.Panel2.Content = null;
                    window.Content = originalContent;
                }
                else
                {
                    // A host layout replacement takes precedence. Never overwrite it.
                    previous.Panel1.Content = null;
                    previous.Panel2.Content = null;
                }
                attached = false;
                split = null;
                previous.Dispose();
            }
            finally { placeholder.Dispose(); }
        }

        public void SetCollapsed(bool value)
        {
            RequireUiThread();
            if (windowClosed) return;
            if (!collapsed && value && split != null)
                expandedPanelWidth = Math.Max(330, Math.Min(390, PaneContentWidth() - split.Position));
            collapsed = value;
            body.Visible = !value;
            title.Visible = !value;
            collapseButton.Label = value ? "展开" : "收起";
            viewport.MinWidth = value ? 92 : 330;
            viewport.WidthRequest = value ? 120 : expandedPanelWidth;
            if (split != null) split.Position = Math.Max(0, PaneContentWidth() - viewport.WidthRequest);
        }

        private double PaneContentWidth()
        {
            double width = split == null ? 0 : split.Size.Width;
            if (width <= 0) width = window.Width;
            return Math.Max(0, width - 4); // Native WPF splitter width, in logical pixels.
        }

        private void ConstrainPanelWidth()
        {
            if (windowClosed || constrainingWidth) return;
            constrainingWidth = true;
            try
            {
                // Horizontal scrolling is disabled, but WPF's custom viewport
                // still measures long text at its natural width unless the
                // content itself has a finite native width.
                object nativeScroll = NativeWidget(viewport);
                double available = 0;
                if (nativeScroll != null)
                {
                    PropertyInfo property = nativeScroll.GetType().GetProperty("ViewportWidth");
                    if (property != null) available = (double)property.GetValue(nativeScroll, null);
                }
                if (Double.IsNaN(available) || Double.IsInfinity(available) || available <= 0)
                    available = Math.Max(1, viewport.WidthRequest - 18); // Initial scrollbar allowance before measure.
                double width = Math.Max(1, available - panel.MarginLeft - panel.MarginRight);
                if (Math.Abs(panel.WidthRequest - width) >= 0.5) panel.WidthRequest = width;
                object native = NativeWidget(panel);
                if (native != null)
                {
                    PropertyInfo maximum = native.GetType().GetProperty("MaxWidth");
                    PropertyInfo requested = native.GetType().GetProperty("Width");
                    if (maximum != null) maximum.SetValue(native, width, null);
                    if (requested != null) requested.SetValue(native, width, null);
                }
            }
            finally { constrainingWidth = false; }
        }

        private static object NativeWidget(Widget value)
        {
            object backend = Toolkit.GetBackend(value);
            for (Type type = backend.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty("Widget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(backend, null);
            }
            return null;
        }

        public void UpdateContext(string description)
        {
            Ui(delegate
            {
                if (windowClosed) return;
                EnsureMenu();
                context.Text = String.IsNullOrWhiteSpace(description) ? "等待打开脑图" : description;
                ConstrainPanelWidth();
            });
        }

        /// <summary>The host may rebuild its menu when the main window changes.</summary>
        public void EnsureMenu()
        {
            RequireUiThread();
            if (windowClosed) return;
            Menu target = window.MainMenu;
            if (target == menuContainer && HasMenuEntry) return;
            try
            {
                if (menuContainer != null && menuRoot != null && menuContainer.Items.Contains(menuRoot))
                    menuContainer.Items.Remove(menuRoot);
                Menu submenu = new Menu();
                MenuItem show = new MenuItem("打开聊天面板");
                show.Clicked += delegate
                {
                    try { Show(); SetCollapsed(false); }
                    catch (Exception error) { SetStatus("面板未打开：" + error.Message); }
                };
                submenu.Items.Add(show);
                MenuItem collapse = new MenuItem("收起聊天面板");
                collapse.Clicked += delegate
                {
                    try { Show(); SetCollapsed(true); }
                    catch (Exception error) { SetStatus("面板未打开：" + error.Message); }
                };
                submenu.Items.Add(collapse);
                MenuItem hide = new MenuItem("关闭聊天面板");
                hide.Clicked += delegate
                {
                    try { Detach(); }
                    catch (Exception error) { SetStatus("面板未关闭：" + error.Message); }
                };
                submenu.Items.Add(hide);
                MenuItem next = new MenuItem("Brainmind");
                next.SubMenu = submenu;
                if (target == null)
                {
                    target = new Menu();
                    target.Items.Add(next);
                    window.MainMenu = target;
                }
                else target.Items.Add(next);
                menuRoot = next;
                menuContainer = target;
            }
            catch (Exception error)
            {
                // Menu failure must not take down the host or undo a valid pane.
                status.Text = "菜单入口暂不可用：" + error.Message;
            }
        }

        public void SetStatus(string message)
        {
            Ui(delegate { if (!windowClosed) status.Text = message ?? ""; });
        }

        public void AppendMessage(string role, string text)
        {
            Ui(delegate
            {
                if (windowClosed) return;
                string name = role == "user" ? "你" : role == "assistant" ? "AI" : "提示";
                if (transcript.Length > 0) transcript.AppendLine().AppendLine();
                transcript.Append(name).Append("：").AppendLine().Append(text ?? "");
                history.Text = transcript.ToString();
                history.ScrollToBottom();
            });
        }

        public void SetBusy(bool value)
        {
            Ui(delegate { SetBusyCore(value); });
        }

        public void ReloadConfiguration()
        {
            RequireUiThread();
            Dictionary<string, string> values = loadConfig == null ? new Dictionary<string, string>() : loadConfig();
            if (values == null) values = new Dictionary<string, string>();
            endpoint.Text = Value(values, "base_url");
            model.Text = Value(values, "model");
            apiKey.Password = Value(values, "api_key");
            settings.Expanded = String.IsNullOrWhiteSpace(endpoint.Text) || String.IsNullOrWhiteSpace(model.Text);
        }

        public Dictionary<string, string> ConfigurationValues()
        {
            RequireUiThread();
            return new Dictionary<string, string> { { "base_url", endpoint.Text.Trim() },
                { "model", model.Text.Trim() }, { "api_key", apiKey.Password ?? "" } };
        }

        public void SaveConfiguration()
        {
            RequireUiThread();
            try
            {
                if (saveConfig == null) throw new InvalidOperationException("保存设置功能尚未就绪");
                Dictionary<string, string> values = ConfigurationValues();
                if (String.IsNullOrWhiteSpace(values["base_url"]) || String.IsNullOrWhiteSpace(values["model"]))
                    throw new InvalidOperationException("请填写 API 地址和模型名称");
                saveConfig(values);
                settings.Expanded = false;
                status.Text = "设置已保存";
            }
            catch (Exception error) { status.Text = "设置未保存：" + error.Message; }
        }

        /// <summary>Shared by the Send button and command-line integration tests.</summary>
        public Task<string> SubmitAsync(string message)
        {
            TaskCompletionSource<string> completion = new TaskCompletionSource<string>();
            Ui(delegate { StartSend(message, completion, send, false); });
            return completion.Task;
        }

        public void ConfigureMapAction(Func<string, Task<string>> action)
        {
            UiResult<object>(delegate { createMap = action; RefreshAvailability(); return null; });
        }

        public Task<string> SubmitMapAsync(string message)
        {
            TaskCompletionSource<string> completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(delegate
            {
                if (createMap == null || !HasReadyFiles())
                {
                    string error = createMap == null ? "生成脑图功能尚未就绪" : "请先添加并读取至少一个资料文件";
                    if (!windowClosed) status.Text = error;
                    completion.TrySetException(new InvalidOperationException(error));
                    return;
                }
                string prompt = String.IsNullOrWhiteSpace(message)
                    ? "依据已选资料自行归纳结构，在当前节点下创建思维导图" : message;
                StartSend(prompt, completion, createMap, true);
            });
            return completion.Task;
        }

        public void RequestCancel()
        {
            RequireUiThread();
            if (preparing)
            {
                if (preparation != null) preparation.Cancel();
                cancelRequested = true;
                cancelButton.Sensitive = false;
                status.Text = "正在取消文件读取…";
                return;
            }
            if (!busy || cancelRequested) return;
            cancelRequested = true;
            cancelButton.Sensitive = false;
            status.Text = "正在取消…";
            try { if (cancel != null) cancel(); }
            catch (Exception error) { status.Text = "取消请求失败：" + error.Message; }
        }

        private void BeginSendFromInput()
        {
            if (busy || preparing || windowClosed) return;
            string message = input.Text;
            if (String.IsNullOrWhiteSpace(message)) return;
            input.Text = "";
            // Display errors in the pane and observe the button path's task as well.
            SubmitAsync(message).ContinueWith(delegate(Task<string> failed)
            {
                failed.Exception.Handle(delegate(Exception error) { return true; });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void BeginMapFromInput()
        {
            if (busy || preparing || windowClosed) return;
            string message = input.Text;
            if (createMap != null && HasReadyFiles()) input.Text = "";
            SubmitMapAsync(message).ContinueWith(delegate(Task<string> failed)
            {
                failed.Exception.Handle(delegate(Exception error) { return true; });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async void StartSend(string message, TaskCompletionSource<string> completion, Func<string, Task<string>> action, bool map)
        {
            if (windowClosed) { completion.TrySetException(new InvalidOperationException("聊天窗口已关闭")); return; }
            if (preparing) { completion.TrySetException(new InvalidOperationException("文件正在读取，请等待完成后发送")); return; }
            if (busy) { completion.TrySetException(new InvalidOperationException("上一条请求尚未完成")); return; }
            if (String.IsNullOrWhiteSpace(message)) { completion.TrySetException(new ArgumentException("请输入要求")); return; }
            cancelRequested = false;
            SetBusyCore(true);
            AppendMessage("user", map ? "生成脑图：\n" + message : message);
            status.Text = map ? "正在依据资料生成脑图…" : "正在处理…";
            string reply = null;
            Exception failure = null;
            bool wasCanceled = false;
            try
            {
                reply = await action(message);
            }
            catch (OperationCanceledException) { wasCanceled = true; }
            catch (Exception error) { failure = error; }
            Ui(delegate
            {
                SetBusyCore(false);
                if (!windowClosed)
                {
                    if (wasCanceled)
                    {
                        AppendMessage("system", "请求已取消。");
                        status.Text = "已取消";
                    }
                    else if (failure != null)
                    {
                        AppendMessage("system", "请求失败：" + failure.Message);
                        status.Text = "请求失败，可修改后重试";
                    }
                    else
                    {
                        AppendMessage("assistant", reply ?? "已完成。");
                        status.Text = "已完成";
                    }
                }
                // Clear busy before releasing callers, so their next request can
                // start immediately without racing the previous request's cleanup.
                if (wasCanceled) completion.TrySetCanceled();
                else if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult(reply);
            });
        }

        private void SetBusyCore(bool value)
        {
            if (windowClosed) return;
            busy = value;
            RefreshAvailability();
        }

        private void RefreshAvailability()
        {
            if (windowClosed) return;
            sendButton.Sensitive = !busy && !preparing;
            createMapButton.Sensitive = !busy && !preparing && createMap != null && HasReadyFiles();
            input.Sensitive = !busy;
            addFilesButton.Sensitive = !busy && !preparing && prepareFiles != null && files.Count < 5;
            foreach (Button remove in fileRemoveButtons) remove.Sensitive = !busy && !preparing;
            cancelButton.Sensitive = (busy || preparing) && !cancelRequested;
        }

        private bool HasReadyFiles()
        {
            return files.Exists(delegate(ChatFileInfo file) { return file.State == "ready" && !String.IsNullOrEmpty(file.Id); });
        }

        private void OnInputKeyPressed(object sender, KeyEventArgs args)
        {
            if ((args.Key == Key.Return || args.Key == Key.NumPadEnter || args.Key == Key.Enter) &&
                (args.Modifiers & ModifierKeys.Control) != 0)
            {
                args.Handled = true;
                BeginSendFromInput();
            }
        }

        private void OnWindowClosed(object sender, EventArgs args)
        {
            if (busy)
            {
                try { if (cancel != null) cancel(); } catch { }
            }
            try { ClearFilesCore(false); } catch { }
            windowClosed = true;
            attached = false;
            try
            {
                if (menuContainer != null && menuRoot != null) menuContainer.Items.Remove(menuRoot);
            }
            catch { }
            menuContainer = null;
            menuRoot = null;
            Panels.Remove(window);
        }

        private static string Value(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value ?? "" : "";
        }

        private static void Ui(Action action)
        {
            if (Application.InvokeRequired) Application.Invoke(action, true); else action();
        }

        private static T UiResult<T>(Func<T> callback)
        {
            if (!Application.InvokeRequired) return callback();
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Application.Invoke(delegate
            {
                try { completion.TrySetResult(callback()); }
                catch (Exception error) { completion.TrySetException(error); }
            }, true);
            return completion.Task.GetAwaiter().GetResult();
        }

        private static void RequireUiThread()
        {
            if (Application.InvokeRequired) throw new InvalidOperationException("聊天面板必须在 Xwt 界面线程操作");
        }

        private sealed class ChatTextEntry : TextEntry
        {
            internal void EnableNativeScrolling()
            {
                try
                {
                    object native = NativeTextBox();
                    if (native == null) return;
                    PropertyInfo property = native.GetType().GetProperty("VerticalScrollBarVisibility");
                    if (property != null) property.SetValue(native, Enum.Parse(property.PropertyType, "Auto"), null);
                }
                catch { /* Scrolling fallback does not affect the original window. */ }
            }

            internal void ScrollToBottom()
            {
                CursorPosition = Text.Length;
                try
                {
                    object native = NativeTextBox();
                    if (native == null) return;
                    MethodInfo method = native.GetType().GetMethod("ScrollToEnd", Type.EmptyTypes);
                    if (method != null) method.Invoke(native, null);
                }
                catch { }
            }

            private object NativeTextBox()
            {
                object backend = GetBackend(this);
                PropertyInfo property = backend.GetType().GetProperty("TextBox", BindingFlags.Instance | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(backend, null);
            }
        }
    }
}
