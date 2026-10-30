namespace DotNetVisualizer.Web.Services;

/// <summary>
/// An API whose use deserves a second look in a security review. These are leads, not vulnerabilities: most uses are fine,
/// which is why each rule says what to check.
/// </summary>
public sealed record RiskyApiRule(string Id, string Category, string Severity, string Title, string Why, string Advice);

public static class RiskyApiRules
{
    private const string AnyMethod = "*";

    // (type full name, method name or "*") -> rule
    private static readonly Dictionary<(string Type, string Method), RiskyApiRule> Rules = Build();

    public static IReadOnlyCollection<RiskyApiRule> All => Rules.Values.Distinct().ToList();

    /// <summary>The rule for a call to <paramref name="typeFullName"/>.<paramref name="method"/>, or null.</summary>
    public static RiskyApiRule? Match(string typeFullName, string method)
    {
        if (Rules.TryGetValue((typeFullName, method), out var exact)) return exact;
        return Rules.TryGetValue((typeFullName, AnyMethod), out var any) ? any : null;
    }

    /// <summary>The rule for a P/Invoke declaration (a managed method that is implemented by a native library).</summary>
    public static readonly RiskyApiRule NativeCall = new("native-call", "Native interop", "Low", "Calls native code (DllImport / P/Invoke)",
        "Native code is outside the runtime's memory and type safety, is platform specific and is not covered by the managed security model.",
        "Check which library is loaded and from where (use a full path or a safe search path), validate every buffer length and string passed across, and prefer a managed API where one exists.");

    private static Dictionary<(string, string), RiskyApiRule> Build()
    {
        var d = new Dictionary<(string, string), RiskyApiRule>();
        void Add(RiskyApiRule rule, string type, params string[] methods)
        {
            foreach (var m in methods.Length == 0 ? new[] { AnyMethod } : methods) d[(type, m)] = rule;
        }

        var insecureDeserialization = (string name) => new RiskyApiRule($"deserialize-{name.ToLowerInvariant()}", "Insecure deserialization", "High",
            $"Uses {name} (unsafe deserializer)",
            $"{name} can instantiate arbitrary types from the data it reads, so deserializing untrusted input can lead to remote code execution. Microsoft marks the binary formatters as obsolete and unsafe.",
            "Do not deserialize untrusted data with it. Use System.Text.Json or DataContractSerializer with known types, and treat any data from outside the process as untrusted.");

        Add(insecureDeserialization("BinaryFormatter"), "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter");
        Add(insecureDeserialization("SoapFormatter"), "System.Runtime.Serialization.Formatters.Soap.SoapFormatter");
        Add(insecureDeserialization("NetDataContractSerializer"), "System.Runtime.Serialization.NetDataContractSerializer");
        Add(insecureDeserialization("LosFormatter"), "System.Web.UI.LosFormatter");
        Add(insecureDeserialization("ObjectStateFormatter"), "System.Web.UI.ObjectStateFormatter");

        Add(new RiskyApiRule("process-start", "Process execution", "Medium", "Starts another process (Process.Start)",
                "Starting a process from text that includes user input enables command injection, and a relative file name can launch a planted program.",
                "Pass arguments through ArgumentList (not one concatenated string), use a full path, never build the command from untrusted input, and set UseShellExecute = false."),
            "System.Diagnostics.Process", "Start");

        var load = new RiskyApiRule("assembly-load", "Dynamic code loading", "Medium", "Loads an assembly at run time (Assembly.Load*)",
            "Loading code from a path or bytes that an attacker can influence runs that code inside the process (plugin / DLL planting risk).",
            "Load only from a fixed, access-controlled location, verify strong name or signature first, and avoid loading from user-writable folders or from network data.");
        Add(load, "System.Reflection.Assembly", "Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom", "LoadWithPartialName");
        Add(load, "System.Activator", "CreateInstanceFrom");
        Add(load, "System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyPath", "LoadFromStream", "LoadFromNativeImagePath", "LoadFromAssemblyName");

        var md5 = WeakHash("MD5", "Low", "MD5 is broken for collision resistance. Fine for checksums that are not security relevant, wrong for signatures, passwords or integrity against an attacker.");
        var sha1 = WeakHash("SHA1", "Low", "SHA-1 has practical collision attacks. Fine for non-security checksums, wrong for signatures or integrity against an attacker.");
        Add(md5, "System.Security.Cryptography.MD5", "Create", "HashData", ".ctor");
        Add(md5, "System.Security.Cryptography.MD5CryptoServiceProvider");
        Add(sha1, "System.Security.Cryptography.SHA1", "Create", "HashData", ".ctor");
        Add(sha1, "System.Security.Cryptography.SHA1Managed");
        Add(sha1, "System.Security.Cryptography.SHA1CryptoServiceProvider");
        Add(sha1, "System.Security.Cryptography.SHA1Cng");

        var weakCipher = (string name, string why) => new RiskyApiRule($"weak-cipher-{name.ToLowerInvariant()}", "Weak cryptography", "Medium", $"Uses {name} (weak cipher)", why,
            "Use AES (AesGcm or Aes with a random IV and a MAC). Never reuse keys or IVs, and prefer authenticated encryption.");
        var des = weakCipher("DES", "DES has a 56-bit key and can be brute-forced.");
        var tdes = weakCipher("TripleDES", "Triple DES has a 64-bit block size (Sweet32 attack) and is deprecated.");
        var rc2 = weakCipher("RC2", "RC2 is obsolete and has known weaknesses with its default key sizes.");
        Add(des, "System.Security.Cryptography.DES", "Create", ".ctor");
        Add(des, "System.Security.Cryptography.DESCryptoServiceProvider");
        Add(tdes, "System.Security.Cryptography.TripleDES", "Create", ".ctor");
        Add(tdes, "System.Security.Cryptography.TripleDESCryptoServiceProvider");
        Add(rc2, "System.Security.Cryptography.RC2", "Create", ".ctor");
        Add(rc2, "System.Security.Cryptography.RC2CryptoServiceProvider");

        Add(new RiskyApiRule("tls-validation", "Insecure TLS", "Medium", "Sets a custom certificate validation callback",
                "A callback that always returns true turns off certificate checks and allows man-in-the-middle attacks. Many are legitimate (pinning); the risky ones accept everything.",
                "Read the callback: it should reject untrusted certificates. Remove any 'always true' callback outside tests."),
            "System.Net.ServicePointManager", "set_ServerCertificateValidationCallback");
        Add(new RiskyApiRule("tls-validation", "Insecure TLS", "Medium", "Sets a custom certificate validation callback",
                "A callback that always returns true turns off certificate checks and allows man-in-the-middle attacks. Many are legitimate (pinning); the risky ones accept everything.",
                "Read the callback: it should reject untrusted certificates. Remove any 'always true' callback outside tests."),
            "System.Net.Http.HttpClientHandler", "set_ServerCertificateCustomValidationCallback");

        return d;
    }

    private static RiskyApiRule WeakHash(string name, string severity, string why) => new($"weak-hash-{name.ToLowerInvariant()}", "Weak cryptography", severity,
        $"Uses {name} (weak hash)", why, "Use SHA-256 or stronger (SHA256.HashData), and a password hashing function (PBKDF2, Argon2, bcrypt) for passwords.");
}
