using System.Management.Automation;
using DotNetVisualizer.Core.Checks;

namespace Dav
{
    /// <summary>A check found something at or above the risk threshold. The findings travel with the error (<see cref="ErrorRecord.TargetObject"/>).</summary>
    public sealed class DavCheckFailedException : Exception
    {
        public DavCheckFailedException(string message) : base(message) { }
    }

    /// <summary>What the cmdlets share: path handling, the threshold, the configuration file and the way a failed check is reported.</summary>
    public abstract class DavCmdlet : PSCmdlet
    {
        protected const string ThresholdHelp = "The lowest risk level that makes the command fail: low (default, everything fails), medium or high.";

        [Parameter(HelpMessage = "Also end the PowerShell process with exit code 1 when the check fails. For scripts started with pwsh -File that do not set $ErrorActionPreference = 'Stop'.")]
        public SwitchParameter ExitOnFailure { get; set; }

        private CancellationTokenSource? _cancel;
        protected CancellationToken Cancellation => (_cancel ??= new CancellationTokenSource()).Token;

        protected override void StopProcessing() => (_cancel ??= new CancellationTokenSource()).Cancel();

        protected string ResolveFile(string path) => GetUnresolvedProviderPathFromPSPath(path);

        protected RiskLevel Threshold(string? text)
        {
            if (string.IsNullOrEmpty(text)) return RiskLevel.Low;
            try { return RiskThreshold.Parse(text); }
            catch (ArgumentException ex)
            {
                Terminate(new ErrorRecord(ex, "InvalidRiskThreshold", ErrorCategory.InvalidArgument, text));
                throw;
            }
        }

        /// <summary>The explicit file, else a dav.json in the current directory, else nothing is accepted.</summary>
        protected DavConfig LoadConfig(string? configPath)
        {
            string? path = configPath != null ? ResolveFile(configPath) : null;
            if (path == null)
            {
                var candidate = Path.Combine(SessionState.Path.CurrentFileSystemLocation.ProviderPath, DavConfig.FileName);
                if (File.Exists(candidate)) path = candidate;
            }
            if (path == null) return DavConfig.Empty;
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException($"Configuration file not found: {path}", path);
                return DavConfig.Load(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Terminate(new ErrorRecord(ex, "InvalidDavConfig", ErrorCategory.InvalidData, path));
                throw;
            }
        }

        /// <summary>An unreadable input (missing file, not a .NET assembly) is an error of its own, not a failed check.</summary>
        protected T Read<T>(string path, Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
            {
                var category = ex is FileNotFoundException ? ErrorCategory.ObjectNotFound : ErrorCategory.ReadError;
                Terminate(new ErrorRecord(ex, "UnreadableAssembly", category, path));
                throw;
            }
        }

        /// <summary>Ends the command with an error; with -ExitOnFailure the process also ends with exit code 1.</summary>
        protected void Terminate(ErrorRecord record)
        {
            if (ExitOnFailure)
            {
                // a statement-terminating error does not stop a script that runs under pwsh -File; this does
                Host.UI.WriteErrorLine(record.Exception.Message);
                Host.SetShouldExit(1);
            }
            ThrowTerminatingError(record);
        }

        /// <summary>A check found something at or above the threshold: the command ends with an error that carries the findings.</summary>
        protected void Fail(string message, object? findings) =>
            Terminate(new ErrorRecord(new DavCheckFailedException(message), "DavCheckFailed", ErrorCategory.InvalidResult, findings));
    }
}
