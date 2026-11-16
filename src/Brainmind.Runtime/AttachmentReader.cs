using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Brainmind.Runtime
{
    public static class AttachmentReader
    {
        public const int MaxFileBytes = 20 * 1024 * 1024;
        public const int MaxPages = 600;
        public const int MaxPageCharacters = 40000;
        public const int MaxFileCharacters = 400000;
        public const int TextSegmentCharacters = 6000;

        public static Task<AttachmentSnapshot> ReadAsync(string path, CancellationToken token)
        {
            return Task.Run(delegate { return Read(path, token); }, token);
        }

        // Synchronous core for a background worker and independent CLI tests.
        // No TheBrain model, editor, importer or user-profile API is called here.
        public static AttachmentSnapshot Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool pdf = false;
            try
            {
                if (String.IsNullOrWhiteSpace(path)) throw new InputException("请先选择要阅读的文件。");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                pdf = extension == ".pdf";
                if (!pdf && extension != ".txt" && extension != ".md" && extension != ".csv")
                    throw new InputException("支持 PDF、TXT、MD 和 CSV 文件。");
                byte[] bytes = ReadBytes(path, token);
                if (bytes.Length == 0) throw new InputException("文件为空，没有可阅读的内容。");
                token.ThrowIfCancellationRequested();
                AttachmentSnapshot value = new AttachmentSnapshot();
                value.Id = Guid.NewGuid().ToString("N");
                value.FileName = Path.GetFileName(path);
                value.SizeBytes = bytes.LongLength;
                using (SHA256 hash = SHA256.Create())
                    value.Sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                value.Kind = pdf ? "pdf" : "text";
                value.Pages = new List<AttachmentPage>();
                if (pdf) ReadPdf(bytes, value, token);
                else ReadText(bytes, value, token);
                token.ThrowIfCancellationRequested();
                return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (InputException error) { throw new InvalidOperationException(error.Message); }
            catch (PdfDocumentEncryptedException)
            {
                throw new InvalidOperationException("PDF 已加密或需要密码。请先提供可直接阅读的解密副本。");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("无法读取所选文件，请检查文件访问权限。");
            }
            catch (IOException)
            {
                throw new InvalidOperationException("无法读取所选文件；它可能已移动、被占用或尚未下载到本机。");
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidOperationException("文字编码无法识别。请另存为 UTF-8，或带 BOM 的 UTF-16 文件。");
            }
            catch (Exception)
            {
                // Parser exceptions can include filenames, internal objects and local paths.
                // Expose a useful bounded error without forwarding those implementation details.
                throw new InvalidOperationException(pdf
                    ? "无法解析 PDF；文件可能损坏、使用不支持的格式或需要密码。请尝试可正常打开的 PDF 副本。"
                    : "无法读取文字文件，请检查格式并另存为 UTF-8 或带 BOM 的 UTF-16。");
            }
        }

        private static byte[] ReadBytes(string path, CancellationToken token)
        {
            using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            {
                if (source.Length > MaxFileBytes) throw new InputException("单个文件不能超过 20 MiB，请拆分后再添加。");
                using (MemoryStream snapshot = new MemoryStream((int)source.Length))
                {
                    byte[] block = new byte[65536];
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = source.Read(block, 0, block.Length);
                        if (count == 0) break;
                        if (snapshot.Length + count > MaxFileBytes)
                            throw new InputException("单个文件不能超过 20 MiB，请拆分后再添加。");
                        snapshot.Write(block, 0, count);
                    }
                    return snapshot.ToArray();
                }
            }
        }

        private static void ReadPdf(byte[] bytes, AttachmentSnapshot value, CancellationToken token)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool magic = false;
            for (int i = 0; i <= Math.Min(bytes.Length - 5, 1023); i++)
                if (bytes[i] == '%' && bytes[i + 1] == 'P' && bytes[i + 2] == 'D' && bytes[i + 3] == 'F' && bytes[i + 4] == '-')
                { magic = true; break; }
            if (!magic) throw new InputException("所选文件没有有效的 PDF 文件头，无法作为 PDF 阅读。");
            int characters = 0, blankPages = 0;
            List<string> notices = new List<string>();
            using (PdfDocument document = PdfDocument.Open(bytes, null))
            {
                CheckPdfBudget(elapsed, token);
                value.TotalPages = document.NumberOfPages;
                if (value.TotalPages <= 0) throw new InputException("PDF 没有可阅读的页面。");
                int limit = Math.Min(value.TotalPages, MaxPages);
                if (value.TotalPages > MaxPages)
                {
                    value.Truncated = true;
                    notices.Add("PDF 共 " + value.TotalPages + " 页，只读取前 " + MaxPages + " 页。");
                }
                for (int pageNumber = 1; pageNumber <= limit; pageNumber++)
                {
                    CheckPdfBudget(elapsed, token);
                    string text = Normalize(ContentOrderTextExtractor.GetText(document.GetPage(pageNumber), false));
                    CheckPdfBudget(elapsed, token);
                    if (String.IsNullOrWhiteSpace(text)) blankPages++;
                    int length = SafeLength(text, 0, Math.Min(MaxPageCharacters, MaxFileCharacters - characters));
                    bool truncated = length < text.Length;
                    if (truncated) value.Truncated = true;
                    value.Pages.Add(new AttachmentPage { Page = pageNumber, Text = text.Substring(0, length), Truncated = truncated });
                    characters += length;
                    if (characters >= MaxFileCharacters && pageNumber < value.TotalPages)
                    {
                        value.Truncated = true;
                        break;
                    }
                }
            }
            bool hasText = false;
            foreach (AttachmentPage page in value.Pages)
                if (!String.IsNullOrWhiteSpace(page.Text)) { hasText = true; break; }
            if (!hasText)
                throw new InputException("在已读取的 PDF 页面中没有可提取的文字。扫描件或图片 PDF 需要先进行 OCR，本功能不执行 OCR。");
            if (blankPages > 0)
                notices.Add("已读取的页面中有 " + blankPages + " 页没有可提取文字，可能是空白页或扫描页；未执行 OCR。");
            if (value.Truncated)
                notices.Add("内容已截断：每页最多 " + MaxPageCharacters + " 字符、每份文件最多 " + MaxFileCharacters
                    + " 字符；当前提供 " + value.Pages.Count + "/" + value.TotalPages + " 页，请勿把片段当作全文。");
            value.Notice = String.Join(" ", notices.ToArray());
        }

        // PdfPig 0.1.8 has no interruptible single-page parser. Checks happen
        // before/after opening and each page; cancellation cannot abort one parser call.
        private static void CheckPdfBudget(Stopwatch elapsed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(45))
                throw new InputException("PDF 提取耗时较长，已停止后续页面的读取。请拆分文件后重试；单个页面解析期间无法立即中断。");
        }

        private static void ReadText(byte[] bytes, AttachmentSnapshot value, CancellationToken token)
        {
            string encoding;
            string text = Normalize(Decode(bytes, out encoding));
            token.ThrowIfCancellationRequested();
            foreach (char character in text)
                if (character == '\0' || (Char.IsControl(character) && character != '\n' && character != '\r' && character != '\t'))
                    throw new InputException("文件包含 NUL 或二进制控制字符，不能作为普通文字阅读。请转换为 UTF-8 文字文件。");
            if (String.IsNullOrWhiteSpace(text)) throw new InputException("文字文件没有可阅读的内容。");
            int position = 0, total = 0;
            while (position < text.Length)
            {
                token.ThrowIfCancellationRequested();
                int length = SafeLength(text, position, TextSegmentCharacters);
                total++;
                position += length;
            }
            value.TotalPages = total;
            position = 0;
            int characters = 0;
            while (position < text.Length && value.Pages.Count < MaxPages && characters < MaxFileCharacters)
            {
                token.ThrowIfCancellationRequested();
                int originalLength = SafeLength(text, position, TextSegmentCharacters);
                int length = SafeLength(text, position, Math.Min(originalLength, MaxFileCharacters - characters));
                if (length == 0) break;
                value.Pages.Add(new AttachmentPage { Page = value.Pages.Count + 1,
                    Text = text.Substring(position, length), Truncated = length < originalLength });
                position += length;
                characters += length;
                if (length < originalLength) break;
            }
            value.Truncated = position < text.Length;
            value.Notice = "文字编码：" + encoding + "。文本按每段最多 " + TextSegmentCharacters
                + " 字符划分为逻辑段，段号不是 PDF 物理页码。";
            if (value.Truncated)
                value.Notice += " 内容已截断，仅提供前 " + characters + " 字符（最多 " + MaxFileCharacters
                    + " 字符、" + MaxPages + " 段），请勿把片段当作全文。";
        }

        private static string Decode(byte[] bytes, out string name)
        {
            if (bytes.Length >= 4 && ((bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
                || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)))
                throw new InputException("暂不支持 UTF-32，请另存为 UTF-8 或带 BOM 的 UTF-16。");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            { name = "UTF-8 BOM"; return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            { name = "UTF-16 LE BOM"; return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            { name = "UTF-16 BE BOM"; return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2); }
            try { name = "UTF-8"; return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                // Windows-authored Chinese text commonly uses GB18030. The provider ships in
                // the .NET 6 runtime; no additional package or external DLL is installed.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                name = "GB18030";
                return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
            }
        }

        private static string Normalize(string text) { return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n'); }
        private static int SafeLength(string text, int start, int maximum)
        {
            int length = Math.Min(Math.Max(0, maximum), text.Length - start);
            if (length > 0 && start + length < text.Length && Char.IsHighSurrogate(text[start + length - 1]) && Char.IsLowSurrogate(text[start + length])) length--;
            return length;
        }
        private sealed class InputException : Exception { internal InputException(string message) : base(message) { } }
    }
}
