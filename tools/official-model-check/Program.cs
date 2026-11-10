// Read BRZ records using the official data models, without starting the application.
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

if (args.Length < 2) {
    Console.Error.WriteLine("Usage: OfficialModelCheck <official-app-directory> <input.brz> [input2.brz ...]");
    return 2;
}
var expected = new Dictionary<string, string> {
    ["TheBrainCore.dll"] = "70356c4e3c243b8072d839754e69d34346803dec4a3c38dca336d2fb0a50d2fa",
    ["Newtonsoft.Json.dll"] = "22c649f75fce5be7c7ccda8880473b634ef69ecf33f5d1ab8ad892caf47d5a07",
    ["TheBrain.Sys.dll"] = "a82871e67b106a6d0cd607ff75850168b6f744c9884e5020668b3b200be3c072",
};
var appDir = Path.GetFullPath(args[0]);
foreach (var pair in expected) {
    var path = Path.Combine(appDir, pair.Key);
    if (!File.Exists(path) || Hash(path) != pair.Value) {
        Console.Error.WriteLine($"Official assembly missing or SHA-256 mismatch: {pair.Key}");
        return 2;
    }
}
// Only this hash-checked build's model names are supported.
var prefix = "TheBrain._xxx_____xx_x_x_xx__x.__xxx_xx_x____x_xxxx__x_.";
var types = new Dictionary<string, string> {
    ["thoughts.json"] = prefix + "_x__x_xx____xx_xxxxx_",
    ["links.json"] = prefix + "_xx___xx___xx___x__x__xx",
    ["attachments.json"] = prefix + "___x_xx__x_x__xxx_x__x_x",
    ["fielddefinitions.json"] = prefix + "_x_______x_xxx___x___x_",
    ["fieldinstances.json"] = prefix + "_xx_xx__x____x_x_xx__x_x",
};
AssemblyLoadContext.Default.Resolving += (context, name) => {
    var path = Path.Combine(appDir, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var jsonAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "Newtonsoft.Json.dll"));
var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "TheBrainCore.dll"));
var convert = jsonAssembly.GetType("Newtonsoft.Json.JsonConvert", true)!;
var deserialize = convert.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) })!;
var serialize = convert.GetMethod("SerializeObject", new[] { typeof(object) })!;
var files = new List<object>();
var failures = new List<object>();
var changes = new List<object>();
var normalizations = new List<object>();
foreach (var input in args.Skip(1)) {
    var name = Path.GetFileName(input);
    var counts = new Dictionary<string, int>();
    using var zip = ZipFile.OpenRead(input);
    foreach (var pair in types) {
        var entry = zip.GetEntry(pair.Key);
        if (entry == null) continue;
        var type = core.GetType(pair.Value, true)!;
        using var stream = new StreamReader(entry.Open());
        string? line;
        int lineNumber = 0, count = 0;
        while ((line = stream.ReadLine()) != null) {
            lineNumber++;
            if (String.IsNullOrWhiteSpace(line)) continue;
            try {
                var entity = deserialize.Invoke(null, new object[] { line, type });
                var serialized = (string)serialize.Invoke(null, new[] { entity })!;
                using var original = JsonDocument.Parse(line);
                using var result = JsonDocument.Parse(serialized);
                var id = original.RootElement.GetProperty("Id").GetString();
                foreach (var field in original.RootElement.EnumerateObject()) {
                    if (!result.RootElement.TryGetProperty(field.Name, out var after)) {
                        changes.Add(new { file = name, table = pair.Key, id, field = field.Name, error = "original field omitted" });
                    } else if (!Equivalent(field.Value, after)) {
                        if (SameTimestamp(field.Name, field.Value, after)) {
                            normalizations.Add(new { file = name, table = pair.Key, id, field = field.Name,
                                before = field.Value.GetString(), after = after.GetString() });
                        } else {
                            changes.Add(new { file = name, table = pair.Key, id, field = field.Name,
                                before = field.Value.Clone(), after = after.Clone() });
                        }
                    }
                }
                count++;
            } catch (Exception error) {
                var cause = error is TargetInvocationException ? error.InnerException! : error;
                failures.Add(new { file = name, table = pair.Key, line = lineNumber,
                    error = cause.GetType().Name, message = cause.Message });
                break;
            }
        }
        counts[pair.Key] = count;
    }
    files.Add(new { file = name, sha256 = Hash(input), records = counts.Values.Sum(), tables = counts });
}
var report = new {
    kind = "official_model_json_roundtrip",
    runtime = RuntimeInformation.FrameworkDescription,
    core_version = core.GetName().Version!.ToString(),
    json_version = jsonAssembly.GetName().Version!.ToString(),
    assembly_sha256 = expected, files, failures,
    semantic_differences = changes, timestamp_format_normalizations = normalizations,
    archive_import_verified = false, application_import_verified = false,
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions {
    WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));
return failures.Count > 0 || changes.Count > 0 ? 1 : 0;

static string Hash(string path) {
    using var input = File.OpenRead(path);
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
}
static bool Equivalent(JsonElement a, JsonElement b) {
    if (a.ValueKind != b.ValueKind) return false;
    return a.ValueKind switch {
        JsonValueKind.Null => true,
        JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(),
        JsonValueKind.String => a.GetString() == b.GetString(),
        JsonValueKind.Number => a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y)
            ? x == y : a.GetRawText() == b.GetRawText(),
        JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
            && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equivalent(p.First, p.Second)),
        JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
            && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var value) && Equivalent(p.Value, value)),
        _ => a.GetRawText() == b.GetRawText(),
    };
}
static bool SameTimestamp(string field, JsonElement before, JsonElement after) {
    if (!field.EndsWith("DateTime", StringComparison.Ordinal)
        || before.ValueKind != JsonValueKind.String || after.ValueKind != JsonValueKind.String) return false;
    return DateTimeOffset.TryParse(before.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var x)
        && DateTimeOffset.TryParse(after.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var y)
        && x.UtcDateTime.Ticks == y.UtcDateTime.Ticks;
}
