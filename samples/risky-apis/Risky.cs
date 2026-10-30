using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace Samples.RiskyApis
{
    // ---------------------------------------------------------------------------------------------------------------------
    // SAMPLE: risky APIs  -  open this assembly and look at the "Risky APIs" view.
    //
    // WHAT THE VIEW REPORTS HERE (all are HEURISTIC leads - the use of an API says nothing about whether it is exploitable;
    // the view says what to check). Calling method -> rule:
    //   Deserialization.Load            BinaryFormatter                       High    Insecure deserialization
    //   Launcher.Run                    Process.Start                         Medium  Process execution
    //   PluginHost.Load                 Assembly.LoadFrom                     Medium  Dynamic code loading
    //   Hashing.Checksum                MD5.HashData                          Low     Weak cryptography (fine for checksums)
    //   Hashing.Legacy                  SHA1.HashData                         Low     Weak cryptography
    //   Encryption.Old                  DES.Create                            Medium  Weak cipher
    //   Network.TrustEverything         ServicePointManager callback setter   Medium  Insecure TLS (this one accepts every certificate)
    //   Native.GetTickCount             [DllImport("kernel32.dll")]           Low     Native interop
    //   Reflective.Call                 MethodBase.Invoke                     Low     Reflection (the call graph has NO edge for it)
    //   Reflective.Create               Activator.CreateInstance(Type)        Low     Reflection
    //   Reflective.Lookup               Type.GetType(string)                  Low     Reflection
    //   CodeGeneration.Emit             new DynamicMethod(...)                Medium  Dynamic code generation
    //   CodeGeneration.Compile          Expression.Compile()                  Medium  Dynamic code generation
    // NEGATIVE CONTROL - SafeCode must produce NO finding: SHA256.HashData, Aes, a harmless Process property read.
    // ---------------------------------------------------------------------------------------------------------------------

    public class Deserialization
    {
        public object Load(System.IO.Stream data) => new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(data);
    }

    public class Launcher
    {
        public void Run(string tool) { Process.Start(tool); }
    }

    public class PluginHost
    {
        public Assembly Load(string path) => Assembly.LoadFrom(path);
    }

    public class Hashing
    {
        public byte[] Checksum(byte[] data) => MD5.HashData(data);
        public byte[] Legacy(byte[] data) => SHA1.HashData(data);
    }

    public class Encryption
    {
        public SymmetricAlgorithm Old() => DES.Create();
    }

    public class Network
    {
        public void TrustEverything() { ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true; }
    }

    public static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();
    }

    public class Reflective
    {
        public object Call(MethodInfo method, object target) => method.Invoke(target, null);
        public object Create(Type type) => Activator.CreateInstance(type);
        public Type Lookup(string name) => Type.GetType(name);
    }

    public class CodeGeneration
    {
        public DynamicMethod Emit() => new DynamicMethod("generated", typeof(int), Type.EmptyTypes);
        public int Compile(Expression<Func<int>> expression) => expression.Compile()();
    }

    public class SafeCode
    {
        public byte[] Hash(byte[] data) => SHA256.HashData(data);
        public int ProcessId() => Process.GetCurrentProcess().Id;
        public Aes Cipher() => Aes.Create();
    }
}
