using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Scour.Elevator;
using Scour.Ipc;

// Scour.Elevator: the only part of Scour that runs as admin. It serves one client (the Scour
// process that launched it) over a named pipe only that user can open, and exits when the
// pipe closes or after five idle minutes.

var options = Arguments.Parse(args);
if (options is null)
    return 2;

var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(options.ClientSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
using (var self = WindowsIdentity.GetCurrent())
    security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

await using var pipe = NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
{
    try
    {
        await pipe.WaitForConnectionAsync(connectTimeout.Token);
    }
    catch (OperationCanceledException)
    {
        return 3;
    }
}

if (!ClientCheck.IsExpectedClient(pipe, options.ParentPid))
    return 4;

var handler = new RequestHandler(pipe);
var idle = TimeSpan.FromMinutes(5);
while (pipe.IsConnected)
{
    using var idleTimeout = new CancellationTokenSource(idle);
    (IpcMessage? Message, IReadOnlyList<EntryRecord>? Entries)? frame;
    try
    {
        frame = await PipeProtocol.ReadAsync(pipe, idleTimeout.Token);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException)
    {
        break; // Idle, closed, or garbage: either way, stop.
    }

    if (frame?.Message is null or ShutdownRequest)
        break;
    if (!await handler.HandleAsync(frame.Value.Message))
        break;
}
return 0;

namespace Scour.Elevator
{
    internal sealed partial record Arguments(string PipeName, int ParentPid, string ClientSid)
    {
        [GeneratedRegex("^scour-elevator-[0-9a-f]{32}$")]
        private static partial Regex PipeNamePattern();

        [GeneratedRegex(@"^S-1-5-21(-\d+){4}$")]
        private static partial Regex SidPattern();

        public static Arguments? Parse(string[] args)
        {
            string? pipe = null, sid = null;
            var parent = 0;
            for (var i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--pipe": pipe = args[i + 1]; break;
                    case "--parent": int.TryParse(args[i + 1], out parent); break;
                    case "--client-sid": sid = args[i + 1]; break;
                }
            }
            return pipe is not null && PipeNamePattern().IsMatch(pipe) && sid is not null && SidPattern().IsMatch(sid) && parent > 0
                ? new Arguments(pipe, parent, sid)
                : null;
        }
    }

    internal static partial class ClientCheck
    {
        /// <summary>
        /// The process on the other end must be the one that launched us, and must be a Scour
        /// executable from our own folder: not just any process running as this user.
        /// </summary>
        public static bool IsExpectedClient(NamedPipeServerStream pipe, int expectedPid)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != expectedPid)
                return false;
            try
            {
                using var client = Process.GetProcessById((int)pid);
                var image = client.MainModule?.FileName;
                return image is not null
                    && string.Equals(Path.GetDirectoryName(image)?.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }
}
