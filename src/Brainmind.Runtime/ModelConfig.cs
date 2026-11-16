using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Brainmind.Runtime
{
    // API keys are protected with the signed-in Windows user's DPAPI key.
    // A key-less endpoint (for example a loopback local model) is supported.
    public sealed class ModelConfig
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public int TimeoutSeconds { get; set; }

        public ModelConfig()
        {
            BaseUrl = "https://api.openai.com/v1";
            Model = "";
            ApiKey = "";
            TimeoutSeconds = 120;
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brainmind", "model.json"); }
        }

        public static ModelConfig Load(string path = null)
        {
            path = path ?? DefaultPath;
            if (!File.Exists(path)) return new ModelConfig();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
            {
                JsonElement root = doc.RootElement;
                ModelConfig result = new ModelConfig();
                JsonElement value;
                if (root.TryGetProperty("base_url", out value)) result.BaseUrl = value.GetString() ?? "";
                if (root.TryGetProperty("model", out value)) result.Model = value.GetString() ?? "";
                if (root.TryGetProperty("timeout_seconds", out value)) result.TimeoutSeconds = value.GetInt32();
                if (root.TryGetProperty("api_key_dpapi", out value) && !String.IsNullOrEmpty(value.GetString()))
                    result.ApiKey = Unprotect(value.GetString());
                // Intentionally ignore legacy/plaintext api_key fields.
                return result;
            }
        }

        public void Save(string path = null)
        {
            path = Path.GetFullPath(path ?? DefaultPath);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string encrypted = String.IsNullOrEmpty(ApiKey) ? "" : Protect(ApiKey);
            string json = JsonSerializer.Serialize(new {
                version = 1,
                base_url = BaseUrl ?? "",
                model = Model ?? "",
                timeout_seconds = TimeoutSeconds,
                api_key_dpapi = encrypted
            }, new JsonSerializerOptions { WriteIndented = true });
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal Uri CompletionEndpoint()
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(BaseUrl) || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Model base URL must be an absolute HTTP(S) URL without credentials, query or fragment.");
            if (String.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Please configure a model name in Brainmind settings first.");
            if (TimeoutSeconds < 1 || TimeoutSeconds > 600) throw new InvalidOperationException("Model timeout must be between 1 and 600 seconds.");
            // Header parsers can quote invalid input in exception messages.
            // Validate the credential without ever interpolating its value.
            if (!String.IsNullOrWhiteSpace(ApiKey))
                foreach (char character in ApiKey.Trim())
                    if (character <= 32 || character == 127) throw new InvalidOperationException("The API key contains invalid whitespace or control characters.");
            // Never transmit a bearer credential over an unencrypted remote connection.
            if (uri.Scheme == "http" && !uri.IsLoopback && !String.IsNullOrEmpty(ApiKey))
                throw new InvalidOperationException("An API key requires HTTPS except for a loopback local model.");
            string url = uri.AbsoluteUri.TrimEnd('/');
            return new Uri(url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? url : url + "/chat/completions");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr value);

        private static string Protect(string value)
        {
            return Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(value), true));
        }
        private static string Unprotect(string value)
        {
            try { return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(value), false)); }
            catch (Exception ex) { throw new InvalidOperationException("The saved API key cannot be decrypted for this Windows user. Re-enter it in Brainmind settings.", ex); }
        }
        private static byte[] Crypt(byte[] source, bool protect)
        {
            Blob input = new Blob();
            Blob output = new Blob();
            input.Length = source.Length;
            input.Data = Marshal.AllocHGlobal(source.Length);
            try
            {
                Marshal.Copy(source, 0, input.Data, source.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "Brainmind model credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new InvalidOperationException("Windows DPAPI credential protection failed (" + Marshal.GetLastWin32Error() + ").");
                byte[] result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                for (int i = 0; i < source.Length; i++) Marshal.WriteByte(input.Data, i, 0);
                Array.Clear(source, 0, source.Length);
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                    LocalFree(output.Data);
                }
            }
        }
    }
}
