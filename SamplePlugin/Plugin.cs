using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PartyBlackjack;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Party Blackjack";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    private readonly BlackjackTable table;
    private readonly Configuration config;
    private readonly WindowSystem windowSystem = new("PartyBlackjackWindows");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        config = (PluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();
        config.Initialize(PluginInterface);

        table = new BlackjackTable(IsLocalPartyLeader, msg => ChatGui.SendMessage(XivChatType.Party, msg));

        mainWindow = new MainWindow(table, IsLocalPartyLeader, StartTableFromUi, DealFromUi, StopFromUi,
            () => config.AutoOpenUiWhenTableOpens, v => { config.AutoOpenUiWhenTableOpens = v; config.Save(); });

        windowSystem.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleUI;

        CommandManager.AddHandler("/bj", new CommandInfo(OnBjCommand)
        {
            HelpMessage = "Host: /bj start/deal/stop/ui/status\n" +
                          "Players: !bj join/bet<amt>/hit/stand/double/leave (party chat)\n" +
                          "REAL GIL: Announce bet, trade gil to host pre-deal. Double: trade addl gil.\n" +
                          "Host trades payouts post-round."
        });

        ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler("/bj");
        windowSystem.RemoveAllWindows();
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleUI;
    }

    private void OnBjCommand(string command, string args)
    {
        var argTokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (argTokens.Length == 0)
        {
            ToggleUI();
            return;
        }

        var sub = argTokens[0].ToLowerInvariant();

        switch (sub)
        {
            case "ui":
                ToggleUI();
                break;
            case "start":
                StartTableFromUi();
                break;
            case "deal":
                DealFromUi();
                break;
            case "stop":
                StopFromUi();
                break;
            case "status":
                table.CopyPublicMessageAsPartyCommandToClipboard();
                ChatGui.Print("Status copied to clipboard (/p command).");
                break;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != XivChatType.Party) return;
        if (!table.TableOpen) return;
        if (!IsLocalPartyLeader()) return;

        string? senderName = null;
        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload pp)
            {
                senderName = pp.PlayerName;
                break;
            }
        }
        if (string.IsNullOrEmpty(senderName)) return;

        var msgText = message.TextValue.Trim().ToLowerInvariant();
        if (!msgText.StartsWith("!bj ")) return;

        var cmd = msgText[4..].Trim();
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0) return;

        var subCmd = tokens[0];

        bool handled = subCmd switch
        {
            "join" => table.Join(senderName),
            "leave" => table.Leave(senderName),
            "bet" => table.Bet(senderName, tokens.Length > 1 ? tokens[1] : null),
            "hit" => table.Hit(senderName),
            "stand" => table.Stand(senderName),
            "double" => table.Double(senderName),
            _ => false
        };

        if (handled)
            isHandled = true;
    }

    private bool IsLocalPartyLeader()
    {
        if (ClientState.LocalPlayer == null) return false;
        if (PartyList.Length == 0) return true;
        return PartyList[0].Name == ClientState.LocalPlayer.Name;
    }

    private void StartTableFromUi() => table.OpenTableAndDeal();
    private void DealFromUi() => table.DealNextRound();
    private void StopFromUi() => table.CloseTable();

    private void DrawUI() => windowSystem.Draw();
    private void ToggleUI() => mainWindow.Toggle();
}
