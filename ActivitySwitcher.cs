using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ActivitySwitcher {
public sealed class RpcClient : IDisposable {
    NamedPipeClientStream pipe;
    readonly object sync = new object();
    readonly Dictionary<string, TaskCompletionSource<bool>> pending = new Dictionary<string, TaskCompletionSource<bool>>();
    readonly string prefix;
    volatile bool connected;
    public bool Connected { get { return connected; } }
    public RpcClient(string pipePrefix = "discord-ipc-") { prefix = pipePrefix; }
    static string Json(object obj) { return new JavaScriptSerializer().Serialize(obj); }
    public static void ValidateId(string id) {
        ulong value;
        if (!Regex.IsMatch(id ?? "", @"^\d{17,20}$") || !ulong.TryParse(id, out value) || value == 0)
            throw new ArgumentException("Введите Application ID из Discord Developer Portal (17–20 цифр).");
    }
    public async Task ConnectAsync(string id) {
        ValidateId(id);
        for (int n = 0; n < 10; n++) {
            var candidate = new NamedPipeClientStream(".", prefix + n, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await Task.Run(() => candidate.Connect(150)); pipe = candidate; break; }
            catch (TimeoutException) { candidate.Dispose(); }
            catch (IOException) { candidate.Dispose(); }
            catch (UnauthorizedAccessException) { candidate.Dispose(); }
        }
        if (pipe == null) throw new IOException("Discord недоступен. Откройте настольный Discord и войдите в аккаунт.");
        try {
            WriteFrame(0, Encoding.UTF8.GetBytes(Json(new { v = 1, client_id = id })));
            var read = ReadFrameAsync();
            if (await Task.WhenAny(read, Task.Delay(5000)) != read) throw new TimeoutException("Discord не ответил на подключение.");
            var packet = await read;
            var ready = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(packet.Item2)) as Dictionary<string, object>;
            if (packet.Item1 != 1 || ready == null || !ready.ContainsKey("evt") || Convert.ToString(ready["evt"]) != "READY")
                throw new IOException("Discord отклонил подключение. Проверьте Application ID.");
            connected = true;
            ReadLoop();
        } catch { Dispose(); throw; }
    }
    void WriteFrame(int opcode, byte[] body) {
        byte[] frame = new byte[8 + body.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, frame, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, frame, 4, 4);
        Buffer.BlockCopy(body, 0, frame, 8, body.Length);
        lock (sync) { pipe.Write(frame, 0, frame.Length); }
    }
    async Task<byte[]> ReadExactAsync(int length) {
        var data = new byte[length];
        int offset = 0;
        while (offset < length) {
            int count = await pipe.ReadAsync(data, offset, length - offset).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Соединение с Discord закрыто.");
            offset += count;
        }
        return data;
    }
    async Task<Tuple<int, byte[]>> ReadFrameAsync() {
        var header = await ReadExactAsync(8).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > 1024 * 1024) throw new IOException("Некорректный размер RPC-пакета.");
        return Tuple.Create(BitConverter.ToInt32(header, 0), await ReadExactAsync(length).ConfigureAwait(false));
    }
    async void ReadLoop() {
        try {
            while (connected) {
                var packet = await ReadFrameAsync().ConfigureAwait(false);
                if (packet.Item1 == 3) { WriteFrame(4, packet.Item2); continue; }
                if (packet.Item1 == 2) throw new IOException("Discord закрыл соединение.");
                if (packet.Item1 != 1) continue;
                var obj = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(packet.Item2)) as Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey("nonce")) continue;
                var nonce = Convert.ToString(obj["nonce"]);
                TaskCompletionSource<bool> completion;
                lock (sync) { if (!pending.TryGetValue(nonce, out completion)) continue; pending.Remove(nonce); }
                if (obj.ContainsKey("evt") && Convert.ToString(obj["evt"]) == "ERROR") {
                    var data = obj.ContainsKey("data") ? obj["data"] as Dictionary<string, object> : null;
                    completion.TrySetException(new IOException("Discord: " + (data != null && data.ContainsKey("message") ? Convert.ToString(data["message"]) : "ошибка RPC")));
                } else completion.TrySetResult(true);
            }
        } catch { Dispose(); }
    }
    public async Task SetActivityAsync(object activity) {
        if (!connected) throw new IOException("Нет соединения с Discord.");
        string nonce = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<bool>();
        lock (sync) { pending.Add(nonce, completion); }
        try {
            WriteFrame(1, Encoding.UTF8.GetBytes(Json(new { cmd = "SET_ACTIVITY", args = new { pid = Process.GetCurrentProcess().Id, activity = activity }, nonce = nonce })));
            if (await Task.WhenAny(completion.Task, Task.Delay(5000)) != completion.Task) throw new TimeoutException("Discord не подтвердил изменение активности.");
            await completion.Task;
        } finally { lock (sync) { pending.Remove(nonce); } }
    }
    public void Dispose() {
        connected = false;
        List<TaskCompletionSource<bool>> waiting;
        lock (sync) {
            if (pipe != null) { try { pipe.Dispose(); } catch { } }
            waiting = new List<TaskCompletionSource<bool>>(pending.Values);
            pending.Clear();
        }
        foreach (var item in waiting) item.TrySetException(new IOException("Соединение с Discord закрыто."));
    }
}

