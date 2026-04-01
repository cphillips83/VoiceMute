using Tmds.DBus.Protocol;

namespace VoiceMute;

static class GlobalShortcut
{
    const string PortalBus = "org.freedesktop.portal.Desktop";
    const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    static readonly ObjectPath PortalObjectPath = new("/org/freedesktop/portal/desktop");

    public static async Task RunAsync(
        Action onActivated,
        Action onDeactivated,
        CancellationToken ct)
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();

        var senderName = connection.UniqueName!.Replace(":", "").Replace(".", "_");
        var sessionToken = $"voicemute_{Environment.ProcessId}";
        var sessionPath = new ObjectPath($"/org/freedesktop/portal/desktop/session/{senderName}/{sessionToken}");

        Console.WriteLine("[Shortcut] Connecting to GlobalShortcuts portal...");

        // Subscribe to Activated signal (key press)
        await connection.AddMatchAsync<string>(
            new MatchRule { Interface = ShortcutsInterface, Member = "Activated", Type = MessageType.Signal },
            static (Message msg, object? _) => "activated",
            (Exception? ex, string value, object? s1, object? s2) =>
            {
                if (ex == null) onActivated();
            },
            ObserverFlags.None, null, null);

        // Subscribe to Deactivated signal (key release)
        await connection.AddMatchAsync<string>(
            new MatchRule { Interface = ShortcutsInterface, Member = "Deactivated", Type = MessageType.Signal },
            static (Message msg, object? _) => "deactivated",
            (Exception? ex, string value, object? s1, object? s2) =>
            {
                if (ex == null) onDeactivated();
            },
            ObserverFlags.None, null, null);

        // CreateSession: signature "a{sv}"
        Console.WriteLine("[Shortcut] Creating session...");
        {
            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalBus,
                path: PortalObjectPath,
                @interface: ShortcutsInterface,
                member: "CreateSession",
                signature: "a{sv}");

            // options dict: a{sv}
            var dict = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariantString($"req_{sessionToken}");
            writer.WriteDictionaryEntryStart();
            writer.WriteString("session_handle_token");
            writer.WriteVariantString(sessionToken);
            writer.WriteDictionaryEnd(dict);

            await connection.CallMethodAsync(writer.CreateMessage());
        }

        await Task.Delay(500, ct);

        // BindShortcuts: signature "oa(sa{sv})sa{sv}"
        Console.WriteLine("[Shortcut] Binding Right Ctrl shortcut...");
        {
            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalBus,
                path: PortalObjectPath,
                @interface: ShortcutsInterface,
                member: "BindShortcuts",
                signature: "oa(sa{sv})sa{sv}");

            // session path
            writer.WriteObjectPath(sessionPath);

            // shortcuts: a(sa{sv})
            var arr = writer.WriteArrayStart(DBusType.Struct);

            // First shortcut entry: (sa{sv})
            writer.WriteStructureStart();
            writer.WriteString("voicemute-ptt");

            var innerDict = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("description");
            writer.WriteVariantString("VoiceMute Push-to-Talk");
            writer.WriteDictionaryEntryStart();
            writer.WriteString("preferred_trigger");
            writer.WriteVariantString("Control_R");
            writer.WriteDictionaryEnd(innerDict);

            writer.WriteArrayEnd(arr);

            // parent window
            writer.WriteString("");

            // options: a{sv}
            var opts = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariantString($"bind_{sessionToken}");
            writer.WriteDictionaryEnd(opts);

            await connection.CallMethodAsync(writer.CreateMessage());
        }

        Console.WriteLine("[Shortcut] Registered! Press Right Ctrl to activate.");
        Console.WriteLine("[Shortcut] A system dialog may appear to approve the shortcut.");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
    }
}
