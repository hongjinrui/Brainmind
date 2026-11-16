using System.Collections.Generic;

namespace Brainmind.Runtime
{
    public sealed class AttachmentPage
    {
        public int Page { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
    }

    // No local path is retained in a snapshot. SHA-256 identifies the exact file
    // bytes read, while Id identifies this selected attachment in the chat.
    public sealed class AttachmentSnapshot
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string Kind { get; set; }
        public int TotalPages { get; set; }
        public List<AttachmentPage> Pages { get; set; }
        public bool Truncated { get; set; }
        public string Notice { get; set; }
    }
}
