using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Brainmind.Runtime
{
    internal static class PipeServer
    {
        internal static void Start()
        {
            for (int i = 0; i < 4; i++) Task.Run((Func<Task>)Serve);
        }

        private static async Task Serve()
        {
            while (true)
            {
                bool failed = false;
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(RuntimeHost.PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                    {
                        await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                        using (CancellationTokenSource receive = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        {
                            string line = await ReadRequest(pipe, receive.Token).ConfigureAwait(false);
                            object result;
                            try
                            {
                                if (line == null) throw new InvalidOperationException("An empty request is not supported.");
                                using (JsonDocument json = JsonDocument.Parse(line))
                                    result = await RuntimeHost.HandleRequest(json.RootElement.Clone()).ConfigureAwait(false);
                                result = new { success = true, result = result };
                            }
                            catch (Exception error)
                            {
                                result = new { success = false, error = error.Message };
                            }
                            byte[] response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result) + "\n");
                            using (CancellationTokenSource send = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                            {
                                await pipe.WriteAsync(response, 0, response.Length, send.Token).ConfigureAwait(false);
                                await pipe.FlushAsync(send.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception error) { failed = true; RuntimeHost.Log("pipe_error", error.Message); }
                if (failed) await Task.Delay(500).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequest(Stream pipe, CancellationToken token)
        {
            using (MemoryStream data = new MemoryStream())
            {
                byte[] block = new byte[8192];
                while (true)
                {
                    int count = await pipe.ReadAsync(block, 0, block.Length, token).ConfigureAwait(false);
                    if (count == 0) return null;
                    int end = Array.IndexOf<byte>(block, 10, 0, count);
                    int length = end < 0 ? count : end;
                    if (data.Length + length > 8 * 1024 * 1024) throw new InvalidOperationException("Request exceeds the 8 MiB limit.");
                    data.Write(block, 0, length);
                    if (end >= 0) return new UTF8Encoding(false, true).GetString(data.ToArray()).TrimStart('\ufeff');
                }
            }
        }
    }
}
