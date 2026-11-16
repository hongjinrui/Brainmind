using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Brainmind.Runtime
{
    // All calls run on the native UI thread. Database changes remain exclusively
    // in TheBrain's own business transaction; the local journal protects files
    // which its blob store moves before that transaction commits.
    internal static class NativeDeletionTransaction
    {
        private const long MaximumBytes = 256L * 1024 * 1024;
        private const int MaximumFiles = 10000;

        private sealed class SavedFile
        {
            public string Source, Backup, Hash;
            public long Length;
            public FileAttributes Attributes;
            public DateTime Modified;
        }
        private sealed class SavedDirectory
        {
            public string Source;
            public FileAttributes Attributes;
            public DateTime Modified;
        }
        private sealed class Journal
        {
            public string Root;
            public readonly List<string> SourceRoots = new List<string>();
            public readonly List<SavedFile> Files = new List<SavedFile>();
            public readonly List<SavedDirectory> Directories = new List<SavedDirectory>();
            public long Bytes;
        }

        internal static Dictionary<string, object> Execute(object context, object logic, Guid brain, List<Guid> ids, Dictionary<Guid, object> attachments)
        {
            return Execute(context, logic, brain, ids, attachments, null, null);
        }
        internal static Dictionary<string, object> Execute(object context, object logic, Guid brain, List<Guid> ids, Dictionary<Guid, object> attachments, Action beforeDelete)
        {
            return Execute(context, logic, brain, ids, attachments, beforeDelete, null);
        }

        // beforeCommit is an in-process test hook, never a model/IPC argument.
        internal static Dictionary<string, object> Execute(object context, object logic, Guid brain, List<Guid> ids,
            Dictionary<Guid, object> attachments, Action beforeDelete, Action beforeCommit)
        {
            Dictionary<string, object> receipt = Map("success", false, "code", "native_error", "error", "原生删除尚未完成。",
                "native_errors", new List<string>(), "native_exception", null, "commit_attempted", false, "committed", false,
                "commit_state", "not_attempted", "filesystem_rollback_verified", true, "partial_file_mutations", new List<object>(),
                "recovery_backup", null, "backup_complete", false, "backed_up_files", 0, "backed_up_bytes", 0L,
                "native_transaction", false, "native_trash", true, "file_event_scope", false, "before_delete_attempted", false,
                "mutation_attempted", false, "database_rollback_verified", true, "unverified_mutation", false,
                "file_rollback_verified", true, "file_rollback_attempted", false, "recovery_backup_available", false, "recovery_directory", null);
            IDisposable scope = null, transactionLifetime = null;
            object transaction = null;
            Journal journal = null;
            bool coreAttempted = false, commitAttempted = false, committed = false, commitKnown = true;
            try
            {
                if (context == null || logic == null || brain == Guid.Empty || ids == null || ids.Count == 0 || attachments == null)
                    throw new InvalidOperationException("删除事务参数不完整。");
                object processor = Get(context, "_______x_x__x__xx_xxx_x_");
                scope = Call(processor, "___xx_x_x__x_xx_xx_xxxx_", new object[] { null }) as IDisposable;
                if (scope == null) throw new InvalidOperationException("无法暂停原生文件事件，未执行删除。");
                receipt["file_event_scope"] = true;
                object resolver = Get(context, "_x___xx_xx___x_xxxxxx__x");
                journal = new Journal();
                Backup(context, resolver, ids, attachments, journal);
                receipt["recovery_backup"] = journal.Root;
                receipt["backed_up_files"] = journal.Files.Count;
                receipt["backed_up_bytes"] = journal.Bytes;
                receipt["backup_complete"] = true;
                List<object> changed = Verify(journal);
                if (changed.Count != 0) throw new InvalidOperationException("备份期间附件已变化，未执行删除。");
                if (beforeDelete != null)
                {
                    receipt["before_delete_attempted"] = true;
                    receipt["mutation_attempted"] = true;
                    beforeDelete();
                }
                if (Verify(journal).Count != 0) throw new InvalidOperationException("删除前附件已变化，未执行删除。");
                object factory = Get(logic, "_x__xxxx_____xx_xx_x_x");
                object core = Get(logic, "___x_xx___x_xx_x__x_xxx");
                // Method selection matches parameter types: the factory also has a
                // different single-argument overload accepting a native brain context.
                transaction = Call(factory, "_xx_x__x_xx___xxxx__xx", new object[] { brain });
                transactionLifetime = transaction as IDisposable;
                if (transactionLifetime == null) throw new InvalidOperationException("原生事务未提供 Dispose，未执行删除。");
                receipt["native_transaction"] = true;
                coreAttempted = true;
                receipt["mutation_attempted"] = true;
                receipt["database_rollback_verified"] = false;
                receipt["filesystem_rollback_verified"] = false;
                object native = Call(core, "__x_xxx____xxxxx_xxx___x", new object[] { transaction, ids });
                List<string> errors = NativeErrors(native);
                receipt["native_errors"] = errors;
                if (!NativeSuccess(native))
                    throw new InvalidOperationException(errors.Count == 0 ? "原生删除返回失败。" : "原生删除返回失败：" + String.Join("；", errors));
                if (beforeCommit != null) beforeCommit();
                commitAttempted = true;
                receipt["commit_attempted"] = true;
                commitKnown = false;
                Call(transaction, "_x__x__xxxxx__xxx__xx__", new object[0]);
                committed = true;
                commitKnown = true;
                receipt["committed"] = true;
                receipt["commit_state"] = "committed";
                receipt["success"] = true;
                receipt["code"] = null;
                receipt["error"] = null;
                receipt["filesystem_rollback_verified"] = true;
            }
            catch (Exception error)
            {
                error = Unwrap(error);
                receipt["native_exception"] = error.GetType().Name + ": " + error.Message;
                receipt["error"] = error.Message;
                if (journal == null || !(bool)receipt["backup_complete"])
                    receipt["code"] = "backup_refused";
                if (coreAttempted)
                {
                    int state = ProbeCommit(logic, brain, ids);
                    commitKnown = state != 0;
                    committed = state == 1;
                }
                receipt["committed"] = committed;
                receipt["commit_state"] = committed ? "committed" : commitKnown ? "uncommitted" : "unknown";
                receipt["database_rollback_verified"] = !coreAttempted || (commitKnown && !committed);
                if (coreAttempted && !committed && commitKnown && journal != null)
                {
                    receipt["file_rollback_attempted"] = true;
                    List<string> nativeRestoreErrors = new List<string>();
                    try { RestoreNativeFiles(logic, transaction, attachments, journal, nativeRestoreErrors); }
                    catch (Exception restoreError) { nativeRestoreErrors.Add(Unwrap(restoreError).Message); }
                    receipt["native_file_restore_errors"] = nativeRestoreErrors;
                    RestoreJournal(journal);
                    List<object> remaining = Verify(journal);
                    receipt["partial_file_mutations"] = remaining;
                    receipt["filesystem_rollback_verified"] = remaining.Count == 0;
                    if (remaining.Count != 0) receipt["code"] = "filesystem_restore_incomplete";
                }
                else if (coreAttempted && !committed)
                {
                    receipt["filesystem_rollback_verified"] = false;
                    if (journal != null) receipt["partial_file_mutations"] = Verify(journal);
                    receipt["code"] = "commit_unknown";
                }
                else if (!coreAttempted)
                {
                    // A failed backup or callback never invokes the native delete.
                    receipt["filesystem_rollback_verified"] = true;
                }
                receipt["unverified_mutation"] = (bool)receipt["mutation_attempted"]
                    && ((!coreAttempted && (bool)receipt["before_delete_attempted"])
                        || !commitKnown || !(bool)receipt["filesystem_rollback_verified"]);
            }
            finally
            {
                if (journal != null)
                {
                    receipt["recovery_backup"] = journal.Root;
                    receipt["backed_up_files"] = journal.Files.Count;
                    receipt["backed_up_bytes"] = journal.Bytes;
                }
                receipt["file_rollback_verified"] = receipt["filesystem_rollback_verified"];
                receipt["recovery_directory"] = journal == null ? null : journal.Root;
                receipt["recovery_backup_available"] = journal != null && !String.IsNullOrEmpty(journal.Root)
                    && Directory.Exists(journal.Root) && journal.Files.Count != 0;
                // Files are restored and verified before the event queue can resume.
                try { if (transactionLifetime != null) transactionLifetime.Dispose(); }
                catch (Exception error) { FailCleanup(receipt, "原生事务释放失败：", error); }
                try { if (scope != null) scope.Dispose(); }
                catch (Exception error) { FailCleanup(receipt, "文件事件队列恢复失败：", error); }
                if ((bool)receipt["success"] && journal != null)
                {
                    try { Cleanup(journal); receipt["recovery_backup"] = null; receipt["recovery_directory"] = null; receipt["recovery_backup_available"] = false; }
                    catch { receipt["recovery_backup_retained"] = true; }
                }
            }
            return receipt;
        }

        private static void Backup(object context, object resolver, List<Guid> ids, Dictionary<Guid, object> attachments, Journal journal)
        {
            HashSet<Guid> sources = new HashSet<Guid>(ids);
            foreach (object attachment in attachments.Values) sources.Add((Guid)Get(attachment, "SourceId"));
            foreach (Guid id in ids)
            {
                object graphResult = Call(context, "_xx_x_x__x_______x__x__", new object[] { id, false, false });
                if (!NativeSuccess(graphResult)) throw new InvalidOperationException("无法核验待删除节点的原生关系，未备份或删除。");
                IEnumerable links = Get(Get(graphResult, "Data"), "__xx_x___xxxxx__x__xx_x_") as IEnumerable;
                if (links == null) throw new InvalidOperationException("原生关系列表缺失，未执行删除。");
                foreach (object link in links) sources.Add((Guid)Get(link, "Id"));
            }
            string config = Environment.GetEnvironmentVariable("BRAINMIND_CONFIG_PATH");
            string recovery = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(String.IsNullOrEmpty(config) ? ModelConfig.DefaultPath : config)), "recovery");
            journal.Root = Path.GetFullPath(Path.Combine(recovery, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(journal.Root);
            try
            {
                foreach (Guid source in sources)
                {
                    if (source == Guid.Empty) continue;
                    string path = Path.GetFullPath(Convert.ToString(Call(resolver, "__xxx_x______xxxx______x", new object[] { source }), CultureInfo.InvariantCulture));
                    bool covered = false;
                    foreach (string existing in journal.SourceRoots) if (Within(path, existing)) { covered = true; break; }
                    if (covered || !Directory.Exists(path)) continue;
                    if (Within(journal.Root, path) || Within(path, journal.Root))
                        throw new InvalidOperationException("恢复目录与脑图文件目录重叠，未执行删除。");
                    journal.SourceRoots.Add(path);
                    // Recovery rejects reparse points in every ancestor; use the
                    // same predicate before allowing any native file movement.
                    SafeAncestors(path, journal);
                    Stack<string> pending = new Stack<string>(); pending.Push(path);
                    while (pending.Count != 0)
                    {
                        string directory = pending.Pop();
                        FileAttributes attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("附件目录含链接或重解析点，未执行删除。");
                        if (journal.Directories.Count >= MaximumFiles) throw new InvalidOperationException("附件目录数量超过备份上限，未执行删除。");
                        journal.Directories.Add(new SavedDirectory { Source = directory, Attributes = attributes, Modified = Directory.GetLastWriteTimeUtc(directory) });
                        foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
                        foreach (string file in Directory.GetFiles(directory))
                        {
                            FileInfo info = new FileInfo(file);
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("附件文件含链接或重解析点，未执行删除。");
                            if (journal.Files.Count >= MaximumFiles || info.Length > MaximumBytes - journal.Bytes)
                                throw new InvalidOperationException("附件备份超过 256 MiB 或 10000 个文件上限，未执行删除。");
                            string copy = Path.Combine(journal.Root, journal.Files.Count.ToString("D6", CultureInfo.InvariantCulture) + ".payload");
                            using (FileStream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (FileStream output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                            SavedFile entry = new SavedFile { Source = Path.GetFullPath(file), Backup = copy, Hash = Hash(copy),
                                Length = info.Length, Attributes = info.Attributes, Modified = info.LastWriteTimeUtc };
                            if (Hash(file) != entry.Hash) throw new InvalidOperationException("附件备份时内容发生变化，未执行删除。");
                            journal.Files.Add(entry); journal.Bytes += entry.Length;
                        }
                    }
                }
                List<object> entries = new List<object>();
                foreach (SavedFile file in journal.Files) entries.Add(Map("source", file.Source, "backup", Path.GetFileName(file.Backup),
                    "sha256", file.Hash, "length", file.Length, "attributes", (int)file.Attributes, "modified_utc", file.Modified));
                File.WriteAllText(Path.Combine(journal.Root, "recovery-manifest.json"), JsonSerializer.Serialize(Map("format", 1,
                    "source_roots", journal.SourceRoots, "files", entries), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // A failed backup never touches source files. Keep any completed private
                // copies for inspection; no recursive deletion of a computed source path.
                throw new InvalidOperationException("附件恢复备份失败，未调用原生删除。部分备份位于：" + journal.Root);
            }
        }

        private static void RestoreNativeFiles(object logic, object transaction, Dictionary<Guid, object> attachments, Journal journal, List<string> errors)
        {
            if (transaction == null) return;
            object changes = Get(transaction, "____xx__xx_xx__x_xxx__x_");
            if (changes == null) return;
            IEnumerable undo = Get(changes, "_x_x__xx___x__x__x___xxx") as IEnumerable;
            if (undo == null) return;
            object core = Get(logic, "___x_xx___x_xx_x__x_xxx"), blob = Get(core, "__xxxx__xx__xxxx_xxxxx_x");
            object nativeContext = Get(transaction, "__xxxx___x___x_x_x__xxx_");
            HashSet<Guid> restored = new HashSet<Guid>();
            foreach (object operation in undo)
            {
                if (Convert.ToString(Get(operation, "_x_x_xx__x__xxxxxxx_xx__"), CultureInfo.InvariantCulture) != "RestoreAttachmentFromTrash") continue;
                try
                {
                    Guid id;
                    using (JsonDocument document = JsonDocument.Parse((string)Get(operation, "_xxxx___x___xx___xx_xxx_")))
                        id = Guid.Parse(document.RootElement.GetProperty("Id").GetString());
                    object attachment;
                    if (!attachments.TryGetValue(id, out attachment)) { errors.Add("恢复记录没有匹配的预检附件：" + id); continue; }
                    object resolver = Get(blob, "__xx__xx_xxx_x_x_xxx____");
                    object pathResolver = Call(resolver, "_xx_x_x_x_xx_x___x_x_x", new object[] { nativeContext });
                    string source = Path.GetFullPath((string)Call(pathResolver, "__x__xxxx___x_x__x_x_x__", new object[] { attachment }));
                    if (!Owned(journal, source)) { errors.Add("原生恢复目标不在本次已备份目录内：" + id); continue; }
                    if (File.Exists(source) || Directory.Exists(source) || !restored.Add(id)) continue;
                    SafeAncestors(source, journal);
                    object trash = Get(operation, "______xxxx_x_xxxxx__x___");
                    if (!(trash is Guid)) { errors.Add("原生恢复记录缺少 Trash UUID：" + id); continue; }
                    object result = Call(blob, "____xxx__x_x_x____xx_xx_", new object[] { nativeContext, attachment, trash });
                    if (!NativeSuccess(result)) errors.AddRange(NativeErrors(result));
                }
                catch (Exception error) { errors.Add(Unwrap(error).Message); }
            }
        }

        private static void RestoreJournal(Journal journal)
        {
            foreach (SavedDirectory directory in journal.Directories)
            {
                try { SafeAncestors(directory.Source, journal); if (!Directory.Exists(directory.Source)) Directory.CreateDirectory(directory.Source); }
                catch { }
            }
            foreach (SavedFile file in journal.Files)
            {
                try
                {
                    SafeAncestors(file.Source, journal);
                    if (File.Exists(file.Source) || Directory.Exists(file.Source)) continue;
                    if (Hash(file.Backup) != file.Hash) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Source));
                    File.Copy(file.Backup, file.Source, false);
                    File.SetLastWriteTimeUtc(file.Source, file.Modified);
                    File.SetAttributes(file.Source, file.Attributes);
                }
                catch { }
            }
        }

        private static List<object> Verify(Journal journal)
        {
            List<object> changed = new List<object>();
            foreach (SavedFile file in journal.Files)
            {
                string reason = null;
                try
                {
                    SafeAncestors(file.Source, journal);
                    if (!File.Exists(file.Source)) reason = "missing";
                    else if (new FileInfo(file.Source).Length != file.Length || Hash(file.Source) != file.Hash) reason = "content_changed";
                    else if (File.GetAttributes(file.Source) != file.Attributes) reason = "attributes_changed";
                }
                catch { reason = "unreadable_or_unsafe"; }
                if (reason != null) changed.Add(Map("path", file.Source, "reason", reason));
            }
            foreach (SavedDirectory directory in journal.Directories)
            {
                try
                {
                    SafeAncestors(directory.Source, journal);
                    if (!Directory.Exists(directory.Source)) changed.Add(Map("path", directory.Source, "reason", "directory_missing"));
                }
                catch { changed.Add(Map("path", directory.Source, "reason", "directory_unsafe")); }
            }
            return changed;
        }

        private static int ProbeCommit(object logic, Guid brain, List<Guid> ids)
        {
            try
            {
                object result = Call(logic, "_x__xx_xx_xx_x__x____xxx", new object[] { brain, new HashSet<Guid>(ids), true });
                if (!NativeSuccess(result)) return 0;
                IEnumerable thoughts = Get(result, "Thoughts") as IEnumerable;
                if (thoughts == null) return 0;
                HashSet<Guid> found = new HashSet<Guid>();
                foreach (object thought in thoughts) found.Add((Guid)Get(thought, "Id"));
                int present = 0; foreach (Guid id in ids) if (found.Contains(id)) present++;
                return present == 0 ? 1 : present == ids.Count ? -1 : 0;
            }
            catch { return 0; }
        }

        private static bool Owned(Journal journal, string path)
        {
            foreach (string root in journal.SourceRoots) if (Within(path, root)) return true;
            return false;
        }
        private static bool Within(string path, string root)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return String.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static void SafeAncestors(string path, Journal journal)
        {
            if (!Owned(journal, path)) throw new InvalidOperationException("附件恢复路径超出本次备份目录。");
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("附件恢复路径含重解析点。");
                current = Path.GetDirectoryName(current);
            }
        }
        private static void Cleanup(Journal journal)
        {
            string root = Path.GetFullPath(journal.Root), manifest = Path.Combine(root, "recovery-manifest.json");
            foreach (SavedFile file in journal.Files)
            {
                if (!Within(file.Backup, root) || Path.GetDirectoryName(file.Backup) != root)
                    throw new InvalidOperationException("恢复备份清理路径不匹配。");
                File.Delete(file.Backup);
            }
            File.Delete(manifest);
            Directory.Delete(root, false);
        }
        private static string Hash(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 hash = SHA256.Create()) return Convert.ToHexString(hash.ComputeHash(stream));
        }
        private static void FailCleanup(Dictionary<string, object> receipt, string prefix, Exception error)
        {
            receipt["success"] = false; receipt["code"] = "native_cleanup_error";
            receipt["error"] = prefix + Unwrap(error).Message;
        }
        private static bool NativeSuccess(object result)
        {
            return result != null && Convert.ToBoolean(Call(result, "_xxx___x_x__x_x__x_x__x_", new object[0]), CultureInfo.InvariantCulture);
        }
        private static List<string> NativeErrors(object result)
        {
            List<string> values = new List<string>();
            IEnumerable errors = result == null ? null : Get(result, "Errors") as IEnumerable;
            if (errors != null) foreach (object error in errors) values.Add(Convert.ToString(error, CultureInfo.InvariantCulture));
            return values;
        }
        private static object Get(object target, string name)
        {
            if (target == null) return null;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }
        private static object Call(object target, string name, object[] args)
        {
            if (target == null) throw new InvalidOperationException("原生业务对象缺失。");
            MethodInfo found = null;
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != name || parameters.Length != args.Length || method.ContainsGenericParameters) continue;
                bool matches = true;
                for (int i = 0; i < args.Length; i++)
                    if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i])) matches = false;
                if (!matches) continue;
                if (found != null) throw new AmbiguousMatchException(name);
                found = method;
            }
            if (found == null) throw new MissingMethodException(target.GetType().Name, name);
            try { return found.Invoke(target, args); }
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