public sealed class Settings {
    public string ApplicationId = "";
    public string FilePath = "";
    public string Name = "";
    public string Details = "";
    public string State = "";
    public string Image = "";
    public bool Watch = true;
    public bool Timer = true;
}

public static class Files {
    public static string Resolve(string path) {
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(path)) throw new IOException("Файл не найден.");
        if (String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) {
            object shell = null, shortcut = null;
            try {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                path = Convert.ToString(shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null));
            } finally {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
        if (!String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new IOException("Выберите .exe или ярлык .lnk, который ведёт на .exe. Ярлыки Steam .url не поддерживаются.");
        return Path.GetFullPath(path);
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static bool IsRunning(string path) {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))) {
            using (process) {
                IntPtr handle = OpenProcess(0x1000, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try {
                    var buffer = new StringBuilder(32768); int size = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size) && String.Equals(buffer.ToString(), path, StringComparison.OrdinalIgnoreCase)) return true;
                } finally { CloseHandle(handle); }
            }
        }
        return false;
    }
}

public sealed class MainForm : Form {
    readonly TextBox appId = new TextBox(), file = new TextBox(), name = new TextBox(), details = new TextBox(), state = new TextBox(), image = new TextBox();
    readonly CheckBox watch = new CheckBox(), timerBox = new CheckBox();
    readonly Button start = new Button(), stop = new Button(), browse = new Button(), preset = new Button();
    readonly Label status = new Label(), preview = new Label();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    RpcClient rpc;
    bool active, busy, shown;
    long started;
    readonly string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActivitySwitcher", "settings.json");
    public MainForm() {
        Text = "Activity Switcher • Discord"; ClientSize = new Size(690, 680);
        MinimumSize = new Size(706, 719); MaximumSize = new Size(1000, 719);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(25, 27, 34); ForeColor = Color.FromArgb(230, 232, 240);
        AutoScaleMode = AutoScaleMode.Dpi; AllowDrop = true;
        var title = LabelAt("Твоя активность в Discord", 24, 20, 640, 34); title.Font = new Font("Segoe UI", 19, FontStyle.Bold);
        LabelAt("Своя карточка для любой программы.", 26, 61, 425, 26).ForeColor = Color.Silver;
        preset.Text = "VS Code · Overflow"; preset.SetBounds(475, 59, 187, 30); StyleButton(preset); Controls.Add(preset);
        preset.Click += (s, e) => ApplyVsCodePreset();
        Field("Application ID", appId, 102); appId.MaxLength = 20; appId.Width = 420;
        var portal = new LinkLabel { Text = "Где взять ID?", Left = 475, Top = 127, Width = 165, LinkColor = Color.LightSkyBlue };
        portal.LinkClicked += (s, e) => { Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); }; Controls.Add(portal);
        Field("EXE или ярлык — можно перетащить в окно", file, 164); file.ReadOnly = true; file.Width = 526;
        browse.Text = "…"; browse.SetBounds(562, 186, 100, 30); StyleButton(browse); browse.Click += Browse; Controls.Add(browse);
        Field("Название активности", name, 226); name.MaxLength = 128;
        Field("Описание", details, 288); details.MaxLength = 128;
        Field("Вторая строка", state, 350); state.MaxLength = 128;
        Field("Картинка: ссылка HTTPS или ключ из Rich Presence → Art Assets", image, 412); image.MaxLength = 256;
        watch.Text = "Показывать только пока выбранная программа запущена"; watch.SetBounds(26, 477, 635, 28); watch.Checked = true;
        timerBox.Text = "Показывать время"; timerBox.SetBounds(26, 507, 240, 28); timerBox.Checked = true;
        Controls.Add(watch); Controls.Add(timerBox);
        preview.SetBounds(26, 542, 635, 28); preview.ForeColor = Color.Silver; Controls.Add(preview);
        start.Text = "Включить"; start.SetBounds(26, 577, 310, 40); StyleButton(start); start.BackColor = Color.FromArgb(88, 101, 242); Controls.Add(start);
        stop.Text = "Остановить"; stop.SetBounds(350, 577, 312, 40); StyleButton(stop); stop.Enabled = false; Controls.Add(stop);
        status.SetBounds(26, 628, 636, 44); status.Text = "Готово. Нужен запущенный настольный Discord."; Controls.Add(status);
        name.TextChanged += (s, e) => UpdatePreview();
        start.Click += async (s, e) => {
            try {
                RpcClient.ValidateId(appId.Text.Trim());
                if (watch.Checked && file.Text.Length == 0) throw new ArgumentException("Выберите EXE или выключите проверку запущенной программы.");
                if (file.Text.Length > 0) file.Text = Files.Resolve(file.Text);
                if (String.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Введите название активности.");
                foreach (var value in new[] { name.Text.Trim(), details.Text.Trim(), state.Text.Trim() })
                    if (value.Length == 1) throw new ArgumentException("Текст должен содержать минимум 2 символа или быть пустым.");
                Save(); active = true; shown = false; started = 0; SetEditing(false); timer.Start(); await UpdateAsync();
            } catch (Exception ex) { status.Text = ex.Message; }
        };
        stop.Click += async (s, e) => { await StopAsync(); };
        timer.Interval = 4000; timer.Tick += async (s, e) => { await UpdateAsync(); };
        DragEnter += (s, e) => { e.Effect = !active && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
        DragDrop += (s, e) => { if (!active) SelectFile(((string[])e.Data.GetData(DataFormats.FileDrop))[0]); };
        FormClosing += (s, e) => { active = false; timer.Stop(); if (rpc != null) rpc.Dispose(); };
        LoadSettings(); UpdatePreview();
    }
    Label LabelAt(string text, int x, int y, int width, int height) {
        var label = new Label { Text = text, Left = x, Top = y, Width = width, Height = height }; Controls.Add(label); return label;
    }
    void Field(string caption, TextBox box, int y) {
        LabelAt(caption, 26, y, 635, 24); box.SetBounds(26, y + 23, 636, 30);
        box.BackColor = Color.FromArgb(40, 43, 53); box.ForeColor = ForeColor; box.BorderStyle = BorderStyle.FixedSingle; Controls.Add(box);
    }
    void StyleButton(Button button) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.BackColor = Color.FromArgb(49, 53, 65); button.ForeColor = Color.White; }
    void Browse(object s, EventArgs e) {
        using (var dialog = new OpenFileDialog { Filter = "Программы и ярлыки|*.exe;*.lnk", DereferenceLinks = false })
            if (dialog.ShowDialog() == DialogResult.OK) SelectFile(dialog.FileName);
    }
    void SelectFile(string path) {
        try { file.Text = Files.Resolve(path); name.Text = Path.GetFileNameWithoutExtension(path); status.Text = "Выбрано: " + Path.GetFileName(file.Text); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    void UpdatePreview() { preview.Text = "Играет в  " + (String.IsNullOrWhiteSpace(name.Text) ? "название активности" : name.Text.Trim()); }
    void ApplyVsCodePreset() {
        name.Text = "Visual Studio Code"; details.Text = "Проект: Overflow"; state.Text = "";
        image.Text = "https://raw.githubusercontent.com/microsoft/vscode/main/resources/linux/code.png"; timerBox.Checked = true;
        string found = null;
        foreach (var root in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }) {
            if (String.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Microsoft VS Code", "Code.exe");
            if (File.Exists(candidate)) { found = candidate; break; }
        }
        file.Text = found ?? ""; watch.Checked = found != null;
        status.Text = found != null ? "Пресет готов. Вставьте Application ID и нажмите «Включить»." : "Пресет готов. VS Code не найден: карточка будет работать вручную. Можно выбрать Code.exe.";
    }
    void SetEditing(bool editable) {
        foreach (Control c in new Control[] { appId, file, name, details, state, image, watch, timerBox, browse, preset, start }) c.Enabled = editable;
        stop.Enabled = !editable;
    }
    public static Dictionary<string, object> Activity(string name, string details, string state, string image, long started, bool timer) {
        var result = new Dictionary<string, object> { { "name", name }, { "type", 0 } };
        if (details.Length > 0) result["details"] = details;
        if (state.Length > 0) result["state"] = state;
        if (image.Length > 0) result["assets"] = new { large_image = image, large_text = name };
        if (timer) result["timestamps"] = new { start = started };
        return result;
    }
    async Task UpdateAsync() {
        if (!active || busy) return;
        busy = true;
        try {
            bool shouldShow = !watch.Checked || Files.IsRunning(file.Text);
            if (!shouldShow) {
                if (shown && rpc != null && rpc.Connected) await rpc.SetActivityAsync(null);
                shown = false; started = 0; status.Text = "Ожидание запуска: " + Path.GetFileName(file.Text); return;
            }
            if (rpc == null || !rpc.Connected) {
                if (rpc != null) rpc.Dispose();
                rpc = new RpcClient(); shown = false; status.Text = "Подключение к Discord…";
                await rpc.ConnectAsync(appId.Text.Trim());
            }
            if (!active) { rpc.Dispose(); return; }
            if (!shown) {
                if (started == 0) started = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                await rpc.SetActivityAsync(Activity(name.Text.Trim(), details.Text.Trim(), state.Text.Trim(), image.Text.Trim(), started, timerBox.Checked));
                shown = true;
            }
            status.Text = "Discord подтвердил активность. Другие приложения могут показывать свои карточки.";
        } catch (Exception ex) {
            if (rpc != null) rpc.Dispose(); rpc = null; shown = false;
            if (active) status.Text = ex.Message + " Повтор через 4 с.";
        } finally { busy = false; }
    }
    async Task StopAsync() {
        active = false; timer.Stop(); stop.Enabled = false;
        while (busy) await Task.Delay(50);
        string message = "Остановлено. Собственная активность убрана.";
        try { if (rpc != null && rpc.Connected) await rpc.SetActivityAsync(null); }
        catch { message = "Соединение закрыто. Discord может убрать карточку с задержкой."; }
        finally { if (rpc != null) rpc.Dispose(); rpc = null; shown = false; SetEditing(true); status.Text = message; }
    }
    void Save() {
        var settings = new Settings { ApplicationId = appId.Text.Trim(), FilePath = file.Text, Name = name.Text, Details = details.Text, State = state.Text, Image = image.Text, Watch = watch.Checked, Timer = timerBox.Checked };
        Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(settings), Encoding.UTF8);
    }
    void LoadSettings() {
        try {
            if (!File.Exists(configPath)) return;
            var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(configPath));
            appId.Text = s.ApplicationId; file.Text = s.FilePath; name.Text = s.Name; details.Text = s.Details; state.Text = s.State; image.Text = s.Image; watch.Checked = s.Watch; timerBox.Checked = s.Timer;
        } catch { status.Text = "Не удалось прочитать настройки. Заполните поля заново."; }
    }
}
static class Program {
    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
}
}
